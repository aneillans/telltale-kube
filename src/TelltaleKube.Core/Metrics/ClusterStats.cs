using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Resources;

namespace TelltaleKube.Core.Metrics;

/// <summary>CPU (millicores) and memory (bytes).</summary>
public readonly record struct Resources(long CpuMillis, long MemoryBytes)
{
    public static Resources operator +(Resources a, Resources b) => new(a.CpuMillis + b.CpuMillis, a.MemoryBytes + b.MemoryBytes);

    public static Resources Max(Resources a, Resources b) => new(Math.Max(a.CpuMillis, b.CpuMillis), Math.Max(a.MemoryBytes, b.MemoryBytes));

    public static Resources Of(JsonNode? list) =>
        new(Quantity.ParseCpuMillis(list?["cpu"]?.ToString()), Quantity.ParseBytes(list?["memory"]?.ToString()));
}

public enum UsageSource
{
    None,

    MetricsServer,

    /// <summary>The kubelet summary API via the node proxy (no metrics-server needed).</summary>
    KubeletSummary,
}

public sealed record NodeStats(
    string Name,
    bool Ready,
    bool Unschedulable,
    IReadOnlyList<string> Pressures,
    Resources Allocatable,
    long PodCapacity,
    int PodCount,
    Resources Requests,
    Resources Limits,
    Resources? Usage,
    long? FsUsedBytes,
    long? FsCapacityBytes)
{
    public static double Percent(long part, long whole) => whole <= 0 ? 0 : Math.Round(100d * part / whole, 1);
}

public sealed record PodUsage(string Namespace, string Name, Resources Usage, Resources Requests, Resources Limits, IReadOnlyDictionary<string, Resources> Containers);

public sealed record ClusterStats(
    DateTimeOffset Timestamp,
    UsageSource Source,
    IReadOnlyList<NodeStats> Nodes,
    IReadOnlyList<PodUsage> Pods,
    IReadOnlyList<string> Notes)
{
    public Resources Allocatable => Sum(Nodes.Select(n => n.Allocatable));
    public Resources Requests => Sum(Nodes.Select(n => n.Requests));
    public Resources Limits => Sum(Nodes.Select(n => n.Limits));
    public Resources? Usage => Source == UsageSource.None ? null : Sum(Nodes.Select(n => n.Usage ?? default));
    public long PodCapacity => Nodes.Sum(n => n.PodCapacity);
    public int PodCount => Nodes.Sum(n => n.PodCount);
    public long? FsUsed => Nodes.Any(n => n.FsUsedBytes is not null) ? Nodes.Sum(n => n.FsUsedBytes ?? 0) : null;
    public long? FsCapacity => Nodes.Any(n => n.FsCapacityBytes is not null) ? Nodes.Sum(n => n.FsCapacityBytes ?? 0) : null;

    private static Resources Sum(IEnumerable<Resources> values) => values.Aggregate(default(Resources), (a, b) => a + b);
}

/// <summary>
/// Builds a resource overview without Prometheus, from what every cluster already has:
/// pod requests/limits vs node allocatable (always), live usage from metrics-server if installed, else from the
/// kubelet summary API (if RBAC allows nodes/proxy). Each source degrades gracefully when missing or forbidden.
/// </summary>
public sealed class StatsCollector(KubeClient client)
{
    private const int MaxSummaryNodes = 50;
    private bool? _metricsAvailable;
    private bool? _summaryAvailable;

