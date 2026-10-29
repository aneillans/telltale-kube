using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelltaleKube.Core.Security;

/// <summary>
/// Protects the vault master key using the platform's secret store:
/// Windows DPAPI, macOS/iOS Keychain, Android Keystore (hardware-backed where available), Linux Secret Service.
/// </summary>
public interface IKeyProtector
{
    string Description { get; }

    /// <summary>False when the key is only protected by file permissions (no OS secret store available).</summary>
    bool IsStrong { get; }

    /// <summary>Returns the 256-bit master key, creating and storing it on first use.</summary>
    Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct);

    Task DeleteKeyAsync(CancellationToken ct);
}

/// <summary>
/// Encrypted local store for kubeconfigs, credentials and settings (envelope encryption: AES-256-GCM data,
/// master key held by <see cref="IKeyProtector"/>). Nothing secret is ever written to disk in plaintext.
/// </summary>
public sealed class SecretVault : IDisposable
{
    private static readonly byte[] Magic = "TTKV"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 5;

    private readonly string _path;
    private readonly IKeyProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VaultData? _data;

    public SecretVault(string directory, IKeyProtector protector)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "vault.bin");
        _protector = protector;
    }

    public IKeyProtector Protector => _protector;

    public async Task<VaultData> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_data is not null) return _data;
            if (!File.Exists(_path)) return _data = new VaultData();

            var blob = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            var key = await _protector.GetOrCreateKeyAsync(ct).ConfigureAwait(false);
            try
            {
                var json = Decrypt(blob, key);
                _data = JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultData) ?? new VaultData();
                CryptographicOperations.ZeroMemory(json);
                return _data;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = _data ?? new VaultData();
            var json = JsonSerializer.SerializeToUtf8Bytes(data, VaultJsonContext.Default.VaultData);
            var key = await _protector.GetOrCreateKeyAsync(ct).ConfigureAwait(false);
            byte[] blob;
            try
            {
                blob = Encrypt(json, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(json);
            }

            // Atomic replace so a crash never leaves a torn vault.
            var tmp = _path + ".tmp";
            await using (var fs = CreatePrivateFile(tmp))
            {
                await fs.WriteAsync(blob, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Destroys the vault and its key (crypto-shredding: even a recovered file is unreadable).</summary>
    public async Task WipeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _data = new VaultData();
            if (File.Exists(_path)) File.Delete(_path);
            await _protector.DeleteKeyAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] key)
    {
        var output = new byte[HeaderSize + NonceSize + TagSize + plaintext.Length];
        Magic.CopyTo(output, 0);
        output[4] = FormatVersion;
        var nonce = output.AsSpan(HeaderSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(HeaderSize + NonceSize + TagSize), output.AsSpan(HeaderSize + NonceSize, TagSize), output.AsSpan(0, HeaderSize));
        return output;
    }

    internal static byte[] Decrypt(ReadOnlySpan<byte> blob, byte[] key)
    {
        if (blob.Length < HeaderSize + NonceSize + TagSize || !blob[..4].SequenceEqual(Magic))
        {
            throw new CryptographicException("Vault file is corrupt or not a Telltale Kube vault.");
        }
        if (blob[4] != FormatVersion) throw new CryptographicException($"Unsupported vault format version {blob[4]}.");
        var plaintext = new byte[blob.Length - HeaderSize - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(blob.Slice(HeaderSize, NonceSize), blob[(HeaderSize + NonceSize + TagSize)..], blob.Slice(HeaderSize + NonceSize, TagSize), plaintext, blob[..HeaderSize]);
        return plaintext;
    }

    internal static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}

public sealed class VaultData
{
    public List<StoredCluster> Clusters { get; set; } = [];

    /// <summary>Fingerprints of exec credential plugins the user approved (see ExecCredentialSource).</summary>
    public List<string> ApprovedExecPlugins { get; set; } = [];

    public AppSettings Settings { get; set; } = new();
}

public sealed class StoredCluster
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string ContextName { get; set; } = "";
    public string KubeconfigYaml { get; set; } = "";
    public bool ReadOnly { get; set; }
    public bool AllowInsecureTls { get; set; }
    public string? DefaultNamespace { get; set; }
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";

    /// <summary>Lock the app (require OS authentication / re-open) after this many minutes in the background. 0 = never.</summary>
    public int AutoLockMinutes { get; set; } = 5;

    public bool ReadOnlyByDefault { get; set; }

    /// <summary>Desktop: also show contexts from ~/.kube/config (read live, never copied into the vault).</summary>
    public bool UseSystemKubeconfig { get; set; } = true;

    public bool AllowBasicAuth { get; set; }

    public bool CheckForUpdates { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VaultData))]
internal sealed partial class VaultJsonContext : JsonSerializerContext;

/// <summary>
/// Fallback when no OS secret store exists (e.g. headless Linux without a Secret Service). The key lives in a
/// user-only (0600) file. Reported as weak so the UI can warn.
/// </summary>
public sealed class FileKeyProtector(string directory) : IKeyProtector
{
    private readonly string _path = Path.Combine(directory, "vault.key");

    public string Description => "File protected by OS permissions (no secret store found)";
    public bool IsStrong => false;

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct)
    {
        if (File.Exists(_path))
        {
            var existing = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            if (existing.Length == 32) return existing;
            throw new CryptographicException("Vault key file is corrupt.");
        }
        Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(32);
        await using var fs = SecretVault.CreatePrivateFile(_path);
        await fs.WriteAsync(key, ct).ConfigureAwait(false);
        return key;
    }

    public Task DeleteKeyAsync(CancellationToken ct)
    {
        if (File.Exists(_path))
        {
            // Overwrite before delete (best effort; SSD wear levelling means this is not a guarantee).
            File.WriteAllBytes(_path, RandomNumberGenerator.GetBytes(32));
            File.Delete(_path);
        }
        return Task.CompletedTask;
    }
}

/// <summary>In-memory protector for tests and for the "don't remember anything" mode.</summary>
public sealed class EphemeralKeyProtector : IKeyProtector
{
    private byte[]? _key;
    public string Description => "In-memory only";
    public bool IsStrong => true;
    public Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct) => Task.FromResult((byte[])(_key ??= RandomNumberGenerator.GetBytes(32)).Clone());
    public Task DeleteKeyAsync(CancellationToken ct)
    {
        _key = null;
        return Task.CompletedTask;
    }
}
