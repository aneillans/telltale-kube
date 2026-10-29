using Avalonia;
using TelltaleKube.Desktop.Platform;
using Velopack;

namespace TelltaleKube.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks and exits early when invoked by the installer.
        VelopackApp.Build().Run();

        TelltaleKube.App.Services.Platform.Current = new DesktopPlatformServices();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TelltaleKube.App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
