using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using TelltaleKube.App.Services;
using TelltaleKube.App.ViewModels;
using TelltaleKube.App.Views;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App;

public partial class App : Application
{
    private MainViewModel? _main;
#if DEBUG
    private static bool s_devToolsAttached;
#endif

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        // Guarded: test hosts create several Application instances per process.
        if (!s_devToolsAttached)
        {
            s_devToolsAttached = true;
            this.AttachDeveloperTools();
        }
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var platform = Platform.Current;
        var state = new AppState(new SecretVault(platform.DataDirectory, platform.KeyProtector));
        _main = new MainViewModel(state);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = _main };
            desktop.ShutdownRequested += (_, _) => _ = _main.GoHome();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView { DataContext = _main };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView { DataContext = _main };
        }

        // Privacy screen + auto-lock when the app goes to the background (mobile app switcher, minimised window).
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Deactivated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Background) _main.OnBackgrounded();
            };
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Background) _main.OnForegrounded();
            };
        }

        base.OnFrameworkInitializationCompleted();
        _ = InitialiseAsync(state);
    }

    private async Task InitialiseAsync(AppState state)
    {
        await state.InitialiseAsync();
        ApplyTheme(state.Settings.Theme);
        _main!.Home.Refresh();
        if (state.Settings.CheckForUpdates && Platform.Current.IsDesktop)
        {
            var status = await Platform.Current.CheckForUpdatesAsync(apply: false);
            if (status is not null && status.StartsWith("Update", StringComparison.Ordinal)) _main.Toast(status);
        }
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
