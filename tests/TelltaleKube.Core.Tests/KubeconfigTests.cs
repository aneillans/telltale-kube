using TelltaleKube.Core.Config;

namespace TelltaleKube.Core.Tests;

public class KubeconfigTests
{
    internal const string Sample = """
        apiVersion: v1
        kind: Config
        current-context: prod
        clusters:
        - name: prod-cluster
          cluster:
            server: https://prod.example.com:6443
            certificate-authority-data: aGVsbG8=
            tls-server-name: api.internal
        - name: dev-cluster
          cluster:
            server: https://dev.example.com
            insecure-skip-tls-verify: true
        users:
        - name: alice
          user:
            token: s3cr3t
        - name: eks
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: aws
              args: ["eks", "get-token", "--cluster-name", "prod"]
              env:
              - name: AWS_PROFILE
                value: prod
        contexts:
        - name: prod
          context:
            cluster: prod-cluster
            user: alice
            namespace: payments
        - name: dev
          context:
            cluster: dev-cluster
            user: eks
        """;

    [Fact]
    public void Parses_clusters_users_and_contexts()
    {
        var k = KubeconfigLoader.Parse(Sample);

        Assert.Equal("prod", k.CurrentContext);
        Assert.Equal(2, k.Clusters.Count);
        Assert.Equal("hello"u8.ToArray(), k.FindCluster("prod-cluster")!.CertificateAuthorityData);
        Assert.Equal("api.internal", k.FindCluster("prod-cluster")!.TlsServerName);
        Assert.True(k.FindCluster("dev-cluster")!.InsecureSkipTlsVerify);
        Assert.Equal("s3cr3t", k.FindUser("alice")!.Token);

        var exec = k.FindUser("eks")!.Exec!;
        Assert.Equal("aws", exec.Command);
        Assert.Equal(["eks", "get-token", "--cluster-name", "prod"], exec.Args);
        Assert.Equal("prod", exec.Env["AWS_PROFILE"]);
    }

    [Fact]
    public void Resolve_uses_current_context_by_default()
    {
        var r = KubeconfigLoader.Parse(Sample).Resolve();
        Assert.Equal("prod-cluster", r.Cluster.Name);
        Assert.Equal("alice", r.User.Name);
        Assert.Equal("payments", r.Context.Namespace);
    }

    [Fact]
    public void Resolve_reports_dangling_references()
    {
        var k = KubeconfigLoader.Parse("""
            contexts:
            - name: broken
              context: { cluster: missing, user: nobody }
            """);
        var ex = Assert.Throws<KubeconfigException>(() => k.Resolve("broken"));
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_yaml_and_base64_are_reported_as_kubeconfig_errors()
    {
        Assert.Throws<KubeconfigException>(() => KubeconfigLoader.Parse("clusters: [ {"));
        Assert.Throws<KubeconfigException>(() => KubeconfigLoader.Parse("""
            clusters:
            - name: c
              cluster: { server: "https://x", certificate-authority-data: "!!!notbase64" }
            """));
    }

    [Fact]
    public void Referenced_files_are_inlined_relative_to_the_kubeconfig()
    {
        var dir = Directory.CreateTempSubdirectory("telltale-kube-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "ca.crt"), "CA");
            File.WriteAllText(Path.Combine(dir, "token"), "tok\n");
            File.WriteAllText(Path.Combine(dir, "config"), """
                clusters:
                - name: c
                  cluster: { server: "https://x", certificate-authority: ca.crt }
                users:
                - name: u
                  user: { tokenFile: token }
                contexts:
                - name: ctx
                  context: { cluster: c, user: u }
                """);

            var k = KubeconfigLoader.LoadFile(Path.Combine(dir, "config"));
            Assert.Equal("CA"u8.ToArray(), k.Clusters[0].CertificateAuthorityData);
            Assert.Equal("tok", k.Users[0].Token);
            Assert.Null(k.Users[0].TokenFile);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Merge_keeps_first_definition()
    {
        var a = KubeconfigLoader.Parse(Sample);
        var b = KubeconfigLoader.Parse("""
            current-context: other
            users:
            - name: alice
              user: { token: different }
            - name: bob
              user: { token: b }
            """);
        KubeconfigLoader.Merge(a, b);
        Assert.Equal("prod", a.CurrentContext);
        Assert.Equal("s3cr3t", a.FindUser("alice")!.Token);
        Assert.NotNull(a.FindUser("bob"));
    }

    [Fact]
    public void Writer_round_trips_and_extracts_a_single_context()
    {
        var original = KubeconfigLoader.Parse(Sample);
        var single = KubeconfigWriter.Extract(original, "dev");
        var yaml = KubeconfigWriter.Write(single);
        var back = KubeconfigLoader.Parse(yaml);

        Assert.Equal("dev", back.CurrentContext);
        Assert.Single(back.Contexts);
        Assert.Single(back.Clusters);
        Assert.True(back.Clusters[0].InsecureSkipTlsVerify);
        Assert.Equal(original.FindUser("eks")!.Exec!.Args, back.Users[0].Exec!.Args);
        Assert.Equal("prod", back.Users[0].Exec!.Env["AWS_PROFILE"]);
    }

    [Fact]
    public void Oversized_kubeconfig_is_rejected()
    {
        var huge = new string('a', KubeconfigLoader.MaxFileBytes + 1);
        Assert.Throws<KubeconfigException>(() => KubeconfigLoader.Parse(huge));
    }
}
