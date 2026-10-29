using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TelltaleKube.Core.Config;
using TelltaleKube.Core.Http;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Core.Tests;

public class SecurityTests
{
    [Fact]
    public async Task Vault_round_trips_and_file_is_not_plaintext()
    {
        var dir = Directory.CreateTempSubdirectory("telltale-kube-vault").FullName;
        try
        {
            var protector = new EphemeralKeyProtector();
            using (var vault = new SecretVault(dir, protector))
            {
                var data = await vault.LoadAsync(TestContext.Current.CancellationToken);
                data.Clusters.Add(new StoredCluster { DisplayName = "prod", KubeconfigYaml = "token: super-secret-token" });
                await vault.SaveAsync(TestContext.Current.CancellationToken);
            }

            var raw = await File.ReadAllBytesAsync(Path.Combine(dir, "vault.bin"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("super-secret-token", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

            using var reopened = new SecretVault(dir, protector);
            var loaded = await reopened.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("token: super-secret-token", Assert.Single(loaded.Clusters).KubeconfigYaml);

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "vault.bin")));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Tampered_vault_is_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = SecretVault.Encrypt("hello"u8, key);
        blob[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => SecretVault.Decrypt(blob, key));
    }

    [Fact]
    public void Vault_with_wrong_key_is_rejected()
    {
        var blob = SecretVault.Encrypt("hello"u8, RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => SecretVault.Decrypt(blob, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public async Task Wipe_destroys_key_and_data()
    {
        var dir = Directory.CreateTempSubdirectory("telltale-kube-wipe").FullName;
        try
        {
            var protector = new FileKeyProtector(dir);
            using var vault = new SecretVault(dir, protector);
            (await vault.LoadAsync(TestContext.Current.CancellationToken)).Clusters.Add(new StoredCluster());
            await vault.SaveAsync(TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(dir, "vault.key")));

            await vault.WipeAsync(TestContext.Current.CancellationToken);
            Assert.False(File.Exists(Path.Combine(dir, "vault.bin")));
            Assert.False(File.Exists(Path.Combine(dir, "vault.key")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def-ghi", "Bearer [REDACTED]")]
    [InlineData("token: abc123", "token: [REDACTED]")]
    [InlineData("jwt eyJhbGciOiJIUzI1.eyJzdWIiOiIxMjM0.SflKxwRJSMeKKF2QT4", "[REDACTED-JWT]")]
    public void Redactor_scrubs_credentials(string input, string expected)
    {
        var output = Redactor.Redact(input);
        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Insecure_tls_contexts_are_refused_unless_explicitly_allowed()
    {
        var ctx = Resolve("https://dev.example.com", insecure: true);
        Assert.Throws<SecurityPolicyException>(() => KubeHttpPipeline.Create(ctx, SecurityPolicy.Default, null, out _));

        using var invoker = KubeHttpPipeline.Create(ctx, SecurityPolicy.Default with { AllowInsecureTls = true }, null, out var uri);
        Assert.Equal("https://dev.example.com/", uri.ToString());
    }

    [Theory]
    [InlineData("http://api.example.com", true)]
    [InlineData("http://127.0.0.1:8001", false)]
    [InlineData("http://localhost:8001", false)]
    [InlineData("ftp://x", true)]
    public void Plain_http_is_only_allowed_to_loopback(string server, bool shouldThrow)
    {
        var ctx = Resolve(server, insecure: false);
        if (shouldThrow)
        {
            Assert.ThrowsAny<Exception>(() => KubeHttpPipeline.Create(ctx, SecurityPolicy.Default, null, out _));
        }
        else
        {
            using var _ = KubeHttpPipeline.Create(ctx, SecurityPolicy.Default, null, out var _);
        }
    }

    [Fact]
    public void Basic_auth_and_exec_plugins_respect_policy()
    {
        var basic = Resolve("https://x", false, u => u.Username = "admin");
        Assert.Throws<SecurityPolicyException>(() => CredentialSourceFactory.Create(basic, SecurityPolicy.Default, null));
        Assert.NotNull(CredentialSourceFactory.Create(basic, SecurityPolicy.Default with { AllowBasicAuth = true }, null));

        var exec = Resolve("https://x", false, u => u.Exec = new ExecConfig { Command = "aws" });
        Assert.Throws<SecurityPolicyException>(() => CredentialSourceFactory.Create(exec, SecurityPolicy.Default with { AllowExecPlugins = false }, new DenyApprover()));
        Assert.Throws<SecurityPolicyException>(() => CredentialSourceFactory.Create(exec, SecurityPolicy.Default, approver: null));
    }

    [Fact]
    public async Task Unapproved_exec_plugin_never_runs()
    {
        var cluster = new NamedCluster { Name = "c", Server = "https://x" };
        using var source = new ExecCredentialSource(new ExecConfig { Command = "definitely-not-run" }, cluster, new DenyApprover());
        await Assert.ThrowsAsync<SecurityPolicyException>(async () => await source.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Exec_fingerprint_changes_when_command_changes()
    {
        var a = new ExecConfig { Command = "aws" };
        a.Args.Add("eks");
        var b = new ExecConfig { Command = "aws" };
        b.Args.Add("eks");
        var c = new ExecConfig { Command = "aws" };
        c.Args.Add("eks");
        c.Env["EVIL"] = "1";

        Assert.Equal(ExecCredentialSource.ComputeFingerprint(a), ExecCredentialSource.ComputeFingerprint(b));
        Assert.NotEqual(ExecCredentialSource.ComputeFingerprint(a), ExecCredentialSource.ComputeFingerprint(c));
    }

    [Fact]
    public void Exec_credential_output_is_parsed()
    {
        var cred = ExecCredentialSource.ParseExecCredential("""
            {"apiVersion":"client.authentication.k8s.io/v1","kind":"ExecCredential",
             "status":{"token":"abc","expirationTimestamp":"2030-01-01T00:00:00Z"}}
            """);
        Assert.Equal("Bearer", cred.Authorization!.Scheme);
        Assert.Equal("abc", cred.Authorization.Parameter);
        Assert.Equal(2030, cred.Expires!.Value.Year);
        Assert.Throws<KubeconfigException>(() => ExecCredentialSource.ParseExecCredential("""{"status":{}}"""));
    }

    [Fact]
    public void Custom_ca_validation_pins_to_the_cluster_ca()
    {
        using var clusterCa = CreateCa("CN=cluster-ca");
        using var otherCa = CreateCa("CN=other-ca");
        using var leaf = CreateLeaf(clusterCa, "CN=api");
        using var rogue = CreateLeaf(otherCa, "CN=api");

        var validator = TlsConfig.CreateValidator([X509CertificateLoader.LoadCertificate(clusterCa.RawData)], insecureSkipVerify: false);
        Assert.True(validator(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validator(this, rogue, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validator(this, leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Default_validation_requires_no_errors()
    {
        var validator = TlsConfig.CreateValidator(null, insecureSkipVerify: false);
        Assert.False(validator(this, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(validator(this, null, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Pem_client_certificate_loads_with_private_key()
    {
        using var ca = CreateCa("CN=ca");
        using var leaf = CreateLeaf(ca, "CN=user");
        var certPem = System.Text.Encoding.ASCII.GetBytes(leaf.ExportCertificatePem());
        var keyPem = System.Text.Encoding.ASCII.GetBytes(leaf.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());

        using var loaded = TlsConfig.LoadClientCertificate(certPem, keyPem);
        Assert.True(loaded.HasPrivateKey);
    }

    private sealed class DenyApprover : IExecApprover
    {
        public ValueTask<bool> ApproveAsync(ExecConfig exec, string fingerprint, CancellationToken ct) => ValueTask.FromResult(false);
    }

    private static ResolvedContext Resolve(string server, bool insecure, Action<NamedUser>? configure = null)
    {
        var user = new NamedUser { Name = "u", Token = "t" };
        if (configure is not null)
        {
            user.Token = null;
            configure(user);
        }
        return new ResolvedContext(new NamedContext { Name = "ctx", Cluster = "c", User = "u" },
            new NamedCluster { Name = "c", Server = server, InsecureSkipTlsVerify = insecure }, user);
    }

    private static X509Certificate2 CreateCa(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string subject)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var cert = req.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8));
        return cert.CopyWithPrivateKey(key);
    }
}
