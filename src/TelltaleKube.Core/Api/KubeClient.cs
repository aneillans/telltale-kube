using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TelltaleKube.Core.Config;
using TelltaleKube.Core.Http;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Core.Api;

public sealed class KubeClient : IDisposable
{
    private const int MaxResponseBytes = 64 * 1024 * 1024;
    private readonly HttpMessageInvoker _invoker;
    private readonly HttpClient _http;

    public KubeClient(ResolvedContext context, SecurityPolicy policy, IExecApprover? approver = null)
    {
        Context = context;
        Policy = policy;
        _invoker = KubeHttpPipeline.Create(context, policy, approver, out var baseUri);
        BaseUri = baseUri;
        _http = new HttpClient(new InvokerHandler(_invoker), disposeHandler: false)
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan, // per-call timeouts; watches and log streams are long-lived
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TelltaleKube", typeof(KubeClient).Assembly.GetName().Version?.ToString(3) ?? "0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Test seam: drives the client through an arbitrary handler.</summary>
    internal KubeClient(HttpMessageHandler handler, Uri baseUri, SecurityPolicy? policy = null)
    {
        Policy = policy ?? SecurityPolicy.Default;
        Context = new ResolvedContext(new NamedContext { Name = "test" }, new NamedCluster { Name = "test", Server = baseUri.ToString() }, new NamedUser { Name = "test" });
        HttpMessageHandler h = Policy.ReadOnly ? new ReadOnlyGuardHandler { InnerHandler = handler } : handler;
        _invoker = new HttpMessageInvoker(h);
        BaseUri = baseUri;
        _http = new HttpClient(new InvokerHandler(_invoker), disposeHandler: false) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
    }

    public ResolvedContext Context { get; }
    public SecurityPolicy Policy { get; }
    public Uri BaseUri { get; }

    public async Task<string> GetServerVersionAsync(CancellationToken ct = default)
    {
        var v = await GetJsonAsync("version", ct).ConfigureAwait(false);
        return v?["gitVersion"]?.ToString() ?? "unknown";
    }

    /// <summary>Discovers CRD-backed and aggregated resource types via /apis (one request per group version).</summary>
    public async Task<IReadOnlyList<ResourceKind>> DiscoverCustomKindsAsync(CancellationToken ct = default)
    {
        var groups = await GetJsonAsync("apis", ct).ConfigureAwait(false);
        var builtInGroups = ResourceKinds.BuiltIn.Select(k => k.Group).ToHashSet(StringComparer.Ordinal);
        var result = new List<ResourceKind>();
        foreach (var g in groups?["groups"]?.AsArray() ?? [])
        {
            var name = g?["name"]?.ToString();
            var preferred = g?["preferredVersion"]?["groupVersion"]?.ToString();
            if (name is null || preferred is null || builtInGroups.Contains(name) || CustomResourceFamily.IsBuiltInGroup(name)) continue;
            JsonNode? list;
            try
            {
                list = await GetJsonAsync($"apis/{preferred}", ct).ConfigureAwait(false);
            }
            catch (KubeApiException)
            {
                continue; // unavailable aggregated API (e.g. broken metrics-server) must not break discovery
            }
            var version = preferred[(preferred.IndexOf('/', StringComparison.Ordinal) + 1)..];
            foreach (var r in list?["resources"]?.AsArray() ?? [])
            {
                var plural = r?["name"]?.ToString();
                if (plural is null || plural.Contains('/', StringComparison.Ordinal)) continue; // skip subresources
                var verbs = r?["verbs"]?.AsArray().Select(v => v?.ToString()).ToList() ?? [];
                if (!verbs.Contains("list")) continue;
                result.Add(new ResourceKind(r?["kind"]?.ToString() ?? plural, plural, name, version,
                    r?["namespaced"]?.GetValue<bool>() ?? true, ResourceCategory.CustomResources, $"{plural} ({name})",
                    r?["shortNames"]?.AsArray().FirstOrDefault()?.ToString()));
            }
        }
        return await WithPrinterColumnsAsync(result, ct).ConfigureAwait(false);
    }

    /// <summary>Adds each CRD's printer columns. Best-effort: users who can't list CRDs just get Name / Age.</summary>
    private async Task<IReadOnlyList<ResourceKind>> WithPrinterColumnsAsync(List<ResourceKind> kinds, CancellationToken ct)
    {
        if (kinds.Count == 0) return kinds;
        JsonNode? crds;
        try
        {
            crds = await GetJsonAsync(ResourceKinds.Crds.CollectionPath(null), ct).ConfigureAwait(false);
        }
        catch (KubeApiException)
        {
            return kinds;
        }
        var columns = PrinterColumns.FromCrds(crds, kinds.ToDictionary(k => k.Key, k => k.Version, StringComparer.Ordinal));
        return kinds.Select(k => columns.TryGetValue(k.Key, out var c) ? k with { PrinterColumns = c } : k).ToList();
    }

    public async Task<ResourceList> ListAsync(ResourceKind kind, string? ns = null, string? labelSelector = null, int? limit = null, string? fieldSelector = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(labelSelector)) query.Add("labelSelector=" + Uri.EscapeDataString(labelSelector));
        if (!string.IsNullOrEmpty(fieldSelector)) query.Add("fieldSelector=" + Uri.EscapeDataString(fieldSelector));
        if (limit is { } l) query.Add($"limit={l}");
        var path = kind.CollectionPath(ns) + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        var json = await GetJsonAsync(path, ct).ConfigureAwait(false) as JsonObject ?? throw new KubeApiException(HttpStatusCode.OK, "Empty list response.");
        var items = (json["items"] as JsonArray ?? []).OfType<JsonObject>().Select(o =>
        {
            // List items omit apiVersion/kind; restore them so YAML views and edits are complete.
            o["apiVersion"] ??= kind.ApiVersion;
            o["kind"] ??= kind.Kind;
            return new KubeObject(o);
        }).ToList();
        return new ResourceList(items, json["metadata"]?["resourceVersion"]?.ToString());
    }

