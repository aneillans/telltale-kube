using System.Security.Cryptography;
using Foundation;
using TelltaleKube.Core.Security;
using Security;

namespace TelltaleKube.iOS;

/// <summary>
/// Master key in the iOS Keychain with kSecAttrAccessibleWhenUnlockedThisDeviceOnly: only readable while the
/// device is unlocked, never synced to iCloud Keychain and never restored to another device.
/// </summary>
internal sealed class KeychainKeyProtector : IKeyProtector
{
    private const string Service = "uk.co.neillans.telltalekube.vault";
    private const string Account = "master-key";

    public string Description => "iOS Keychain (this device only, Secure Enclave protected)";
    public bool IsStrong => true;

    public Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct)
    {
        var query = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = Account };
        var existing = SecKeyChain.QueryAsData(query, wantPersistentReference: false, out var status);
        if (status == SecStatusCode.Success && existing is not null) return Task.FromResult(existing.ToArray());
        if (status != SecStatusCode.ItemNotFound) throw new CryptographicException($"Keychain read failed: {status}");

        var key = RandomNumberGenerator.GetBytes(32);
        var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = Account,
            ValueData = NSData.FromArray(key),
            Accessible = SecAccessible.WhenUnlockedThisDeviceOnly,
            Synchronizable = false,
        };
        var add = SecKeyChain.Add(record);
        if (add != SecStatusCode.Success) throw new CryptographicException($"Keychain write failed: {add}");
        return Task.FromResult(key);
    }

    public Task DeleteKeyAsync(CancellationToken ct)
    {
        SecKeyChain.Remove(new SecRecord(SecKind.GenericPassword) { Service = Service, Account = Account });
        return Task.CompletedTask;
    }
}
