using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Metrics;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.ViewModels;

public sealed record StatTile(string Label, string Value, string Detail, Health Health);

public sealed record EventRow(string Type, string Reason, string Target, string Message, string Age, Health Health);

/// <summary>One utilisation gauge: a big number, a bar and (for live usage) a sparkline of recent samples.</summary>
public sealed record UsageGauge(string Label, string Value, string Detail, double Percent, IReadOnlyList<double>? History = null)
{
    public string PercentText => $"{Percent:0}%";
    public double BarValue => Math.Min(Percent, 100);
    public bool HasHistory => History is { Count: > 1 };
}

/// <summary>One node's row in the resources table.</summary>
/// <param name="LiveSource">The cluster has a live usage source; a node without usage then shows no number rather than requests.</param>
public sealed record NodeUsageRow(NodeStats Node, bool LiveSource = false)
{
    /// <summary>Live usage is expected but this node reported none (e.g. NotReady).</summary>
    public bool UsageMissing => LiveSource && Node.Usage is null;
    public string Name => Node.Name;
    public bool HasUsage => Node.Usage is not null;
    public double CpuUsage => Node.Usage is { } u ? NodeStats.Percent(u.CpuMillis, Node.Allocatable.CpuMillis) : 0;
    public double MemUsage => Node.Usage is { } u ? NodeStats.Percent(u.MemoryBytes, Node.Allocatable.MemoryBytes) : 0;
    public double CpuRequested => NodeStats.Percent(Node.Requests.CpuMillis, Node.Allocatable.CpuMillis);
    public double MemRequested => NodeStats.Percent(Node.Requests.MemoryBytes, Node.Allocatable.MemoryBytes);
    public string CpuText => Node.Usage is { } u
        ? $"{Quantity.FormatCpu(u.CpuMillis)} / {Quantity.FormatCpu(Node.Allocatable.CpuMillis)}"
        : $"req {Quantity.FormatCpu(Node.Requests.CpuMillis)} / {Quantity.FormatCpu(Node.Allocatable.CpuMillis)}";
    public string MemText => Node.Usage is { } u
        ? $"{Quantity.FormatBytes(u.MemoryBytes)} / {Quantity.FormatBytes(Node.Allocatable.MemoryBytes)}"
        : $"req {Quantity.FormatBytes(Node.Requests.MemoryBytes)} / {Quantity.FormatBytes(Node.Allocatable.MemoryBytes)}";
    public double CpuBar => HasUsage ? CpuUsage : UsageMissing ? 0 : CpuRequested;
    public double MemBar => HasUsage ? MemUsage : UsageMissing ? 0 : MemRequested;
    public string RequestsText => $"requested {CpuRequested:0}% cpu · {MemRequested:0}% mem";
    public string PodsText => $"{Node.PodCount}/{Node.PodCapacity} pods";
    public string? DiskText => Node.FsUsedBytes is { } used && Node.FsCapacityBytes is { } cap and > 0
        ? $"disk {NodeStats.Percent(used, cap):0}% ({Quantity.FormatBytes(used)} / {Quantity.FormatBytes(cap)})"
        : null;
    public string SummaryText => string.Join("  ·  ", new[] { RequestsText, PodsText, DiskText }.Where(t => t is not null));

    public string Flags => string.Join("  ", new[] { Node.Ready ? null : "NotReady", Node.Unschedulable ? "Cordoned" : null }.Concat(Node.Pressures).Where(f => f is not null));
    public bool HasFlags => Flags.Length > 0;
    public Health Health => !Node.Ready || Node.Pressures.Count > 0 ? Health.Error : Node.Unschedulable ? Health.Warning : Health.Ok;
}

public sealed record TopPodRow(PodUsage Pod, string Value, string Detail, double Percent)
{
    public string Name => Pod.Name;
    public string Namespace => Pod.Namespace;
}

/// <summary>
/// Cluster dashboard: health tiles, resource utilisation (no Prometheus needed: requests/limits always,
/// live usage from metrics-server or the kubelet), per-node breakdown, top consumers and recent warnings.
/// </summary>
public sealed partial class OverviewViewModel(ClusterViewModel cluster) : ViewModelBase, IDisposable
{
    private const int HistorySize = 40;
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private CancellationTokenSource? _refresh;

    public ClusterViewModel Cluster { get; } = cluster;
    public ObservableCollection<StatTile> Tiles { get; } = [];
    public ObservableCollection<EventRow> Warnings { get; } = [];
    public ObservableCollection<UsageGauge> Gauges { get; } = [];
    public ObservableCollection<NodeUsageRow> NodeRows { get; } = [];
    public ObservableCollection<TopPodRow> TopCpu { get; } = [];
    public ObservableCollection<TopPodRow> TopMemory { get; } = [];
    public ObservableCollection<string> StatsNotes { get; } = [];

    [ObservableProperty]
    public partial string UsageSourceText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasTopPods { get; set; }

