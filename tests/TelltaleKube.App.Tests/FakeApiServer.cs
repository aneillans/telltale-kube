using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TelltaleKube.App.Tests;

/// <summary>
/// A tiny in-process Kubernetes API server (plain HTTP on loopback) good enough to drive the real UI end-to-end:
/// discovery, lists, watches, get, delete and scale.
/// </summary>
public sealed class FakeApiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public FakeApiServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = LoopAsync();
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public List<string> Requests { get; } = [];
    public string? LastAuthorization { get; private set; }

    private readonly BatchNamespace _batch = new();

    public IReadOnlyList<JsonObject> CreatedJobs => _batch.Created;

    public bool MetricsAvailable { get; set; } = true;

    /// <summary>Increments on every metrics scrape so usage changes between samples.</summary>
    private int _scrape;

    public string Kubeconfig(string token = "test-token") => $"""
        apiVersion: v1
        kind: Config
        current-context: fake
        clusters:
        - name: fake
          cluster:
            server: {Url}
        users:
        - name: tester
          user:
            token: {token}
        contexts:
        - name: fake
          context:
            cluster: fake
            user: tester
            namespace: shop
        """;

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url!.AbsolutePath;
        lock (Requests) Requests.Add($"{req.HttpMethod} {path}{req.Url.Query}");
        LastAuthorization = req.Headers["Authorization"];
        try
        {
            if (req.QueryString["watch"] is "1" or "true")
            {
                // Hold the watch open (no events) until the client goes away.
                ctx.Response.ContentType = "application/json";
                ctx.Response.SendChunked = true;
                await ctx.Response.OutputStream.FlushAsync();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                ctx.Response.Close();
                return;
            }

            string? requestBody = null;
            if (req.HasEntityBody)
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                requestBody = await reader.ReadToEndAsync();
            }
            var (status, body) = path.Contains("/namespaces/batch/", StringComparison.Ordinal) && !path.EndsWith("/log", StringComparison.Ordinal)
                ? _batch.Route(req.HttpMethod, path, req.QueryString["labelSelector"], requestBody)
                : Route(req.HttpMethod, path, req.QueryString["container"], req.QueryString["previous"] == "true");
            var bytes = Encoding.UTF8.GetBytes(body is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : body.ToJsonString());
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = body is JsonValue ? "text/plain" : "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
        catch (HttpListenerException)
        {
        }
    }

    private (int, JsonNode) Route(string method, string path, string? container, bool previous)
    {
        if (path.StartsWith("/apis/metrics.k8s.io/", StringComparison.Ordinal)) return Metrics(path);
        if (path.Contains("/proxy/", StringComparison.Ordinal)) return (403, new JsonObject { ["kind"] = "Status", ["message"] = "nodes/proxy is forbidden" });
        if (path.EndsWith("/log", StringComparison.Ordinal)) return Logs(path, container, previous);
        if (method == "DELETE" || method == "PATCH" || method == "PUT") return (200, new JsonObject { ["kind"] = "Status", ["status"] = "Success" });
        if (method == "POST") return (201, new JsonObject { ["status"] = new JsonObject { ["allowed"] = true } });
        return path switch
        {
            "/version" => (200, new JsonObject { ["gitVersion"] = "v1.34.1" }),
            "/apis" => (200, ApiGroups()),
            _ when CustomResources.ContainsKey(path) => (200, ApiResources(CustomResources[path])),
            "/apis/apiextensions.k8s.io/v1/customresourcedefinitions" => (200, JsonNode.Parse(CrdList)!),
            "/apis/cert-manager.io/v1/certificaterequests" or "/apis/cert-manager.io/v1/namespaces/shop/certificaterequests" => (200, List(
                CertificateRequest("web-tls-1", "letsencrypt", approved: true, ready: "True", "Certificate fetched from issuer successfully"),
                CertificateRequest("api-tls-1", "letsencrypt", approved: true, ready: "False", "Failed to wait for order resource to be ready"),
                CertificateRequest("internal-ca-7", "internal-ca", approved: false, ready: "Unknown", "Waiting on certificate issuance"))),
            "/api/v1/namespaces" => (200, List(Ns("default"), Ns("shop"), Ns("db"), Ns("batch"), Ns("kube-system"))),
            "/api/v1/namespaces/db/pods" => (200, List(SidecarPod())),
            "/api/v1/nodes" => (200, List(Node("node-a", true), Node("node-b", true), Node("node-c", false))),
            "/api/v1/pods" or "/api/v1/namespaces/shop/pods" => (200, List(
                Pod("web-6f9c7d-abcde", "Running", true, 0),
                Pod("web-6f9c7d-fghij", "Running", true, 1),
                Pod("worker-5d8b9-klmno", "Running", false, 14, "CrashLoopBackOff"),
                Pod("migrate-xyz12", "Pending", false, 0))),
            "/apis/apps/v1/deployments" or "/apis/apps/v1/namespaces/shop/deployments" => (200, List(Deploy("web", 2, 2), Deploy("worker", 1, 0))),
            "/api/v1/events" or "/api/v1/namespaces/shop/events" => (200, List(Event("BackOff", "Back-off restarting failed container worker"))),
            "/api/v1/namespaces/shop/pods/web-6f9c7d-abcde" => (200, Pod("web-6f9c7d-abcde", "Running", true, 0)),
            _ when path.StartsWith("/apis/metrics", StringComparison.Ordinal) => (404, new JsonObject { ["message"] = "not found" }),
            _ => (200, List()),
        };
    }

    /// <summary>Discovery: CRD groups from a few products, plus built-in groups discovery must skip.</summary>
    private static readonly Dictionary<string, (string Plural, string Kind, string? Short)[]> CustomResources = new(StringComparer.Ordinal)
    {
        ["/apis/longhorn.io/v1beta2"] = [("volumes", "Volume", "lhv"), ("engines", "Engine", "lhe"), ("nodes", "Node", "lhn")],
        ["/apis/helm.cattle.io/v1"] = [("helmcharts", "HelmChart", null), ("helmchartconfigs", "HelmChartConfig", null)],
        ["/apis/source.toolkit.fluxcd.io/v1"] = [("gitrepositories", "GitRepository", "gitrepo")],
        ["/apis/helm.toolkit.fluxcd.io/v2"] = [("helmreleases", "HelmRelease", "hr")],
        ["/apis/gateway.networking.k8s.io/v1"] = [("gateways", "Gateway", "gtw"), ("httproutes", "HTTPRoute", null)],
        ["/apis/example.com/v1"] = [("widgets", "Widget", null)],
        ["/apis/cert-manager.io/v1"] = [("certificaterequests", "CertificateRequest", "cr"), ("certificates", "Certificate", "cert")],
    };

    /// <summary>The printer columns cert-manager's CRDs declare (schemas trimmed).</summary>
    private const string CrdList = """
        { "kind": "CustomResourceDefinitionList", "items": [
          { "metadata": { "name": "certificaterequests.cert-manager.io" },
            "spec": { "group": "cert-manager.io", "names": { "plural": "certificaterequests", "kind": "CertificateRequest" }, "scope": "Namespaced",
              "versions": [ { "name": "v1", "served": true, "storage": true, "additionalPrinterColumns": [
                { "jsonPath": ".status.conditions[?(@.type == \"Approved\")].status", "name": "Approved", "type": "string" },
                { "jsonPath": ".status.conditions[?(@.type == \"Denied\")].status", "name": "Denied", "type": "string" },
                { "jsonPath": ".status.conditions[?(@.type == \"Ready\")].status", "name": "Ready", "type": "string" },
                { "jsonPath": ".spec.issuerRef.name", "name": "Issuer", "type": "string" },
                { "jsonPath": ".spec.username", "name": "Requester", "type": "string" },
                { "jsonPath": ".status.conditions[?(@.type == \"Ready\")].message", "name": "Status", "priority": 1, "type": "string" },
                { "jsonPath": ".metadata.creationTimestamp", "name": "Age", "type": "date" } ] } ] } },
          { "metadata": { "name": "certificates.cert-manager.io" },
            "spec": { "group": "cert-manager.io", "names": { "plural": "certificates", "kind": "Certificate" }, "scope": "Namespaced",
              "versions": [ { "name": "v1", "served": true, "storage": true, "additionalPrinterColumns": [
                { "jsonPath": ".status.conditions[?(@.type == \"Ready\")].status", "name": "Ready", "type": "string" },
                { "jsonPath": ".spec.secretName", "name": "Secret", "type": "string" },
                { "jsonPath": ".spec.issuerRef.name", "name": "Issuer", "priority": 1, "type": "string" },
                { "jsonPath": ".status.conditions[?(@.type == \"Ready\")].message", "name": "Status", "priority": 1, "type": "string" },
                { "jsonPath": ".metadata.creationTimestamp", "name": "Age", "type": "date" } ] } ] } }
        ] }
        """;

    private static JsonObject CertificateRequest(string name, string issuer, bool approved, string ready, string message) => new()
    {
        ["apiVersion"] = "cert-manager.io/v1",
        ["kind"] = "CertificateRequest",
        ["metadata"] = Meta(name),
        ["spec"] = new JsonObject
        {
            ["issuerRef"] = new JsonObject { ["name"] = issuer, ["kind"] = "ClusterIssuer" },
            ["username"] = "system:serviceaccount:cert-manager:cert-manager",
        },
        ["status"] = new JsonObject
        {
            ["conditions"] = new JsonArray(
                new JsonObject[] { approved ? new() { ["type"] = "Approved", ["status"] = "True" } : null!, new() { ["type"] = "Ready", ["status"] = ready, ["message"] = message } }
                    .Where(c => c is not null).ToArray()),
        },
    };

    private static JsonObject ApiGroups()
    {
        var groups = new JsonArray();
        foreach (var gv in CustomResources.Keys.Select(k => k["/apis/".Length..]).Append("flowcontrol.apiserver.k8s.io/v1").Append("storage.k8s.io/v1"))
        {
            groups.Add(new JsonObject { ["name"] = gv[..gv.IndexOf('/', StringComparison.Ordinal)], ["preferredVersion"] = new JsonObject { ["groupVersion"] = gv } });
        }
        return new JsonObject { ["groups"] = groups };
    }

    private static JsonObject ApiResources((string Plural, string Kind, string? Short)[] kinds) => new()
    {
        ["resources"] = new JsonArray(kinds.SelectMany(k => new JsonNode[]
        {
            new JsonObject
            {
                ["name"] = k.Plural, ["kind"] = k.Kind, ["namespaced"] = true, ["verbs"] = new JsonArray("get", "list", "watch"),
                ["shortNames"] = k.Short is null ? null : new JsonArray(k.Short),
            },
            new JsonObject { ["name"] = k.Plural + "/status", ["kind"] = k.Kind, ["namespaced"] = true, ["verbs"] = new JsonArray("get") },
        }).ToArray()),
    };

    /// <summary>Plain-text log lines per container; previous logs exist only for the restarted fluent-bit sidecar.</summary>
    private static (int, JsonNode) Logs(string path, string? container, bool previous)
    {
        var pod = path.Split('/')[^2];
        if (previous && container != "fluent-bit")
        {
            return (400, new JsonObject { ["kind"] = "Status", ["message"] = $"previous terminated container \"{container}\" in pod \"{pod}\" not found" });
        }
        var text = previous
            ? $"{container} starting (previous)\n{container} out of memory, exiting\n"
            : string.Concat(Enumerable.Range(1, 3).Select(i => $"{container} line {i}\n"));
        return (200, JsonValue.Create(text));
    }

    /// <summary>Modelled on a real StatefulSet pod: 4 classic sidecar-style containers + a native sidecar, statuses in name order.</summary>
    private static JsonObject SidecarPod()
    {
        var meta = Meta("mariadb-1-0", "db");
        meta["generateName"] = "mariadb-1-";
        meta["labels"] = new JsonObject { ["io.kompose.service"] = "mariadb-1", ["statefulset.kubernetes.io/pod-name"] = "mariadb-1-0" };
        static JsonObject C(string name, string image) => new() { ["name"] = name, ["image"] = image };
        static JsonObject S(string name, int restarts = 0, JsonObject? last = null) => new()
        {
            ["name"] = name,
            ["ready"] = true,
            ["restartCount"] = restarts,
            ["state"] = new JsonObject { ["running"] = new JsonObject { ["startedAt"] = "2026-10-21T16:30:15Z" } },
            ["lastState"] = last ?? new JsonObject(),
        };
        var oom = new JsonObject { ["terminated"] = new JsonObject { ["reason"] = "OOMKilled", ["exitCode"] = 137, ["finishedAt"] = DateTimeOffset.UtcNow.AddMinutes(-7).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) } };
        return new JsonObject
        {
            ["metadata"] = meta,
            ["spec"] = new JsonObject
            {
                ["nodeName"] = "k3s-node2",
                ["initContainers"] = new JsonArray(new JsonObject { ["name"] = "galera-init", ["image"] = "busybox" }, new JsonObject { ["name"] = "mesh-proxy", ["image"] = "envoyproxy/envoy:v1.36", ["restartPolicy"] = "Always" }),
                ["containers"] = new JsonArray(C("mariadb-1", "mariadb:12.3"), C("mariadb-1-healthcheck", "alpine:3.24"), C("fluent-bit", "fluent/fluent-bit:5.1.5"), C("slow-log-rotate", "mariadb:12.3")),
            },
            ["status"] = new JsonObject
            {
                ["phase"] = "Running",
                ["podIP"] = "10.42.0.115",
                ["initContainerStatuses"] = new JsonArray(
                    new JsonObject { ["name"] = "galera-init", ["ready"] = false, ["restartCount"] = 0, ["state"] = new JsonObject { ["terminated"] = new JsonObject { ["reason"] = "Completed", ["exitCode"] = 0 } } },
                    S("mesh-proxy")),
                ["containerStatuses"] = new JsonArray(S("fluent-bit", 2, oom), S("mariadb-1"), S("mariadb-1-healthcheck"), S("slow-log-rotate")),
            },
        };
    }

    /// <summary>metrics.k8s.io: node and pod usage; worker pods are the hungry ones.</summary>
    private (int, JsonNode) Metrics(string path)
    {
        if (!MetricsAvailable) return (404, new JsonObject { ["kind"] = "Status", ["message"] = "the server could not find the requested resource" });
        var tick = Interlocked.Increment(ref _scrape);
        if (path.EndsWith("/nodes", StringComparison.Ordinal))
        {
            JsonObject N(string name, int cpuMillis, string mem) =>
                new() { ["metadata"] = new JsonObject { ["name"] = name }, ["usage"] = new JsonObject { ["cpu"] = $"{cpuMillis + (tick * 37 % 400)}m", ["memory"] = mem } };
            return (200, List(N("node-a", 2400, "11Gi"), N("node-b", 600, "3Gi")));
        }
        JsonObject P(string name, string cpu, string mem) => new()
        {
            ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = "shop" },
            ["containers"] = new JsonArray(new JsonObject { ["name"] = "app", ["usage"] = new JsonObject { ["cpu"] = cpu, ["memory"] = mem } }),
        };
        var pods = new[] { P("web-6f9c7d-abcde", "120m", "180Mi"), P("web-6f9c7d-fghij", "80m", "150Mi"), P("worker-5d8b9-klmno", "900m", "480Mi") };
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // .../namespaces/shop/pods/<name>
        if (segments.Length == 7) return pods.FirstOrDefault(p => p["metadata"]!["name"]!.ToString() == segments[6]) is { } one ? (200, one) : (404, new JsonObject { ["message"] = "not found" });
        return (200, List(pods));
    }

    private static JsonObject List(params JsonObject[] items) =>
        new() { ["kind"] = "List", ["metadata"] = new JsonObject { ["resourceVersion"] = "100" }, ["items"] = new JsonArray(items) };

    private static JsonObject Meta(string name, string? ns = "shop") => new()
    {
        ["name"] = name,
        ["namespace"] = ns,
        ["uid"] = Guid.NewGuid().ToString(),
        ["resourceVersion"] = "1",
        ["creationTimestamp"] = DateTimeOffset.UtcNow.AddHours(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["labels"] = new JsonObject { ["app"] = name.Split('-')[0] },
    };

    private static JsonObject Ns(string name)
    {
        var m = Meta(name, null);
        m.Remove("namespace");
        return new JsonObject { ["metadata"] = m, ["status"] = new JsonObject { ["phase"] = "Active" } };
    }

    private static JsonObject Node(string name, bool ready)
    {
        var m = Meta(name, null);
        m.Remove("namespace");
        m["labels"] = new JsonObject { ["node-role.kubernetes.io/worker"] = "" };
        return new JsonObject
        {
            ["metadata"] = m,
            ["status"] = new JsonObject
            {
                ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = ready ? "True" : "False" }),
                ["nodeInfo"] = new JsonObject { ["kubeletVersion"] = "v1.34.1", ["osImage"] = "Ubuntu 24.04", ["architecture"] = "amd64" },
                ["allocatable"] = new JsonObject { ["cpu"] = "4", ["memory"] = "16Gi", ["pods"] = "110" },
            },
        };
    }

    private static JsonObject Pod(string name, string phase, bool ready, int restarts, string? waiting = null)
    {
        var state = waiting is null ? new JsonObject { ["running"] = new JsonObject() } : new JsonObject { ["waiting"] = new JsonObject { ["reason"] = waiting } };
        return new JsonObject
        {
            ["metadata"] = Meta(name),
            ["spec"] = new JsonObject
            {
                ["nodeName"] = "node-a",
                ["containers"] = new JsonArray(new JsonObject
                {
                    ["name"] = "app",
                    ["image"] = "ghcr.io/example/app:1.2.3",
                    ["ports"] = new JsonArray(new JsonObject { ["containerPort"] = 8080 }),
                    ["resources"] = new JsonObject
                    {
                        ["requests"] = new JsonObject { ["cpu"] = "250m", ["memory"] = "256Mi" },
                        ["limits"] = new JsonObject { ["memory"] = "512Mi" },
                    },
                }),
            },
            ["status"] = new JsonObject
            {
                ["phase"] = phase,
                ["podIP"] = "10.0.0.12",
                ["containerStatuses"] = new JsonArray(new JsonObject { ["name"] = "app", ["ready"] = ready, ["restartCount"] = restarts, ["state"] = state }),
            },
        };
    }

    private static JsonObject Deploy(string name, int want, int ready) => new()
    {
        ["metadata"] = Meta(name),
        ["spec"] = new JsonObject { ["replicas"] = want },
        ["status"] = new JsonObject { ["readyReplicas"] = ready, ["updatedReplicas"] = want, ["availableReplicas"] = ready },
    };

    private static JsonObject Event(string reason, string message) => new()
    {
        ["metadata"] = Meta("ev-" + reason.ToLowerInvariant()),
        ["type"] = "Warning",
        ["reason"] = reason,
        ["message"] = message,
        ["lastTimestamp"] = DateTimeOffset.UtcNow.AddMinutes(-3).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["involvedObject"] = new JsonObject { ["kind"] = "Pod", ["name"] = "worker-5d8b9-klmno" },
    };

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        _cts.Dispose();
    }
}
