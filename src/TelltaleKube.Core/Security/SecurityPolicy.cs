namespace TelltaleKube.Core.Security;

/// <summary>
/// Per-connection security posture. Defaults are the safe choice; every relaxation is an explicit opt-in that
/// the UI surfaces with a warning.
/// </summary>
public sealed record SecurityPolicy
{
    /// <summary>Block every mutating verb (delete, patch, apply, scale, exec, port-forward).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Honour <c>insecure-skip-tls-verify: true</c> from a kubeconfig. Off by default: such a context fails to
    /// connect until the user explicitly allows it for that cluster.
    /// </summary>
    public bool AllowInsecureTls { get; init; }

    /// <summary>Allow kubeconfig exec credential plugins to run (desktop only; never available on mobile).</summary>
    public bool AllowExecPlugins { get; init; } = true;

    /// <summary>Allow HTTP basic auth (deprecated in Kubernetes, sends a reusable password on every request).</summary>
    public bool AllowBasicAuth { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static SecurityPolicy Default { get; } = new();
}

public sealed class SecurityPolicyException(string message) : Exception(message);