    [ObservableProperty]
    public partial string? LastUpdated { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var client = Cluster.Client;
        if (client is null) return;
        IsLoading = true;
        Error = null;
        var ns = Cluster.NamespaceScope;
        try
        {
            var nodesTask = Try(() => client.ListAsync(ResourceKinds.Nodes));
            var podsTask = Try(() => client.ListAsync(ResourceKinds.Pods, ns));
            var deploysTask = Try(() => client.ListAsync(ResourceKinds.Deployments, ns));
            var eventsTask = Try(() => client.ListAsync(ResourceKinds.Events, ns, fieldSelector: "type=Warning"));
            await Task.WhenAll(nodesTask, podsTask, deploysTask, eventsTask);

            Tiles.Clear();
            Tiles.Add(new StatTile("Version", Cluster.ServerVersion, Cluster.Entry.Server, Health.Neutral));
            if (nodesTask.Result is { } nodes)
            {
                var ready = nodes.Items.Count(n => ResourcePresenter.HealthOf(ResourceKinds.Nodes, n) == Health.Ok);
                Tiles.Add(new StatTile("Nodes", $"{ready}/{nodes.Items.Count}", "ready", ready == nodes.Items.Count ? Health.Ok : Health.Error));
            }
            if (podsTask.Result is { } pods)
            {
                var byHealth = pods.Items.GroupBy(p => ResourcePresenter.HealthOf(ResourceKinds.Pods, p)).ToDictionary(g => g.Key, g => g.Count());
                var bad = byHealth.GetValueOrDefault(Health.Error);
                var pending = byHealth.GetValueOrDefault(Health.Warning);
                Tiles.Add(new StatTile("Pods", pods.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{bad} failing · {pending} pending",
                    bad > 0 ? Health.Error : pending > 0 ? Health.Warning : Health.Ok));
            }
            if (deploysTask.Result is { } deploys)
            {
                var ok = deploys.Items.Count(d => ResourcePresenter.HealthOf(ResourceKinds.Deployments, d) == Health.Ok);
                Tiles.Add(new StatTile("Deployments", $"{ok}/{deploys.Items.Count}", "available", ok == deploys.Items.Count ? Health.Ok : Health.Warning));
            }

            await LoadStatsAsync();

            Warnings.Clear();
            if (eventsTask.Result is { } events)
            {
                foreach (var e in events.Items
                    .OrderByDescending(e => e.GetString("lastTimestamp") ?? e.GetString("eventTime") ?? "", StringComparer.Ordinal)
                    .Take(50))
                {
                    Warnings.Add(new EventRow(e.GetString("type") ?? "", e.GetString("reason") ?? "",
                        $"{e.GetString("involvedObject.kind")}/{e.GetString("involvedObject.name")}", e.GetString("message") ?? "",
                        ResourcePresenter.Age(e.GetString("lastTimestamp") ?? e.GetString("eventTime")), Health.Warning));
                }
            }
        }
        catch (Exception ex)
        {
            Error = Redactor.Redact(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
        StartAutoRefresh();
    }

    /// <summary>Refreshes the resource section only (cheap enough to poll while the overview is visible).</summary>
    public async Task LoadStatsAsync()
    {
        if (Cluster.Stats is not { } collector) return;
        ClusterStats stats;
        try
        {
            stats = await collector.CollectAsync(Cluster.NamespaceScope);
        }
        catch (KubeApiException ex) when (ex.IsForbidden)
        {
            StatsNotes.Clear();
            StatsNotes.Add("You don't have permission to list nodes, so resource stats aren't available.");
            return;
        }
        Cluster.LastStats = stats;
        Apply(stats);
    }

    private void Apply(ClusterStats stats)
    {
        var alloc = stats.Allocatable;
        var history = Cluster.UsageHistory;
        if (stats.Usage is { } usage)
        {
            history.Add((stats.Timestamp, NodeStats.Percent(usage.CpuMillis, alloc.CpuMillis), NodeStats.Percent(usage.MemoryBytes, alloc.MemoryBytes)));
            if (history.Count > HistorySize) history.RemoveRange(0, history.Count - HistorySize);
        }

        Gauges.Clear();
        if (stats.Usage is { } u)
        {
            Gauges.Add(new UsageGauge("CPU usage", Quantity.FormatCpu(u.CpuMillis), $"of {Quantity.FormatCpu(alloc.CpuMillis)} allocatable",
                NodeStats.Percent(u.CpuMillis, alloc.CpuMillis), history.Select(h => h.Cpu).ToList()));
            Gauges.Add(new UsageGauge("Memory usage", Quantity.FormatBytes(u.MemoryBytes), $"of {Quantity.FormatBytes(alloc.MemoryBytes)} allocatable",
                NodeStats.Percent(u.MemoryBytes, alloc.MemoryBytes), history.Select(h => h.Memory).ToList()));
        }
        var req = stats.Requests;
        var lim = stats.Limits;
        Gauges.Add(new UsageGauge("CPU requested", Quantity.FormatCpu(req.CpuMillis),
            LimitsText(lim.CpuMillis, alloc.CpuMillis, Quantity.FormatCpu), NodeStats.Percent(req.CpuMillis, alloc.CpuMillis)));
        Gauges.Add(new UsageGauge("Memory requested", Quantity.FormatBytes(req.MemoryBytes),
            LimitsText(lim.MemoryBytes, alloc.MemoryBytes, Quantity.FormatBytes), NodeStats.Percent(req.MemoryBytes, alloc.MemoryBytes)));
        Gauges.Add(new UsageGauge("Pods", $"{stats.PodCount}", $"of {stats.PodCapacity} slots", NodeStats.Percent(stats.PodCount, stats.PodCapacity)));
        if (stats.FsUsed is { } fsUsed && stats.FsCapacity is { } fsCap and > 0)
        {
            Gauges.Add(new UsageGauge("Node disk", Quantity.FormatBytes(fsUsed), $"of {Quantity.FormatBytes(fsCap)}", NodeStats.Percent(fsUsed, fsCap)));
        }

        NodeRows.Clear();
        foreach (var n in stats.Nodes) NodeRows.Add(new NodeUsageRow(n, stats.Source != UsageSource.None));

        TopCpu.Clear();
        TopMemory.Clear();
        foreach (var p in stats.Pods.OrderByDescending(p => p.Usage.CpuMillis).Take(8))
        {
            TopCpu.Add(new TopPodRow(p, Quantity.FormatCpu(p.Usage.CpuMillis), OfRequest(p.Usage.CpuMillis, p.Requests.CpuMillis, p.Limits.CpuMillis, Quantity.FormatCpu),
                NodeStats.Percent(p.Usage.CpuMillis, Math.Max(1, p.Limits.CpuMillis > 0 ? p.Limits.CpuMillis : p.Requests.CpuMillis))));
        }
        foreach (var p in stats.Pods.OrderByDescending(p => p.Usage.MemoryBytes).Take(8))
        {
            TopMemory.Add(new TopPodRow(p, Quantity.FormatBytes(p.Usage.MemoryBytes), OfRequest(p.Usage.MemoryBytes, p.Requests.MemoryBytes, p.Limits.MemoryBytes, Quantity.FormatBytes),
                NodeStats.Percent(p.Usage.MemoryBytes, Math.Max(1, p.Limits.MemoryBytes > 0 ? p.Limits.MemoryBytes : p.Requests.MemoryBytes))));
        }
        HasTopPods = TopCpu.Count > 0;

        UsageSourceText = stats.Source switch
        {
            UsageSource.MetricsServer => "Live usage from metrics-server",
            UsageSource.KubeletSummary => "Live usage from the kubelet (metrics-server not installed)",
            _ => "Requests and limits only (no live usage source)",
        };
        StatsNotes.Clear();
        foreach (var n in stats.Notes) StatsNotes.Add(n);
        LastUpdated = $"Updated {stats.Timestamp.ToLocalTime():T} · refreshes every {RefreshInterval.TotalSeconds:0}s";
    }

    /// <summary>"limits 24Gi (150%, overcommitted)": limits above allocatable mean nodes can run out under load.</summary>
    private static string LimitsText(long limits, long allocatable, Func<long, string> format)
    {
        if (limits == 0) return "no limits set";
        var pct = NodeStats.Percent(limits, allocatable);
        return $"limits {format(limits)} ({pct:0}%{(pct > 100 ? ", overcommitted" : "")})";
    }

    /// <summary>"120% of request · 60% of limit": quickly shows pods running hotter than they asked for.</summary>
    private static string OfRequest(long used, long request, long limit, Func<long, string> format)
    {
        var parts = new List<string>();
        if (request > 0) parts.Add($"{NodeStats.Percent(used, request):0}% of request ({format(request)})");
        if (limit > 0) parts.Add($"{NodeStats.Percent(used, limit):0}% of limit");
        return parts.Count == 0 ? "no requests or limits set" : string.Join(" · ", parts);
    }

    [RelayCommand]
    private void OpenPod(TopPodRow? row)
    {
        if (row is not null) Cluster.ShowPod(row.Namespace, row.Name);
    }

    private void StartAutoRefresh()
    {
        if (_refresh is not null) return;
        _refresh = new CancellationTokenSource();
        var ct = _refresh.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RefreshInterval, ct);
                    await Dispatcher.UIThread.InvokeAsync(LoadStatsAsync);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // Transient failure: keep the last numbers and try again next tick.
                }
            }
        }, ct);
    }

    public void StopAutoRefresh()
    {
        _refresh?.Cancel();
        _refresh?.Dispose();
        _refresh = null;
    }

    public void Dispose() => StopAutoRefresh();

    /// <summary>A forbidden list (RBAC) just hides that tile instead of failing the whole dashboard.</summary>
    private static async Task<ResourceList?> Try(Func<Task<ResourceList>> f)
    {
        try
        {
            return await f();
        }
        catch (KubeApiException ex) when (ex.IsForbidden)
        {
            return null;
        }
    }
}
