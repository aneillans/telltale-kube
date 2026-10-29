using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using TelltaleKube.Core.Config;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Core.Http;

/// <summary>Builds the single HTTP pipeline (TLS → read-only guard → auth) shared by REST, watch and WebSocket calls.</summary>
public static class KubeHttpPipeline
{
    public static HttpMessageInvoker Create(ResolvedContext ctx, SecurityPolicy policy, IExecApprover? approver, out Uri baseUri)
    {
        var cluster = ctx.Cluster;
        if (!Uri.TryCreate(cluster.Server, UriKind.Absolute, out var server) || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
        {
            throw new KubeconfigException($"Cluster '{cluster.Name}' has an invalid server URL '{cluster.Server}'.");
        }
        if (server.Scheme == Uri.UriSchemeHttp && !IsLoopback(server))
        {
            throw new SecurityPolicyException($"Refusing plain-HTTP connection to non-local API server '{server.Host}'.");
        }
        if (cluster.InsecureSkipTlsVerify && !policy.AllowInsecureTls)
        {
            throw new SecurityPolicyException(
                $"Cluster '{cluster.Name}' sets insecure-skip-tls-verify. Enable 'Allow insecure TLS' for this cluster to connect anyway.");
        }

        baseUri = new Uri(server.AbsoluteUri.TrimEnd('/') + "/");
        var credentials = CredentialSourceFactory.Create(ctx, policy, approver);
        var ca = cluster.CertificateAuthorityData is { } caData ? TlsConfig.LoadCertificates(caData) : null;
        var handler = CreateSocketsHandler(cluster, ca, policy, credentials);

        HttpMessageHandler pipeline = new AuthHandler(credentials) { InnerHandler = handler };
        if (policy.ReadOnly) pipeline = new ReadOnlyGuardHandler { InnerHandler = pipeline };
        return new HttpMessageInvoker(pipeline, disposeHandler: true);
    }

    private static SocketsHttpHandler CreateSocketsHandler(NamedCluster cluster, X509Certificate2Collection? ca, SecurityPolicy policy, ICredentialSource credentials)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = false, // never forward bearer tokens to a redirect target
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
#pragma warning disable CA5398 // Explicit floor: TLS 1.2+ only.
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore CA5398
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = TlsConfig.CreateValidator(ca, cluster.InsecureSkipTlsVerify && policy.AllowInsecureTls),
                LocalCertificateSelectionCallback = (_, _, _, _, _) => CurrentClientCertificate(credentials)!,
            },
        };
        if (!string.IsNullOrEmpty(cluster.TlsServerName)) handler.SslOptions.TargetHost = cluster.TlsServerName;
        if (!string.IsNullOrEmpty(cluster.ProxyUrl))
        {
            handler.Proxy = new WebProxy(new Uri(cluster.ProxyUrl));
            handler.UseProxy = true;
        }
        return handler;
    }

    private static X509Certificate2? CurrentClientCertificate(ICredentialSource credentials)
    {
        // TLS handshakes are synchronous callbacks; static/cached sources complete synchronously.
        var task = credentials.GetAsync(CancellationToken.None);
        return task.IsCompletedSuccessfully ? task.Result.ClientCertificate : task.AsTask().GetAwaiter().GetResult().ClientCertificate;
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}

internal sealed class AuthHandler(ICredentialSource credentials) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await ApplyAsync(request, ct).ConfigureAwait(false);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !IsReplayable(request)) return response;

        // Credentials may have rotated or expired: refresh once and retry.
        response.Dispose();
        credentials.Invalidate();
        await ApplyAsync(request, ct).ConfigureAwait(false);
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var credential = await credentials.GetAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = credential.Authorization;
    }

    private static bool IsReplayable(HttpRequestMessage request) =>
        request.Content is null or ByteArrayContent && request.Headers.Upgrade.Count == 0 && request.Headers.Connection.Count == 0;
}

/// <summary>Defence in depth for read-only clusters: mutating requests are blocked before they leave the device.</summary>
internal sealed partial class ReadOnlyGuardHandler : DelegatingHandler
{
    private static readonly string[] BlockedSubresources = ["/exec", "/attach", "/portforward", "/proxy", "/eviction"];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var mutating = request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options;
        // The one proxy call allowed read-only: the kubelet's resource summary (a fixed, side-effect-free GET).
        var isStatsSummary = request.Method == HttpMethod.Get && StatsSummaryPath().IsMatch(path);
        var sensitive = !isStatsSummary && BlockedSubresources.Any(s => path.Contains(s, StringComparison.Ordinal));
        if (mutating || sensitive)
        {
            throw new SecurityPolicyException($"This cluster is in read-only mode; '{request.Method} {path}' was blocked.");
        }
        return base.SendAsync(request, ct);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^/api/v1/nodes/[A-Za-z0-9.\-]+/proxy/stats/summary$")]
    private static partial System.Text.RegularExpressions.Regex StatsSummaryPath();
}
