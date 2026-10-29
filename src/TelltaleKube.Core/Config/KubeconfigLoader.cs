using System.Text;
using YamlDotNet.RepresentationModel;

namespace TelltaleKube.Core.Config;

/// <summary>
/// Reflection-free kubeconfig parser (safe for trimming / NativeAOT). Uses the YAML node model only, so a
/// hostile kubeconfig can never instantiate arbitrary types.
/// </summary>
public static class KubeconfigLoader
{
    /// <summary>Upper bound on a kubeconfig (or referenced cert/key file) to avoid memory exhaustion.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    public static Kubeconfig Parse(string yaml, string? baseDirectory = null, bool inlineFiles = true)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxFileBytes)
        {
            throw new KubeconfigException("Kubeconfig is too large.");
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException)
        {
            throw new KubeconfigException($"Invalid kubeconfig YAML: {ex.Message}", ex);
        }

        var config = new Kubeconfig();
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return config;
        }

        config.CurrentContext = Str(root, "current-context");

        foreach (var item in Seq(root, "clusters"))
        {
            var name = Str(item, "name");
            if (string.IsNullOrEmpty(name) || Map(item, "cluster") is not { } c) continue;
            config.Clusters.Add(new NamedCluster
            {
                Name = name,
                Server = Str(c, "server") ?? "",
                CertificateAuthorityData = DataOrFile(c, "certificate-authority-data", "certificate-authority", baseDirectory),
                InsecureSkipTlsVerify = Bool(c, "insecure-skip-tls-verify"),
                TlsServerName = Str(c, "tls-server-name"),
                ProxyUrl = Str(c, "proxy-url"),
            });
        }

        foreach (var item in Seq(root, "users"))
        {
            var name = Str(item, "name");
            if (string.IsNullOrEmpty(name)) continue;
            var user = new NamedUser { Name = name };
            if (Map(item, "user") is { } u)
            {
                user.Token = Str(u, "token");
                var tokenFile = ResolvePath(Str(u, "tokenFile"), baseDirectory);
                if (tokenFile is not null && inlineFiles && user.Token is null)
                {
                    user.Token = ReadText(tokenFile).Trim();
                }
                else
                {
                    user.TokenFile = tokenFile;
                }

                user.ClientCertificateData = DataOrFile(u, "client-certificate-data", "client-certificate", baseDirectory);
                user.ClientKeyData = DataOrFile(u, "client-key-data", "client-key", baseDirectory);
                user.Username = Str(u, "username");
                user.Password = Str(u, "password");

                if (Map(u, "exec") is { } e)
                {
                    var exec = new ExecConfig
                    {
                        ApiVersion = Str(e, "apiVersion") ?? "client.authentication.k8s.io/v1",
                        Command = Str(e, "command") ?? "",
                        InstallHint = Str(e, "installHint"),
                        ProvideClusterInfo = Bool(e, "provideClusterInfo"),
                    };
                    foreach (var a in SeqScalars(e, "args")) exec.Args.Add(a);
                    foreach (var env in Seq(e, "env"))
                    {
                        var k = Str(env, "name");
                        if (!string.IsNullOrEmpty(k)) exec.Env[k] = Str(env, "value") ?? "";
                    }
                    user.Exec = exec;
                }

                if (Map(u, "auth-provider") is { } ap)
                {
                    var provider = new AuthProviderConfig { Name = Str(ap, "name") ?? "" };
                    if (Map(ap, "config") is { } cfg)
                    {
                        foreach (var (k, v) in cfg.Children)
                        {
                            if (k is YamlScalarNode ks && v is YamlScalarNode vs && ks.Value is not null)
                                provider.Config[ks.Value] = vs.Value ?? "";
                        }
                    }
                    user.AuthProvider = provider;
                }
            }
            config.Users.Add(user);
        }

        foreach (var item in Seq(root, "contexts"))
        {
            var name = Str(item, "name");
            if (string.IsNullOrEmpty(name) || Map(item, "context") is not { } c) continue;
            config.Contexts.Add(new NamedContext
            {
                Name = name,
                Cluster = Str(c, "cluster") ?? "",
                User = Str(c, "user"),
                Namespace = Str(c, "namespace"),
            });
        }

        return config;
    }

    public static Kubeconfig LoadFile(string path, bool inlineFiles = true)
    {
        var full = Path.GetFullPath(path);
        return Parse(ReadText(full), Path.GetDirectoryName(full), inlineFiles);
    }

    /// <summary>Loads and merges the files in $KUBECONFIG (or ~/.kube/config). First definition wins, like kubectl.</summary>
    public static Kubeconfig LoadDefault(bool inlineFiles = false)
    {
        var merged = new Kubeconfig();
        foreach (var path in DefaultPaths())
        {
            if (!File.Exists(path)) continue;
            Merge(merged, LoadFile(path, inlineFiles));
        }
        return merged;
    }

    public static IReadOnlyList<string> DefaultPaths()
    {
        var env = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? [] : [Path.Combine(home, ".kube", "config")];
    }

    public static void Merge(Kubeconfig target, Kubeconfig source)
    {
        target.CurrentContext ??= source.CurrentContext;
        foreach (var c in source.Clusters) if (target.FindCluster(c.Name) is null) target.Clusters.Add(c);
        foreach (var u in source.Users) if (target.FindUser(u.Name) is null) target.Users.Add(u);
        foreach (var c in source.Contexts) if (target.FindContext(c.Name) is null) target.Contexts.Add(c);
    }

    private static byte[]? DataOrFile(YamlMappingNode node, string dataKey, string fileKey, string? baseDir)
    {
        var data = Str(node, dataKey);
        if (!string.IsNullOrWhiteSpace(data))
        {
            try
            {
                return Convert.FromBase64String(data.Trim());
            }
            catch (FormatException ex)
            {
                throw new KubeconfigException($"'{dataKey}' is not valid base64.", ex);
            }
        }

        var file = ResolvePath(Str(node, fileKey), baseDir);
        if (file is null) return null;
        // Certificates are always inlined: TLS handlers are built once per connection.
        return ReadBytes(file);
    }

    private static string? ResolvePath(string? path, string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith('~'))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/', '\\'));
        }
        return Path.IsPathRooted(path) || baseDir is null ? path : Path.GetFullPath(Path.Combine(baseDir, path));
    }

    private static byte[] ReadBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) throw new KubeconfigException($"Referenced file '{path}' is too large.");
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new KubeconfigException($"Cannot read referenced file '{path}': {ex.Message}", ex);
        }
    }

    private static string ReadText(string path) => Encoding.UTF8.GetString(ReadBytes(path));

    private static YamlNode? Child(YamlNode node, string key) =>
        node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Str(YamlNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    private static bool Bool(YamlNode node, string key) =>
        bool.TryParse(Str(node, key), out var b) && b;

    private static YamlMappingNode? Map(YamlNode node, string key) => Child(node, key) as YamlMappingNode;

    private static IEnumerable<YamlNode> Seq(YamlNode node, string key) =>
        Child(node, key) is YamlSequenceNode s ? s.Children : [];

    private static IEnumerable<string> SeqScalars(YamlNode node, string key) =>
        Seq(node, key).OfType<YamlScalarNode>().Select(s => s.Value ?? "");
}
