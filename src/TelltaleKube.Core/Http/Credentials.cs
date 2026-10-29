using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TelltaleKube.Core.Config;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Core.Http;

public sealed record Credential(AuthenticationHeaderValue? Authorization, X509Certificate2? ClientCertificate, DateTimeOffset? Expires);

public interface ICredentialSource
{
    ValueTask<Credential> GetAsync(CancellationToken ct);

    /// <summary>Called after a 401 so cached credentials are discarded before a single retry.</summary>
    void Invalidate();
}

internal sealed class StaticCredentialSource(Credential credential) : ICredentialSource
{
    public ValueTask<Credential> GetAsync(CancellationToken ct) => ValueTask.FromResult(credential);
    public void Invalidate() { }
}

/// <summary>Re-reads a projected/rotated token file at most once a minute (same cadence as client-go).</summary>
internal sealed class TokenFileCredentialSource(string path) : ICredentialSource
{
    private Credential? _cached;
    private DateTimeOffset _readAt;

    public async ValueTask<Credential> GetAsync(CancellationToken ct)
    {
        if (_cached is null || DateTimeOffset.UtcNow - _readAt > TimeSpan.FromMinutes(1))
        {
            var token = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
            _cached = new Credential(new AuthenticationHeaderValue("Bearer", token), null, null);
            _readAt = DateTimeOffset.UtcNow;
        }
        return _cached;
    }

    public void Invalidate() => _cached = null;
}

public interface IExecApprover
{
    ValueTask<bool> ApproveAsync(ExecConfig exec, string fingerprint, CancellationToken ct);
}

/// <summary>
/// Runs a client.authentication.k8s.io exec plugin. Security properties:
/// <list type="bullet">
/// <item>never via a shell (argument list only, no string interpolation),</item>
/// <item>the exact command + args + env must be approved by the user (fingerprinted, so an edited kubeconfig re-prompts),</item>
/// <item>bounded run time and output size, stdin closed (non-interactive).</item>
/// </list>
/// </summary>
public sealed class ExecCredentialSource(ExecConfig exec, NamedCluster cluster, IExecApprover approver) : ICredentialSource, IDisposable
{
    private const int MaxOutputBytes = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Credential? _cached;

    public string Fingerprint { get; } = ComputeFingerprint(exec);