    public async Task<KubeObject> GetAsync(ResourceKind kind, string? ns, string name, CancellationToken ct = default) =>
        new(await GetJsonAsync(kind.ItemPath(ns, name), ct).ConfigureAwait(false) as JsonObject ?? throw new KubeApiException(HttpStatusCode.NotFound, "Not found"));

    public Task DeleteAsync(ResourceKind kind, string? ns, string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, kind.ItemPath(ns, name), new JsonObject { ["kind"] = "DeleteOptions", ["apiVersion"] = "v1", ["propagationPolicy"] = "Background" }, null, ct);

    /// <summary>Replaces an object (PUT). resourceVersion in the body gives optimistic concurrency.</summary>
    public async Task<KubeObject> ReplaceAsync(ResourceKind kind, JsonObject body, CancellationToken ct = default)
    {
        var obj = new KubeObject(body);
        var res = await SendAsync(HttpMethod.Put, kind.ItemPath(obj.Namespace, obj.Name), body, null, ct).ConfigureAwait(false);
        return new KubeObject(res as JsonObject ?? body);
    }

    public async Task<KubeObject> ApplyAsync(ResourceKind kind, JsonObject body, bool force = false, CancellationToken ct = default)
    {
        var obj = new KubeObject(body);
        var path = kind.ItemPath(obj.Namespace, obj.Name) + $"?fieldManager=telltale-kube&force={(force ? "true" : "false")}";
        var res = await SendAsync(HttpMethod.Patch, path, body, "application/apply-patch+yaml", ct).ConfigureAwait(false);
        return new KubeObject(res as JsonObject ?? body);
    }

    public async Task<KubeObject> CreateAsync(ResourceKind kind, JsonObject body, CancellationToken ct = default)
    {
        var obj = new KubeObject(body);
        var res = await SendAsync(HttpMethod.Post, kind.CollectionPath(obj.Namespace), body, null, ct).ConfigureAwait(false);
        return new KubeObject(res as JsonObject ?? body);
    }

    public Task MergePatchAsync(ResourceKind kind, string? ns, string name, JsonObject patch, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, kind.ItemPath(ns, name), patch, "application/merge-patch+json", ct);

