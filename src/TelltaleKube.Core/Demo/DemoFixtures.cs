using System.Globalization;
using System.Text.Json.Nodes;

namespace TelltaleKube.Core.Demo;

/// <summary>
/// The demo cluster's sample objects: a small shop with a crash-looping worker, a pod that can't schedule and a
/// node that is down. Built fresh per request because a <see cref="JsonNode"/> can only have one parent.
/// </summary>
internal static class DemoFixtures
{
    private sealed record PodInfo(string Namespace, string Name, string App, string Node, string Phase, bool Ready, int Restarts, string? Waiting, int CpuMillis, int MemoryMi);

    private static readonly PodInfo[] Pods =
    [
        new("shop", "web-6f9c7d-abcde", "web", "worker-1", "Running", true, 0, null, 120, 180),
        new("shop", "web-6f9c7d-fghij", "web", "worker-2", "Running", true, 1, null, 80, 150),
        new("shop", "api-7c4d8f-pqrst", "api", "worker-1", "Running", true, 0, null, 310, 420),
        new("shop", "api-7c4d8f-uvwxy", "api", "worker-2", "Running", true, 0, null, 280, 390),
        new("shop", "worker-5d8b9-klmno", "worker", "worker-1", "Running", false, 14, "CrashLoopBackOff", 15, 60),
        new("shop", "migrate-xyz12", "migrate", "", "Pending", false, 0, null, 0, 0),
        new("monitoring", "prometheus-0", "prometheus", "worker-2", "Running", true, 0, null, 450, 1800),
        new("monitoring", "grafana-84b6c-mnbvc", "grafana", "worker-1", "Running", true, 0, null, 60, 210),
        new("kube-system", "coredns-5688667-qwert", "coredns", "control-plane", "Running", true, 0, null, 20, 70),
    ];

    public static JsonObject[] Items(string plural) => plural switch
    {
        "namespaces" => [Namespace("default"), Namespace("shop"), Namespace("monitoring"), Namespace("kube-system")],
        "nodes" => [Node("control-plane", true, "control-plane"), Node("worker-1", true, "worker"), Node("worker-2", true, "worker"), Node("worker-3", false, "worker")],
        "pods" => [.. Pods.Select(Pod)],
        "deployments" =>
        [
            Workload("shop", "web", 2, 2),
            Workload("shop", "api", 2, 2),
            Workload("shop", "worker", 1, 0),
            Workload("monitoring", "grafana", 1, 1),
            Workload("kube-system", "coredns", 1, 1),
        ],
        "statefulsets" => [Workload("monitoring", "prometheus", 1, 1)],
        "services" => [Service("shop", "web", 80), Service("shop", "api", 8080), Service("monitoring", "grafana", 3000), Service("monitoring", "prometheus", 9090)],
        "events" =>
        [
            Event("shop", "BackOff", "Back-off restarting failed container app in pod worker-5d8b9-klmno", "worker-5d8b9-klmno"),
            Event("shop", "FailedScheduling", "0/4 nodes are available: 1 node(s) were not ready, 3 Insufficient memory.", "migrate-xyz12"),
        ],
        "certificates" => [Certificate("web-tls", "web-tls-secret", "True", "Certificate is up to date and has not expired"), Certificate("api-tls", "api-tls-secret", "False", "Issuing certificate as Secret does not exist")],
        "customresourcedefinitions" => [CertificateCrd()],
        _ => [],
    };

    public static JsonObject[] Metrics(string plural, int tick) => plural switch
    {
        "nodes" =>
        [
            NodeUsage("control-plane", 300 + (tick * 13 % 120), "2Gi"),
            NodeUsage("worker-1", 1400 + (tick * 37 % 400), "9Gi"),
            NodeUsage("worker-2", 900 + (tick * 23 % 300), "7Gi"),
        ],
        "pods" => [.. Pods.Where(p => p.Phase == "Running").Select(PodUsage)],
        _ => [],
    };

