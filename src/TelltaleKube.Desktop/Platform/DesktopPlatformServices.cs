using TelltaleKube.App.Services;
using TelltaleKube.Core.Security;
using Velopack;
using Velopack.Sources;

namespace TelltaleKube.Desktop.Platform;

internal sealed class DesktopPlatformServices : IPlatformServices
{
    /// <summary>Signed release feed. Velopack verifies package hashes from the release manifest before applying.</summary>
    private const string UpdateFeed = "https://github.com/aneillans/telltale-kube";

    public DesktopPlatformServices()
    {
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "TelltaleKube");
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        KeyProtector = CreateKeyProtector(DataDirectory);
    }

    public string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
    public string DataDirectory { get; }
    public IKeyProtector KeyProtector { get; }
    public bool IsDesktop => true;

    /// <summary>Desktop relies on the OS session lock; there is no extra prompt.</summary>
    public Task<bool> AuthenticateAsync(string reason) => Task.FromResult(true);

    public async Task<string?> CheckForUpdatesAsync(bool apply)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(UpdateFeed, accessToken: null, prerelease: false));
            if (!manager.IsInstalled) return "Updates are managed by your package manager.";
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return "Telltale Kube is up to date.";
            if (!apply) return $"Update {update.TargetFullRelease.Version} is available.";
            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
            return null;
        }
        catch (Exception ex)
        {
            return $"Update check failed: {ex.Message}";
        }
    }

    private static IKeyProtector CreateKeyProtector(string dataDirectory)
    {
        if (OperatingSystem.IsWindows()) return new DpapiKeyProtector(dataDirectory);
        if (OperatingSystem.IsMacOS()) return new MacKeychainKeyProtector();
        if (OperatingSystem.IsLinux() && LibSecretKeyProtector.TryCreate() is { } secretService) return secretService;
        return new FileKeyProtector(dataDirectory);
    }
}