    /// <param name="ns">Restrict the pod list (top consumers) to a namespace; node totals always cover the whole cluster.</param>
    public async Task<ClusterStats> CollectAsync(string? ns, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var nodesTask = client.ListAsync(ResourceKinds.Nodes, ct: ct);
        var podsTask = TryAsync(() => client.ListAsync(ResourceKinds.Pods, null, fieldSelector: "status.phase!=Succeeded,status.phase!=Failed", ct: ct));
        var nodeMetricsTask = _metricsAvailable == false ? Task.FromResult<JsonNode?>(null) : TryJsonAsync("apis/metrics.k8s.io/v1beta1/nodes", ct);
        var podMetricsTask = _metricsAvailable == false
            ? Task.FromResult<JsonNode?>(null)
            : TryJsonAsync(string.IsNullOrEmpty(ns) ? "apis/metrics.k8s.io/v1beta1/pods" : $"apis/metrics.k8s.io/v1beta1/namespaces/{Uri.EscapeDataString(ns)}/pods", ct);

        var nodes = (await nodesTask.ConfigureAwait(false)).Items;
        var pods = await podsTask.ConfigureAwait(false);
        var nodeMetrics = await nodeMetricsTask.ConfigureAwait(false);
        var podMetrics = await podMetricsTask.ConfigureAwait(false);
        _metricsAvailable ??= nodeMetrics is not null;

        if (pods is null) notes.Add("You can't list pods in all namespaces, so requests and limits aren't shown.");

        var perNode = new Dictionary<string, (Resources Req, Resources Lim, int Count)>(StringComparer.Ordinal);
        var podSpecs = new Dictionary<string, (Resources Req, Resources Lim)>(StringComparer.Ordinal);
        foreach (var pod in pods?.Items ?? [])
        {
            var (req, lim) = PodResources(pod);
            podSpecs[$"{pod.Namespace}/{pod.Name}"] = (req, lim);
            if (pod.GetString("spec.nodeName") is not { Length: > 0 } node) continue;
            var cur = perNode.GetValueOrDefault(node);
            perNode[node] = (cur.Req + req, cur.Lim + lim, cur.Count + 1);
        }

        var usage = new Dictionary<string, Resources>(StringComparer.Ordinal);
        foreach (var item in nodeMetrics?["items"] as JsonArray ?? [])
        {
            if (item?["metadata"]?["name"]?.ToString() is { } n) usage[n] = Resources.Of(item["usage"]);
        }
        var source = usage.Count > 0 ? UsageSource.MetricsServer : UsageSource.None;

        // Fallback (and disk usage): kubelet summary API through the node proxy.
        var fs = new Dictionary<string, (long Used, long Capacity)>(StringComparer.Ordinal);
        var summaryPods = new List<PodUsage>();
        if (_summaryAvailable != false && nodes.Count <= MaxSummaryNodes)
        {
            var summaries = await Task.WhenAll(nodes.Select(n => TryJsonAsync($"api/v1/nodes/{Uri.EscapeDataString(n.Name)}/proxy/stats/summary", ct))).ConfigureAwait(false);
            _summaryAvailable ??= summaries.Any(s => s is not null);
            for (var i = 0; i < nodes.Count; i++)
            {
                if (summaries[i]?["node"] is not { } node) continue;
                if (node["fs"] is { } f && f["usedBytes"] is not null)
                {
                    fs[nodes[i].Name] = (Long(f["usedBytes"]), Long(f["capacityBytes"]));
                }
                if (source != UsageSource.MetricsServer)
                {
                    usage[nodes[i].Name] = new Resources(Long(node["cpu"]?["usageNanoCores"]) / 1_000_000, Long(node["memory"]?["workingSetBytes"]));
                    foreach (var p in summaries[i]?["pods"] as JsonArray ?? [])
                    {
                        var pns = p?["podRef"]?["namespace"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(ns) && pns != ns) continue;
                        var containers = (p?["containers"] as JsonArray ?? []).ToDictionary(
                            c => c?["name"]?.ToString() ?? "",
                            c => new Resources(Long(c?["cpu"]?["usageNanoCores"]) / 1_000_000, Long(c?["memory"]?["workingSetBytes"])));
                        summaryPods.Add(Pod(pns, p?["podRef"]?["name"]?.ToString() ?? "", containers, podSpecs));
                    }
                }
            }
            if (source == UsageSource.None && usage.Count > 0) source = UsageSource.KubeletSummary;
        }
        else if (nodes.Count > MaxSummaryNodes)
        {
            notes.Add($"Disk usage is skipped on clusters with more than {MaxSummaryNodes} nodes.");
        }

        if (source == UsageSource.None)
        {
            notes.Add("Live CPU and memory usage needs metrics-server (or permission for nodes/proxy). Showing requests and limits only.");
        }

        var nodeStats = nodes.Select(n =>
        {
            var alloc = Resources.Of(n["status.allocatable"]);
            var pressures = (n["status.conditions"] as JsonArray ?? [])
                .Where(c => c?["type"]?.ToString() is "MemoryPressure" or "DiskPressure" or "PIDPressure" or "NetworkUnavailable" && c?["status"]?.ToString() == "True")
                .Select(c => c!["type"]!.ToString())
                .ToList();
            var ready = (n["status.conditions"] as JsonArray ?? []).Any(c => c?["type"]?.ToString() == "Ready" && c?["status"]?.ToString() == "True");
            var alloc2 = perNode.GetValueOrDefault(n.Name);
            return new NodeStats(
                n.Name,
                ready,
                n.GetString("spec.unschedulable") == "true",
                pressures,
                alloc,
                n.GetLong("status.allocatable.pods") ?? 0,
                alloc2.Count,
                alloc2.Req,
                alloc2.Lim,
                usage.TryGetValue(n.Name, out var u) ? u : null,
                fs.TryGetValue(n.Name, out var f) ? f.Used : null,
                fs.TryGetValue(n.Name, out var f2) ? f2.Capacity : null);
        }).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

        var podUsage = source == UsageSource.MetricsServer
            ? (podMetrics?["items"] as JsonArray ?? []).Select(item => Pod(
                item?["metadata"]?["namespace"]?.ToString() ?? "",
                item?["metadata"]?["name"]?.ToString() ?? "",
                (item?["containers"] as JsonArray ?? []).ToDictionary(c => c?["name"]?.ToString() ?? "", c => Resources.Of(c?["usage"])),
                podSpecs)).ToList()
            : summaryPods;

        return new ClusterStats(DateTimeOffset.UtcNow, source, nodeStats, podUsage, notes);
    }

