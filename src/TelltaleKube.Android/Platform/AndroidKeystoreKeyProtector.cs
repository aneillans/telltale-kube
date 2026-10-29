using System.Security.Cryptography;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Android;

/// <summary>
/// The vault master key is wrapped by a non-exportable AES-256-GCM key living in the Android Keystore
/// (StrongBox secure element when present, otherwise the TEE). The wrapped blob is useless off-device.
/// </summary>
internal sealed class AndroidKeystoreKeyProtector(string directory) : IKeyProtector
{
    private const string Provider = "AndroidKeyStore";
    private const string Alias = "telltale-kube-vault-wrap";
    private const string Transformation = "AES/GCM/NoPadding";
    private readonly string _path = Path.Combine(directory, "vault.key.wrapped");
    private bool _strongBox;

    public string Description => _strongBox ? "Android Keystore (StrongBox secure element)" : "Android Keystore (hardware-backed TEE)";
    public bool IsStrong => true;

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct)
    {
        var wrappingKey = GetOrCreateWrappingKey();
        if (File.Exists(_path))
        {
            var blob = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, wrappingKey, new GCMParameterSpec(128, blob[..12]));
            return cipher.DoFinal(blob[12..])!;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var enc = Cipher.GetInstance(Transformation)!;
        enc.Init(Javax.Crypto.CipherMode.EncryptMode, wrappingKey); // Keystore generates the IV
        var wrapped = enc.DoFinal(key)!;
        var iv = enc.GetIV()!;
        await File.WriteAllBytesAsync(_path, [.. iv, .. wrapped], ct).ConfigureAwait(false);
        return key;
    }

    public Task DeleteKeyAsync(CancellationToken ct)
    {
        if (File.Exists(_path)) File.Delete(_path);
        var ks = KeyStore.GetInstance(Provider)!;
        ks.Load(null);
        if (ks.ContainsAlias(Alias)) ks.DeleteEntry(Alias);
        return Task.CompletedTask;
    }

    private IKey GetOrCreateWrappingKey()
    {
        var ks = KeyStore.GetInstance(Provider)!;
        ks.Load(null);
        if (ks.GetKey(Alias, null) is { } existing) return existing;

        try
        {
            _strongBox = true;
            return Generate(strongBox: true);
        }
        catch (StrongBoxUnavailableException)
        {
            _strongBox = false;
            return Generate(strongBox: false);
        }
    }

    private static IKey Generate(bool strongBox)
    {
        var spec = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .SetRandomizedEncryptionRequired(true)!
            .SetIsStrongBoxBacked(strongBox)!
            .Build();
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(spec);
        return generator.GenerateKey()!;
    }
}
