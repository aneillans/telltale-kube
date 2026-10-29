namespace TelltaleKube.Core.Config;

/// <summary>An in-memory, fully resolved kubeconfig (all referenced files already inlined).</summary>
public sealed class Kubeconfig
{
    public string? CurrentContext { get; set; }
    public List<NamedCluster> Clusters { get; } = [];
    public List<NamedUser> Users { get; } = [];
    public List<NamedContext> Contexts { get; } = [];

    public NamedContext? FindContext(string name) => Contexts.Find(c => c.Name == name);
    public NamedCluster? FindCluster(string name) => Clusters.Find(c => c.Name == name);
    public NamedUser? FindUser(string name) => Users.Find(u => u.Name == name);

    public ResolvedContext Resolve(string? contextName = null)
    {
        contextName ??= CurrentContext ?? Contexts.FirstOrDefault()?.Name
            ?? throw new KubeconfigException("The kubeconfig contains no contexts.");

        var ctx = FindContext(contextName) ?? throw new KubeconfigException($"Context '{contextName}' not found.");
        var cluster = FindCluster(ctx.Cluster) ?? throw new KubeconfigException($"Cluster '{ctx.Cluster}' referenced by context '{ctx.Name}' not found.");
        var user = string.IsNullOrEmpty(ctx.User)
            ? new NamedUser { Name = "" }
            : FindUser(ctx.User) ?? throw new KubeconfigException($"User '{ctx.User}' referenced by context '{ctx.Name}' not found.");
        return new ResolvedContext(ctx, cluster, user);
    }
}

public sealed record ResolvedContext(NamedContext Context, NamedCluster Cluster, NamedUser User);

public sealed class NamedCluster
{
    public required string Name { get; init; }
    public string Server { get; set; } = "";
    public byte[]? CertificateAuthorityData { get; set; }
    public bool InsecureSkipTlsVerify { get; set; }
    public string? TlsServerName { get; set; }
    public string? ProxyUrl { get; set; }
}

public sealed class NamedUser
{
    public required string Name { get; init; }
    public string? Token { get; set; }
    public string? TokenFile { get; set; }
    public byte[]? ClientCertificateData { get; set; }
    public byte[]? ClientKeyData { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public ExecConfig? Exec { get; set; }
    public AuthProviderConfig? AuthProvider { get; set; }
}

public sealed class NamedContext
{
    public required string Name { get; init; }
    public string Cluster { get; set; } = "";
    public string? User { get; set; }
    public string? Namespace { get; set; }
}

public sealed class ExecConfig
{
    public string ApiVersion { get; set; } = "client.authentication.k8s.io/v1";
    public string Command { get; set; } = "";
    public List<string> Args { get; } = [];
    public Dictionary<string, string> Env { get; } = new(StringComparer.Ordinal);
    public string? InstallHint { get; set; }
    public bool ProvideClusterInfo { get; set; }
}

/// <summary>Legacy auth-provider block. Only a pre-issued OIDC id-token is honoured (no refresh, no gcp/azure helpers).</summary>
public sealed class AuthProviderConfig
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Config { get; } = new(StringComparer.Ordinal);
}

public sealed class KubeconfigException(string message, Exception? inner = null) : Exception(message, inner);
