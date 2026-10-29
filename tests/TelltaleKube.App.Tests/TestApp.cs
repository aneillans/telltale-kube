using Avalonia;
using Avalonia.Headless;
using TelltaleKube.App.Services;
using TelltaleKube.App.Tests;
using TelltaleKube.Core.Security;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace TelltaleKube.App.Tests;

public sealed class TestPlatform : IPlatformServices
{
    public string PlatformName => "Test";
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("telltale-kube-ui").FullName;
    public IKeyProtector KeyProtector { get; } = new EphemeralKeyProtector();
    public bool IsDesktop => true;
    public Task<bool> AuthenticateAsync(string reason) => Task.FromResult(true);
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Platform.Current = new TestPlatform();
        return AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