    public static JsonObject ApiGroups() => new()
    {
        ["groups"] = new JsonArray(new JsonObject { ["name"] = "cert-manager.io", ["preferredVersion"] = new JsonObject { ["groupVersion"] = "cert-manager.io/v1" } }),
    };

    public static JsonObject CertManagerResources() => new()
    {
        ["resources"] = new JsonArray(
            new JsonObject { ["name"] = "certificates", ["kind"] = "Certificate", ["namespaced"] = true, ["verbs"] = new JsonArray("get", "list", "watch"), ["shortNames"] = new JsonArray("cert") },
            new JsonObject { ["name"] = "certificates/status", ["kind"] = "Certificate", ["namespaced"] = true, ["verbs"] = new JsonArray("get") }),
    };

    public static string[] LogLines(string pod) => pod.Split('-')[0] switch
    {
        "web" => ["listening on :8080", "GET /healthz 200 1ms", "GET / 200 12ms", "GET /products 200 48ms", "GET /basket 200 21ms", "GET /healthz 200 1ms"],
        "api" => ["connected to database", "listening on :8080", "POST /orders 201 84ms", "GET /orders/1042 200 9ms", "GET /stock 200 17ms"],
        "worker" => ["starting worker", "connecting to queue amqp://queue.shop:5672", "error: connection refused", "fatal: giving up after 5 attempts"],
        "prometheus" => ["Server is ready to receive web requests.", "Completed loading of configuration file", "WAL checkpoint complete"],
        _ => ["started", "ready"],
    };

    private static string Uid(string? ns, string name) => $"demo-{ns ?? "cluster"}-{name}";

    private static string Ago(TimeSpan age) => (DateTimeOffset.UtcNow - age).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static JsonObject Meta(string? ns, string name, string app)
    {
        var meta = new JsonObject
        {
            ["name"] = name,
            ["uid"] = Uid(ns, name),
            ["resourceVersion"] = "1",
            ["creationTimestamp"] = Ago(TimeSpan.FromDays(3)),
            ["labels"] = new JsonObject { ["app"] = app },
        };
        if (ns is not null) meta["namespace"] = ns;
        return meta;
    }

    private static JsonObject Namespace(string name) => new()
    {
        ["metadata"] = Meta(null, name, name),
        ["status"] = new JsonObject { ["phase"] = "Active" },
    };

