using TelltaleKube.Core.Security;

namespace TelltaleKube.App.Services;

/// <summary>Everything that differs between Windows, macOS, Linux, Android and iOS. Implemented by each head project.</summary>
public interface IPlatformServices
{
    string PlatformName { get; }

    string DataDirectory { get; }

    IKeyProtector KeyProtector { get; }

    /// <summary>Desktop-class device: exec credential plugins, port-forwarding and ~/.kube/config are available.</summary>
    bool IsDesktop { get; }

    /// <summary>Ask the OS to verify the device owner (biometrics / device passcode). Returns true when verified or not applicable.</summary>
    Task<bool> AuthenticateAsync(string reason);

    /// <summary>Optional: checks for and applies a signed update (desktop). Returns a status message, or null if not supported.</summary>
    Task<string?> CheckForUpdatesAsync(bool apply) => Task.FromResult<string?>(null);
}

/// <summary>Set by the platform head before Avalonia starts.</summary>
public static class Platform
{
    private static IPlatformServices? _current;

    public static IPlatformServices Current
    {
        get => _current ?? throw new InvalidOperationException("Platform services were not initialised by the platform head.");
        set => _current = value;
    }
}
