using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace TelltaleKube.Android;

[Application]
public class MainApplication : AvaloniaAndroidApplication<TelltaleKube.App.App>
{
    protected MainApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        TelltaleKube.App.Services.Platform.Current = new AndroidPlatformServices(this);
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