    private static JsonObject Node(string name, bool ready, string role)
    {
        var meta = Meta(null, name, "node");
        meta["labels"] = new JsonObject { [$"node-role.kubernetes.io/{role}"] = "" };
        return new JsonObject
        {
            ["metadata"] = meta,
            ["status"] = new JsonObject
            {
                ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = ready ? "True" : "False" }),
                ["nodeInfo"] = new JsonObject { ["kubeletVersion"] = "v1.34.1", ["osImage"] = "Ubuntu 24.04", ["architecture"] = "amd64" },
                ["allocatable"] = new JsonObject { ["cpu"] = "4", ["memory"] = "16Gi", ["pods"] = "110" },
            },
        };
    }

    private static JsonObject Pod(PodInfo p)
    {
        var state = p.Waiting is { } reason
            ? new JsonObject { ["waiting"] = new JsonObject { ["reason"] = reason } }
            : new JsonObject { ["running"] = new JsonObject { ["startedAt"] = Ago(TimeSpan.FromHours(5)) } };
        var status = new JsonObject { ["phase"] = p.Phase };
        if (p.Phase == "Running")
        {
            status["podIP"] = "10.42.0." + (Math.Abs(p.Name.Length * 7) % 200 + 10).ToString(CultureInfo.InvariantCulture);
            status["containerStatuses"] = new JsonArray(new JsonObject { ["name"] = "app", ["ready"] = p.Ready, ["restartCount"] = p.Restarts, ["state"] = state });
        }
        var spec = new JsonObject
        {
            ["containers"] = new JsonArray(new JsonObject
            {
                ["name"] = "app",
                ["image"] = $"registry.example/{p.App}:1.2.3",
                ["ports"] = new JsonArray(new JsonObject { ["containerPort"] = 8080 }),
                ["resources"] = new JsonObject
                {
                    ["requests"] = new JsonObject { ["cpu"] = "250m", ["memory"] = "256Mi" },
                    ["limits"] = new JsonObject { ["memory"] = "512Mi" },
                },
            }),
        };
        if (p.Node.Length > 0) spec["nodeName"] = p.Node;
        return new JsonObject { ["metadata"] = Meta(p.Namespace, p.Name, p.App), ["spec"] = spec, ["status"] = status };
    }

    private static JsonObject Workload(string ns, string name, int want, int ready) => new()
    {
        ["metadata"] = Meta(ns, name, name),
        ["spec"] = new JsonObject
        {
            ["replicas"] = want,
            ["selector"] = new JsonObject { ["matchLabels"] = new JsonObject { ["app"] = name } },
        },
        ["status"] = new JsonObject { ["replicas"] = want, ["readyReplicas"] = ready, ["updatedReplicas"] = want, ["availableReplicas"] = ready },
    };

    private static JsonObject Service(string ns, string name, int port) => new()
    {
        ["metadata"] = Meta(ns, name, name),
        ["spec"] = new JsonObject
        {
            ["type"] = "ClusterIP",
            ["clusterIP"] = "10.43.0." + (port % 200 + 10).ToString(CultureInfo.InvariantCulture),
            ["ports"] = new JsonArray(new JsonObject { ["port"] = port, ["protocol"] = "TCP" }),
            ["selector"] = new JsonObject { ["app"] = name },
        },
    };

    private static JsonObject Event(string ns, string reason, string message, string pod) => new()
    {
        ["metadata"] = Meta(ns, $"{pod}.{reason.ToLowerInvariant()}", "event"),
        ["type"] = "Warning",
        ["reason"] = reason,
        ["message"] = message,
        ["count"] = 12,
        ["lastTimestamp"] = Ago(TimeSpan.FromMinutes(3)),
        ["involvedObject"] = new JsonObject { ["kind"] = "Pod", ["namespace"] = ns, ["name"] = pod, ["uid"] = Uid(ns, pod) },
    };

    private static JsonObject Certificate(string name, string secret, string ready, string message) => new()
    {
        ["apiVersion"] = "cert-manager.io/v1",
        ["kind"] = "Certificate",
        ["metadata"] = Meta("shop", name, "certificate"),
        ["spec"] = new JsonObject { ["secretName"] = secret, ["issuerRef"] = new JsonObject { ["name"] = "letsencrypt", ["kind"] = "ClusterIssuer" } },
        ["status"] = new JsonObject { ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = ready, ["message"] = message }) },
    };

    private static JsonObject CertificateCrd() => JsonNode.Parse("""
        { "metadata": { "name": "certificates.cert-manager.io" },
          "spec": { "group": "cert-manager.io", "names": { "plural": "certificates", "kind": "Certificate" }, "scope": "Namespaced",
            "versions": [ { "name": "v1", "served": true, "storage": true, "additionalPrinterColumns": [
              { "jsonPath": ".status.conditions[?(@.type == \"Ready\")].status", "name": "Ready", "type": "string" },
              { "jsonPath": ".spec.secretName", "name": "Secret", "type": "string" },
              { "jsonPath": ".metadata.creationTimestamp", "name": "Age", "type": "date" } ] } ] } }
        """)!.AsObject();

    private static JsonObject NodeUsage(string name, int cpuMillis, string memory) => new()
    {
        ["metadata"] = new JsonObject { ["name"] = name },
        ["usage"] = new JsonObject { ["cpu"] = $"{cpuMillis}m", ["memory"] = memory },
    };

    private static JsonObject PodUsage(PodInfo p) => new()
    {
        ["metadata"] = new JsonObject { ["name"] = p.Name, ["namespace"] = p.Namespace },
        ["containers"] = new JsonArray(new JsonObject { ["name"] = "app", ["usage"] = new JsonObject { ["cpu"] = $"{p.CpuMillis}m", ["memory"] = $"{p.MemoryMi}Mi" } }),
    };
}
