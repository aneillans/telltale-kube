using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Desktop.Platform;

/// <summary>Windows: master key wrapped with DPAPI (CurrentUser scope, bound to the user's logon credentials).</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class DpapiKeyProtector(string directory) : IKeyProtector
{
    private const int CryptprotectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "TelltaleKube.Vault.v1"u8.ToArray();
    private readonly string _path = Path.Combine(directory, "vault.key.dpapi");

    public string Description => "Windows Data Protection API (DPAPI, current user)";
    public bool IsStrong => true;

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct)
    {
        if (File.Exists(_path))
        {
            return Unprotect(await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false));
        }
        var key = RandomNumberGenerator.GetBytes(32);
        await File.WriteAllBytesAsync(_path, Protect(key), ct).ConfigureAwait(false);
        return key;
    }

    public Task DeleteKeyAsync(CancellationToken ct)
    {
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static unsafe byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* pData = data)
        fixed (byte* pEntropy = Entropy)
        {
            var input = new DataBlob { cbData = data.Length, pbData = (nint)pData };
            var entropy = new DataBlob { cbData = Entropy.Length, pbData = (nint)pEntropy };
            DataBlob output = default;
            var ok = protect
                ? CryptProtectData(ref input, null, ref entropy, 0, 0, CryptprotectUiForbidden, ref output)
                : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, CryptprotectUiForbidden, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                new Span<byte>((void*)output.pbData, output.cbData).Clear();
                LocalFree(output.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public nint pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy, nint pvReserved, nint pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob pDataIn, nint ppszDataDescr, ref DataBlob pOptionalEntropy, nint pvReserved, nint pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint hMem);
}