    /// <summary>Live usage of one pod, per container (metrics-server only); null when unavailable.</summary>
    public async Task<IReadOnlyDictionary<string, Resources>?> PodContainerUsageAsync(string ns, string pod, CancellationToken ct = default)
    {
        if (_metricsAvailable == false) return null;
        var json = await TryJsonAsync($"apis/metrics.k8s.io/v1beta1/namespaces/{Uri.EscapeDataString(ns)}/pods/{Uri.EscapeDataString(pod)}", ct).ConfigureAwait(false);
        if (json is null) return null;
        return (json["containers"] as JsonArray ?? []).ToDictionary(c => c?["name"]?.ToString() ?? "", c => Resources.Of(c?["usage"]), StringComparer.Ordinal);
    }

    public static (Resources Requests, Resources Limits) ContainerResources(JsonNode? container) =>
        (Resources.Of(container?["resources"]?["requests"]), Resources.Of(container?["resources"]?["limits"]));

    /// <summary>
    /// Effective pod requests/limits the way the scheduler counts them: regular containers plus native sidecars,
    /// or the largest init container (plus sidecars) if that is bigger, plus pod overhead.
    /// </summary>
    public static (Resources Requests, Resources Limits) PodResources(KubeObject pod)
    {
        Resources reqSum = default, limSum = default, sidecarReq = default, sidecarLim = default, initReq = default, initLim = default;
        foreach (var c in pod["spec.containers"] as JsonArray ?? [])
        {
            var (r, l) = ContainerResources(c);
            reqSum += r;
            limSum += l;
        }
        foreach (var c in pod["spec.initContainers"] as JsonArray ?? [])
        {
            var (r, l) = ContainerResources(c);
            if (c?["restartPolicy"]?.ToString() == "Always")
            {
                sidecarReq += r;
                sidecarLim += l;
            }
            else
            {
                initReq = Resources.Max(initReq, r + sidecarReq);
                initLim = Resources.Max(initLim, l + sidecarLim);
            }
        }
        var overhead = Resources.Of(pod["spec.overhead"]);
        return (Resources.Max(reqSum + sidecarReq, initReq) + overhead, Resources.Max(limSum + sidecarLim, initLim) + overhead);
    }

    private static PodUsage Pod(string ns, string name, Dictionary<string, Resources> containers, Dictionary<string, (Resources Req, Resources Lim)> specs)
    {
        var total = containers.Values.Aggregate(default(Resources), (a, b) => a + b);
        var spec = specs.GetValueOrDefault($"{ns}/{name}");
        return new PodUsage(ns, name, total, spec.Req, spec.Lim, containers);
    }

    private async Task<JsonNode?> TryJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            return await client.GetJsonAsync(path, ct).ConfigureAwait(false);
        }
        catch (KubeApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway or HttpStatusCode.InternalServerError or HttpStatusCode.Unauthorized)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or Security.SecurityPolicyException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<ResourceList?> TryAsync(Func<Task<ResourceList>> f)
    {
        try
        {
            return await f().ConfigureAwait(false);
        }
        catch (KubeApiException ex) when (ex.IsForbidden)
        {
            return null;
        }
    }

    private static long Long(JsonNode? n) => long.TryParse(n?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
