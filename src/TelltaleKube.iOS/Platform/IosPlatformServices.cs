using Foundation;
using TelltaleKube.App.Services;
using TelltaleKube.Core.Security;
using LocalAuthentication;
using UIKit;

namespace TelltaleKube.iOS;

internal sealed class IosPlatformServices : IPlatformServices
{
    public IosPlatformServices()
    {
        // Application Support is backed up only to encrypted backups and is protected by NSFileProtectionComplete.
        var support = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path!;
        DataDirectory = Path.Combine(support, "TelltaleKube");
        Directory.CreateDirectory(DataDirectory);
        // Exclude from iCloud/iTunes backup: the Keychain key is ThisDeviceOnly, so a restored vault is unusable anyway.
        NSUrl.FromFilename(DataDirectory).SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
        KeyProtector = new KeychainKeyProtector();
    }

    public string PlatformName => $"{UIDevice.CurrentDevice.SystemName} {UIDevice.CurrentDevice.SystemVersion}";
    public string DataDirectory { get; }
    public IKeyProtector KeyProtector { get; }
    public bool IsDesktop => false;

    /// <summary>Face ID / Touch ID with device passcode fallback.</summary>
    public async Task<bool> AuthenticateAsync(string reason)
    {
        using var context = new LAContext();
        if (!context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out _))
        {
            return true; // no passcode set: nothing to verify against
        }
        var (ok, _) = await context.EvaluatePolicyAsync(LAPolicy.DeviceOwnerAuthentication, reason);
        return ok;
    }
}
