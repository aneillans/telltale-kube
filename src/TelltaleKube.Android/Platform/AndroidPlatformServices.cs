using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using Java.Util.Concurrent;
using TelltaleKube.App.Services;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Android;

internal sealed class AndroidPlatformServices : IPlatformServices
{
    public AndroidPlatformServices(Context context)
    {
        // Internal app storage: private to the app's Linux UID, excluded from backups (see manifest).
        DataDirectory = Path.Combine(context.FilesDir!.AbsolutePath, "telltale-kube");
        Directory.CreateDirectory(DataDirectory);
        KeyProtector = new AndroidKeystoreKeyProtector(DataDirectory);
    }

    public static Activity? CurrentActivity { get; set; }

    public string PlatformName => $"Android {Build.VERSION.Release}";
    public string DataDirectory { get; }
    public IKeyProtector KeyProtector { get; }
    public bool IsDesktop => false;

    /// <summary>BiometricPrompt with device PIN/pattern/password fallback.</summary>
    public Task<bool> AuthenticateAsync(string reason)
    {
        var activity = CurrentActivity;
        if (activity is null) return Task.FromResult(false);

        var keyguard = (KeyguardManager?)activity.GetSystemService(Context.KeyguardService);
        if (keyguard is null || !keyguard.IsDeviceSecure)
        {
            // No screen lock configured: there is nothing to verify against.
            return Task.FromResult(true);
        }

        var tcs = new TaskCompletionSource<bool>();
        var prompt = new BiometricPrompt.Builder(activity)
            .SetTitle("Telltale Kube")!
            .SetSubtitle(reason)!
            .SetAllowedAuthenticators((int)(BiometricManagerAuthenticators.BiometricStrong | BiometricManagerAuthenticators.DeviceCredential))!
            .Build();
        prompt.Authenticate(new CancellationSignal(), Executors.NewSingleThreadExecutor()!, new Callback(tcs));
        return tcs.Task;
    }

    private sealed class Callback(TaskCompletionSource<bool> tcs) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) => tcs.TrySetResult(true);
        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) => tcs.TrySetResult(false);
    }
}
