using YamlDotNet.RepresentationModel;

namespace TelltaleKube.Core.Config;

public static class KubeconfigWriter
{
    public static string Write(Kubeconfig config)
    {
        var root = new YamlMappingNode
        {
            { "apiVersion", "v1" },
            { "kind", "Config" },
        };
        if (config.CurrentContext is not null) root.Add("current-context", config.CurrentContext);

        var clusters = new YamlSequenceNode();
        foreach (var c in config.Clusters)
        {
            var m = new YamlMappingNode { { "server", c.Server } };
            if (c.CertificateAuthorityData is not null) m.Add("certificate-authority-data", Convert.ToBase64String(c.CertificateAuthorityData));
            if (c.InsecureSkipTlsVerify) m.Add("insecure-skip-tls-verify", "true");
            AddIf(m, "tls-server-name", c.TlsServerName);
            AddIf(m, "proxy-url", c.ProxyUrl);
            clusters.Add(new YamlMappingNode { { "name", c.Name }, { "cluster", m } });
        }
        root.Add("clusters", clusters);

        var users = new YamlSequenceNode();
        foreach (var u in config.Users)
        {
            var m = new YamlMappingNode();
            AddIf(m, "token", u.Token);
            AddIf(m, "tokenFile", u.TokenFile);
            if (u.ClientCertificateData is not null) m.Add("client-certificate-data", Convert.ToBase64String(u.ClientCertificateData));
            if (u.ClientKeyData is not null) m.Add("client-key-data", Convert.ToBase64String(u.ClientKeyData));
            AddIf(m, "username", u.Username);
            AddIf(m, "password", u.Password);
            if (u.Exec is { } e)
            {
                var em = new YamlMappingNode { { "apiVersion", e.ApiVersion }, { "command", e.Command } };
                if (e.Args.Count > 0) em.Add("args", new YamlSequenceNode(e.Args.Select(a => new YamlScalarNode(a))));
                if (e.Env.Count > 0)
                {
                    em.Add("env", new YamlSequenceNode(e.Env.Select(kv => new YamlMappingNode { { "name", kv.Key }, { "value", kv.Value } })));
                }
                AddIf(em, "installHint", e.InstallHint);
                if (e.ProvideClusterInfo) em.Add("provideClusterInfo", "true");
                m.Add("exec", em);
            }
            if (u.AuthProvider is { } ap)
            {
                var cfg = new YamlMappingNode();
                foreach (var kv in ap.Config) cfg.Add(kv.Key, kv.Value);
                m.Add("auth-provider", new YamlMappingNode { { "name", ap.Name }, { "config", cfg } });
            }
            users.Add(new YamlMappingNode { { "name", u.Name }, { "user", m } });
        }
        root.Add("users", users);

        var contexts = new YamlSequenceNode();
        foreach (var c in config.Contexts)
        {
            var m = new YamlMappingNode { { "cluster", c.Cluster } };
            AddIf(m, "user", c.User);
            AddIf(m, "namespace", c.Namespace);
            contexts.Add(new YamlMappingNode { { "name", c.Name }, { "context", m } });
        }
        root.Add("contexts", contexts);

        using var sw = new StringWriter();
        new YamlStream(new YamlDocument(root)).Save(sw, assignAnchors: false);
        return sw.ToString();
    }

    public static Kubeconfig Extract(Kubeconfig source, string contextName)
    {
        var r = source.Resolve(contextName);
        var k = new Kubeconfig { CurrentContext = r.Context.Name };
        k.Contexts.Add(r.Context);
        k.Clusters.Add(r.Cluster);
        if (!string.IsNullOrEmpty(r.User.Name)) k.Users.Add(r.User);
        return k;
    }

    private static void AddIf(YamlMappingNode m, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) m.Add(key, value);
    }
}