    public Task ScaleAsync(ResourceKind kind, string? ns, string name, int replicas, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replicas);
        return SendAsync(HttpMethod.Patch, kind.ItemPath(ns, name) + "/scale", new JsonObject { ["spec"] = new JsonObject { ["replicas"] = replicas } }, "application/merge-patch+json", ct);
    }

    public Task RolloutRestartAsync(ResourceKind kind, string? ns, string name, CancellationToken ct = default) =>
        MergePatchAsync(kind, ns, name, new JsonObject
        {
            ["spec"] = new JsonObject
            {
                ["template"] = new JsonObject
                {
                    ["metadata"] = new JsonObject
                    {
                        ["annotations"] = new JsonObject { ["kubectl.kubernetes.io/restartedAt"] = DateTimeOffset.UtcNow.ToString("O") },
                    },
                },
            },
        }, ct);

    public Task CordonAsync(string node, bool cordon, CancellationToken ct = default) =>
        MergePatchAsync(ResourceKinds.Nodes, null, node, new JsonObject { ["spec"] = new JsonObject { ["unschedulable"] = cordon } }, ct);

    /// <summary>Streams watch events. Throws <see cref="KubeApiException"/> with 410 when the resourceVersion is too old.</summary>
    public async IAsyncEnumerable<WatchEvent> WatchAsync(ResourceKind kind, string? ns, string? resourceVersion, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = kind.CollectionPath(ns) + "?watch=1&allowWatchBookmarks=true&timeoutSeconds=300"
            + (string.IsNullOrEmpty(resourceVersion) ? "" : "&resourceVersion=" + Uri.EscapeDataString(resourceVersion));
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            var node = JsonNode.Parse(line);
            var type = node?["type"]?.ToString() ?? "";
            if (node?["object"] is not JsonObject obj) continue;
            if (type == "ERROR")
            {
                var code = obj["code"]?.GetValue<int>() ?? 500;
                throw new KubeApiException((HttpStatusCode)code, obj["message"]?.ToString() ?? "watch error");
            }
            obj["apiVersion"] ??= kind.ApiVersion;
            obj["kind"] ??= kind.Kind;
            yield return new WatchEvent(type, new KubeObject(obj));
        }
    }

    public async IAsyncEnumerable<string> StreamLogsAsync(string ns, string pod, string? container, bool follow, int tailLines = 500, bool previous = false, bool timestamps = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = $"{ResourceKinds.Pods.ItemPath(ns, pod)}/log?tailLines={tailLines}&follow={(follow ? "true" : "false")}"
            + (previous ? "&previous=true" : "") + (timestamps ? "&timestamps=true" : "")
            + (string.IsNullOrEmpty(container) ? "" : "&container=" + Uri.EscapeDataString(container));
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            yield return line;
        }
    }

    public async Task<ExecSession> ExecAsync(string ns, string pod, string? container, IReadOnlyList<string> command, bool tty, CancellationToken ct = default)
    {
        var query = new StringBuilder("stdin=true&stdout=true&stderr=").Append(tty ? "false" : "true").Append("&tty=").Append(tty ? "true" : "false");
        if (!string.IsNullOrEmpty(container)) query.Append("&container=").Append(Uri.EscapeDataString(container));
        foreach (var c in command) query.Append("&command=").Append(Uri.EscapeDataString(c));
        var ws = await ConnectWebSocketAsync($"{ResourceKinds.Pods.ItemPath(ns, pod)}/exec?{query}", ["v5.channel.k8s.io", "v4.channel.k8s.io"], ct).ConfigureAwait(false);
        return new ExecSession(ws);
    }

    public async Task<WebSocket> OpenPortForwardAsync(string ns, string pod, int port, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return await ConnectWebSocketAsync($"{ResourceKinds.Pods.ItemPath(ns, pod)}/portforward?ports={port}", ["v4.channel.k8s.io"], ct).ConfigureAwait(false);
    }

    private async Task<WebSocket> ConnectWebSocketAsync(string relative, string[] protocols, CancellationToken ct)
    {
        var http = new Uri(BaseUri, relative);
        var builder = new UriBuilder(http) { Scheme = http.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" };
        var ws = new ClientWebSocket();
        foreach (var p in protocols) ws.Options.AddSubProtocol(p);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await ws.ConnectAsync(builder.Uri, _invoker, ct).ConfigureAwait(false);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        return ws;
    }

    internal async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Policy.RequestTimeout);
        using var response = await _http.GetAsync(path, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, string? contentType, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Policy.RequestTimeout);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            // apply-patch+yaml accepts JSON (JSON is valid YAML).
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
        }
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string message;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            message = (JsonNode.Parse(body)?["message"]?.ToString()) ?? body;
        }
        catch (JsonException)
        {
            message = response.ReasonPhrase ?? "request failed";
        }
        throw new KubeApiException(response.StatusCode, message.Length > 2000 ? message[..2000] : message);
    }

    public void Dispose()
    {
        _http.Dispose();
        _invoker.Dispose();
    }

    /// <summary>Adapts an <see cref="HttpMessageInvoker"/> so HttpClient and ClientWebSocket share one TLS/auth pipeline.</summary>
    private sealed class InvokerHandler(HttpMessageInvoker invoker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => invoker.SendAsync(request, ct);
    }
}

public sealed record ResourceList(IReadOnlyList<KubeObject> Items, string? ResourceVersion);

public sealed record WatchEvent(string Type, KubeObject Resource);

public sealed class KubeApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public bool IsGone => Status == HttpStatusCode.Gone;
    public bool IsForbidden => Status == HttpStatusCode.Forbidden;
}
