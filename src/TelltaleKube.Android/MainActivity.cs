using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace TelltaleKube.Android;

[Activity(
    // Launcher (home-screen) label: short, like iOS. The application label (Settings → Apps, system dialogs)
    // stays "Telltale Kube" via ApplicationTitle / the manifest.
    Label = "Telltale",
    Theme = "@style/TelltaleKubeTheme",
    Icon = "@mipmap/appicon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // FLAG_SECURE: no screenshots, no screen recording, blank thumbnail in Recents (secrets stay on screen only).
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        base.OnCreate(savedInstanceState);
        AndroidPlatformServices.CurrentActivity = this;
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidPlatformServices.CurrentActivity = this;
    }
}
