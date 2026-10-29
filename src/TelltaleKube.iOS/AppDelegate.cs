using Avalonia;
using Avalonia.iOS;
using Foundation;

namespace TelltaleKube.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<TelltaleKube.App.App>
#pragma warning restore CA1711
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        TelltaleKube.App.Services.Platform.Current = new IosPlatformServices();
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