    public static string ComputeFingerprint(ExecConfig exec)
    {
        var sb = new StringBuilder().Append(exec.ApiVersion).Append('\0').Append(exec.Command);
        foreach (var a in exec.Args) sb.Append('\0').Append(a);
        foreach (var kv in exec.Env.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append('\0').Append(kv.Key).Append('=').Append(kv.Value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public async ValueTask<Credential> GetAsync(CancellationToken ct)
    {
        if (_cached is { } c && (c.Expires is null || c.Expires > DateTimeOffset.UtcNow.AddSeconds(30))) return c;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } c2 && (c2.Expires is null || c2.Expires > DateTimeOffset.UtcNow.AddSeconds(30))) return c2;
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            {
                throw new SecurityPolicyException(
                    $"This context uses the exec credential plugin '{exec.Command}', which cannot run on mobile. Use a token or client-certificate kubeconfig instead.");
            }
            if (!await approver.ApproveAsync(exec, Fingerprint, ct).ConfigureAwait(false))
            {
                throw new SecurityPolicyException($"Running the credential plugin '{exec.Command}' was not approved.");
            }
            _cached = await RunAsync(ct).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    public void Dispose() => _gate.Dispose();

    private async Task<Credential> RunAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exec.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in exec.Args) psi.ArgumentList.Add(a);
        foreach (var kv in exec.Env) psi.Environment[kv.Key] = kv.Value;
        psi.Environment["KUBERNETES_EXEC_INFO"] = BuildExecInfo();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var hint = string.IsNullOrWhiteSpace(exec.InstallHint) ? "" : $"\n{exec.InstallHint}";
            throw new KubeconfigException($"Could not start credential plugin '{exec.Command}': {ex.Message}{hint}", ex);
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new TimeoutException($"Credential plugin '{exec.Command}' timed out.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new KubeconfigException($"Credential plugin '{exec.Command}' failed (exit {process.ExitCode}): {Truncate(stderr, 500)}");
            }
            return ParseExecCredential(stdout);
        }
    }

    internal static Credential ParseExecCredential(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new KubeconfigException("Credential plugin returned invalid JSON.", ex);
        }

        var status = node?["status"] ?? throw new KubeconfigException("Credential plugin output has no status.");
        var token = status["token"]?.GetValue<string>();
        var certPem = status["clientCertificateData"]?.GetValue<string>();
        var keyPem = status["clientKeyData"]?.GetValue<string>();
        DateTimeOffset? expires = DateTimeOffset.TryParse(status["expirationTimestamp"]?.GetValue<string>(), out var e) ? e : null;

        X509Certificate2? cert = null;
        if (!string.IsNullOrEmpty(certPem) && !string.IsNullOrEmpty(keyPem))
        {
            cert = TlsConfig.LoadClientCertificate(Encoding.ASCII.GetBytes(certPem), Encoding.ASCII.GetBytes(keyPem));
        }
        if (string.IsNullOrEmpty(token) && cert is null)
        {
            throw new KubeconfigException("Credential plugin returned neither a token nor a client certificate.");
        }
        return new Credential(string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token), cert, expires);
    }

    private string BuildExecInfo()
    {
        var spec = new JsonObject { ["interactive"] = false };
        if (exec.ProvideClusterInfo)
        {
            var c = new JsonObject { ["server"] = cluster.Server, ["insecure-skip-tls-verify"] = cluster.InsecureSkipTlsVerify };
            if (cluster.CertificateAuthorityData is not null) c["certificate-authority-data"] = Convert.ToBase64String(cluster.CertificateAuthorityData);
            if (cluster.TlsServerName is not null) c["tls-server-name"] = cluster.TlsServerName;
            spec["cluster"] = c;
        }
        return new JsonObject { ["apiVersion"] = exec.ApiVersion, ["kind"] = "ExecCredential", ["spec"] = spec }.ToJsonString();
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (sb.Length + read > MaxOutputBytes) throw new KubeconfigException("Credential plugin output is too large.");
            sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s.Trim() : s[..max].Trim() + "…";
}

public static class CredentialSourceFactory
{
    public static ICredentialSource Create(ResolvedContext ctx, SecurityPolicy policy, IExecApprover? approver)
    {
        var user = ctx.User;
        X509Certificate2? cert = null;
        if (user.ClientCertificateData is not null && user.ClientKeyData is not null)
        {
            cert = TlsConfig.LoadClientCertificate(user.ClientCertificateData, user.ClientKeyData);
        }

        if (user.Exec is { } exec)
        {
            if (!policy.AllowExecPlugins || approver is null)
            {
                throw new SecurityPolicyException("Exec credential plugins are disabled by the current security policy.");
            }
            return new ExecCredentialSource(exec, ctx.Cluster, approver);
        }

        if (!string.IsNullOrEmpty(user.Token))
        {
            return new StaticCredentialSource(new Credential(new AuthenticationHeaderValue("Bearer", user.Token), cert, null));
        }

        if (!string.IsNullOrEmpty(user.TokenFile))
        {
            return new TokenFileCredentialSource(user.TokenFile);
        }

        if (user.AuthProvider is { Name: "oidc" } oidc && oidc.Config.TryGetValue("id-token", out var idToken) && !string.IsNullOrEmpty(idToken))
        {
            return new StaticCredentialSource(new Credential(new AuthenticationHeaderValue("Bearer", idToken), cert, null));
        }

        if (!string.IsNullOrEmpty(user.Username))
        {
            if (!policy.AllowBasicAuth)
            {
                throw new SecurityPolicyException("This context uses HTTP basic auth, which is disabled by the current security policy.");
            }
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user.Username}:{user.Password}"));
            return new StaticCredentialSource(new Credential(new AuthenticationHeaderValue("Basic", basic), cert, null));
        }

        return new StaticCredentialSource(new Credential(null, cert, null));
    }
}
