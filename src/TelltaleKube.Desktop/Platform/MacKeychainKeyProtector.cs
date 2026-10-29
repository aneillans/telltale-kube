using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Desktop.Platform;

/// <summary>macOS: master key stored as a generic password in the user's login Keychain.</summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacKeychainKeyProtector : IKeyProtector
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;
    private static readonly byte[] Service = "uk.co.neillans.telltalekube.vault"u8.ToArray();
    private static readonly byte[] Account = "master-key"u8.ToArray();

    public string Description => "macOS Keychain";
    public bool IsStrong => true;

    public Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct)
    {
        if (TryFind(out var existing, out _)) return Task.FromResult(existing);
        var key = RandomNumberGenerator.GetBytes(32);
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(key));
        var status = SecKeychainAddGenericPassword(0, (uint)Service.Length, Service, (uint)Account.Length, Account, (uint)encoded.Length, encoded, 0);
        CryptographicOperations.ZeroMemory(encoded);
        if (status != 0) throw new CryptographicException($"Keychain write failed (OSStatus {status}).");
        return Task.FromResult(key);
    }

    public Task DeleteKeyAsync(CancellationToken ct)
    {
        if (TryFind(out var key, out var item))
        {
            CryptographicOperations.ZeroMemory(key);
            _ = SecKeychainItemDelete(item);
            CFRelease(item);
        }
        return Task.CompletedTask;
    }

    private static bool TryFind(out byte[] key, out nint item)
    {
        key = [];
        var status = SecKeychainFindGenericPassword(0, (uint)Service.Length, Service, (uint)Account.Length, Account, out var length, out var data, out item);
        if (status == ErrSecItemNotFound) return false;
        if (status != 0) throw new CryptographicException($"Keychain read failed (OSStatus {status}).");
        try
        {
            var buffer = new byte[length];
            Marshal.Copy(data, buffer, 0, (int)length);
            key = Convert.FromBase64String(Encoding.ASCII.GetString(buffer));
            CryptographicOperations.ZeroMemory(buffer);
            return true;
        }
        finally
        {
            _ = SecKeychainItemFreeContent(0, data);
        }
    }

    // The SecKeychain* generic-password API is deprecated in favour of SecItem*, but remains supported and is a
    // far smaller interop surface (no CFDictionary marshalling).
    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(nint keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, nint itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(nint keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, out uint passwordLength, out nint passwordData, out nint itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(nint attrList, nint data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(nint itemRef);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);
}
