using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Metrics;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.ViewModels;

public sealed partial class ResourceRowViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Column> _columns;
    private readonly Dictionary<string, string> _cells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _queryCells = new(StringComparer.Ordinal);

    public ResourceRowViewModel(ResourceListViewModel owner, KubeObject obj, IReadOnlyList<Column> columns)
    {
        Owner = owner;
        _columns = columns;
        Object = obj;
    }

    public ResourceListViewModel Owner { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Namespace), nameof(Age), nameof(Health), nameof(C1), nameof(C2), nameof(C3), nameof(C4), nameof(C5), nameof(C6), nameof(C7), nameof(C8), nameof(Summary), nameof(Containers), nameof(HasContainerStrip))]
    public partial KubeObject Object { get; set; }

    /// <summary>Nodes only: live utilisation/allocation from the stats collector (same data as the overview).</summary>
    public NodeUsageRow? NodeUsage { get; private set; }

    public bool HasNodeUsage => NodeUsage is not null;

    /// <summary>Fills the node usage columns (sortable and filterable, e.g. "62%").</summary>
    public void ApplyNodeUsage(NodeUsageRow usage)
    {
        NodeUsage = usage;
        // CPU / Memory are live usage only (empty without a usage source or for a node that reports none: sorts last,
        // never matches numeric filters); requests have their own columns.
        var n = usage.Node;
        Set("c2", "cpu", usage.HasUsage ? $"{usage.CpuUsage:0}%" : "");
        Set("c3", "memory", usage.HasUsage ? $"{usage.MemUsage:0}%" : "");
        Set("c4", "cpureq", $"{usage.CpuRequested:0}%");
        Set("c5", "memreq", $"{usage.MemRequested:0}%");
        Set("c6", "pods", $"{n.PodCount}/{n.PodCapacity}");
        Set("c7", "disk", n.FsUsedBytes is { } used && n.FsCapacityBytes is { } cap and > 0 ? $"{NodeStats.Percent(used, cap):0}%" : "");
        OnPropertyChanged(nameof(NodeUsage));
        OnPropertyChanged(nameof(HasNodeUsage));
        foreach (var c in (string[])[nameof(C3), nameof(C4), nameof(C5), nameof(C6), nameof(C7), nameof(C8), nameof(Summary)]) OnPropertyChanged(c);

        void Set(string id, string key, string value)
        {
            _cells[id] = value;
            _queryCells[key] = value;
        }
    }

    /// <summary>Pods only: one marker per container (main, sidecars, init, debug) for the list's container strip.</summary>
    public IReadOnlyList<ContainerChip> Containers { get; private set; } = [];

    public bool HasContainerStrip => Containers.Count > 1;

    public string Key => string.IsNullOrEmpty(Object.Uid) ? $"{Object.Namespace}/{Object.Name}" : Object.Uid;
    public string Name => Object.Name;
    public string Namespace => Object.Namespace ?? "";
    public string Age => ResourcePresenter.Age(Object);
    public Health Health { get; private set; }
    public string C1 => Cell("c0");
    public string C2 => Cell("c1");
    public string C3 => Cell("c2");
    public string C4 => Cell("c3");
    public string C5 => Cell("c4");
    public string C6 => Cell("c5");
    public string C7 => Cell("c6");
    public string C8 => Cell("c7");

    /// <summary>Cell values keyed by normalized header (for the filter query language).</summary>
    public IReadOnlyDictionary<string, string> QueryCells => _queryCells;

    public string Summary => string.Join("  ·  ", new[] { Owner.ShowNamespace ? Namespace : null }.Concat(SummaryColumns.Where(i => i < _columns.Count).Select(i => Cell($"c{i}") is { Length: > 0 } v ? $"{_columns[i].Header} {v}" : null)).Where(s => !string.IsNullOrWhiteSpace(s)));

    // Phones show a one-line summary; nodes skip CPU / Memory there (the bars show them) in favour of pods and disk.
    private int[] SummaryColumns => NodeUsage is not null ? [0, 1, 6, 7] : [0, 1, 2];

    /// <summary>Cell value by column id: name, namespace, c0..c7, age.</summary>
    public string Cell(string id) => _cells.TryGetValue(id, out var v) ? v : "";

    // Column values are computed once per object version, not on every sort/filter pass.
    partial void OnObjectChanged(KubeObject value)
    {
        _cells.Clear();
        _queryCells.Clear();
        Health = ResourcePresenter.HealthOf(Owner.Kind, value);
        Put("name", "name", value.Name);
        Put("namespace", "namespace", value.Namespace ?? "");
        for (var i = 0; i < _columns.Count; i++) Put($"c{i}", ColumnValue.NormalizeKey(_columns[i].Header), Safe(_columns[i], value));
        Put("age", "age", ResourcePresenter.Age(value));
        _queryCells["health"] = Health.ToString();

        // A new object version recomputes cells; keep the latest usage numbers on it.
        if (NodeUsage is { } usage) ApplyNodeUsage(usage);

        if (Owner.Kind.IsPod)
        {
            var containers = PodContainers.Of(value);
            Containers = containers.Select(c => new ContainerChip(c)).ToList();
            // Extra filter fields: containers:fluent  sidecars:istio  images:nginx
            _queryCells["containers"] = string.Join(' ', containers.Select(c => c.Name));
            _queryCells["sidecars"] = string.Join(' ', containers.Where(c => c.Role == ContainerRole.Sidecar).Select(c => c.Name));
            _queryCells["images"] = string.Join(' ', containers.Select(c => c.Image));
        }
    }

    private void Put(string id, string key, string value)
    {
        _cells[id] = value;
        _queryCells.TryAdd(key, value);
    }

    private static string Safe(Column c, KubeObject o)
    {
        try
        {
            return c.Value(o);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return "?";
        }
    }

    /// <summary>Called by the age timer so relative ages stay current.</summary>
    public void RefreshAge()
    {
        var age = ResourcePresenter.Age(Object);
        _cells["age"] = age;
        _queryCells["age"] = age;
        OnPropertyChanged(nameof(Age));
    }
}

/// <summary>A small marker for one container in a pod row: shape encodes the role, colour the health.</summary>
public sealed class ContainerChip(ContainerInfo info)
{
    public ContainerInfo Info { get; } = info;
    public Health Health => Info.Health;
    public bool IsMain => Info.Role == ContainerRole.Main;
    public bool IsSidecar => Info.Role == ContainerRole.Sidecar;
    public bool IsInit => Info.Role == ContainerRole.Init;
    public bool IsEphemeral => Info.Role == ContainerRole.Ephemeral;
    public string Tooltip =>
        $"{Info.Name} · {Info.RoleLabel}\n{Info.StateText}{(Info.RestartCount > 0 ? $" · {Info.RestartCount} restarts" : "")}\n{Info.Image}"
        + (Info.LastTerminationText is { } last ? $"\nLast exit: {last}" : "");
}

public enum SortDirection
{
    None,
    Ascending,
    Descending,
}

public enum HealthScope
{
    All,
    Healthy,
    NeedsAttention,
    Failing,
}

public sealed record HealthFilterOption(HealthScope Value, string Label);

public sealed partial class ListColumnViewModel : ViewModelBase
{
    private readonly ResourceListViewModel _owner;

    public ListColumnViewModel(ResourceListViewModel owner, string id, string header)
    {
        _owner = owner;
        Id = id;
        Header = header;
    }

    /// <summary>name, namespace, health, c0..c7 or age.</summary>
    public string Id { get; }
    public string Header { get; }
    public bool IsVisible => Header.Length > 0 && (Id != "namespace" || _owner.ShowNamespace);
    public bool IsFilterable => IsVisible && Id != "health";
    public string FilterPlaceholder => Id == "age" ? "e.g. <1h" : Header;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortGlyph))]
    public partial SortDirection Sort { get; set; }

    public string SortGlyph => Sort switch
    {
        SortDirection.Ascending => "↑",
        SortDirection.Descending => "↓",
        _ => "",
    };

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value) => _owner.OnColumnFilterChanged(this);

    public override string ToString() => Header;
}

/// <summary>Sort/filter settings of one resource kind, remembered for the lifetime of a cluster connection.</summary>
public sealed class ListViewState
{
    public string Filter { get; set; } = "";
    public string? SortColumn { get; set; }
    public SortDirection SortDirection { get; set; }
    public HealthScope HealthFilter { get; set; }
    public bool ShowColumnFilters { get; set; }
    public Dictionary<string, string> ColumnFilters { get; } = new(StringComparer.Ordinal);
}

/// <summary>A live (list + watch) table of one resource kind with multi-column sorting and filtering.</summary>
public sealed partial class ResourceListViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ClusterViewModel _cluster;
    private readonly ResourceWatcher _watcher;
    private readonly Dictionary<string, ResourceRowViewModel> _byKey = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<Column> _columns;
    private readonly DispatcherTimer _ageTimer;
    private readonly ListViewState _state;
    private FilterQuery _query = FilterQuery.Parse("");
    private IReadOnlyList<KubeObject>? _pending;
    private bool _flushScheduled;
    private bool _restoring;

    public ResourceListViewModel(ClusterViewModel cluster, ResourceKind kind, string? ns)
    {
        _cluster = cluster;
        Kind = kind;
        Namespace = kind.Namespaced ? ns : null;
        _columns = ResourcePresenter.ColumnsFor(kind);

        HealthColumn = new ListColumnViewModel(this, "health", "Health");
        NameColumn = new ListColumnViewModel(this, "name", "Name");
        NamespaceColumn = new ListColumnViewModel(this, "namespace", "Namespace");
        Col1 = new ListColumnViewModel(this, "c0", Header(0));
        Col2 = new ListColumnViewModel(this, "c1", Header(1));
        Col3 = new ListColumnViewModel(this, "c2", Header(2));
        Col4 = new ListColumnViewModel(this, "c3", Header(3));
        Col5 = new ListColumnViewModel(this, "c4", Header(4));
        Col6 = new ListColumnViewModel(this, "c5", Header(5));
        Col7 = new ListColumnViewModel(this, "c6", Header(6));
        Col8 = new ListColumnViewModel(this, "c7", Header(7));
        AgeColumn = new ListColumnViewModel(this, "age", "Age");
        Columns = [HealthColumn, NameColumn, NamespaceColumn, Col1, Col2, Col3, Col4, Col5, Col6, Col7, Col8, AgeColumn];
        // health, name, namespace (0 when hidden), 8 data slots (0 when unused), age
        ColumnLayout = string.Join(',', new[] { "28", "2.4*", ShowNamespace ? "1.2*" : "0" }
            .Concat(Enumerable.Range(0, 8).Select(i => i < _columns.Count ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_columns[i].Width}*") : "0"))
            .Append("64"));
        SortOptions = Columns.Where(c => c.IsVisible).ToList();

        if (!cluster.ListStates.TryGetValue(kind.Key, out var state)) cluster.ListStates[kind.Key] = state = new ListViewState();
        _state = state;
        _restoring = true;
        Filter = state.Filter;
        HealthFilter = HealthFilterOptions.First(o => o.Value == state.HealthFilter);
        ShowColumnFilters = state.ShowColumnFilters;
        foreach (var c in Columns)
        {
            if (state.ColumnFilters.TryGetValue(c.Id, out var f)) c.FilterText = f;
            if (c.Id == state.SortColumn) c.Sort = state.SortDirection;
        }
        _restoring = false;
        _query = FilterQuery.Parse(Filter);

        _watcher = new ResourceWatcher(cluster.Client!, kind, Namespace);
        _watcher.Changed += OnChanged;
        _watcher.Faulted += ex => Dispatcher.UIThread.Post(() => Error = Redactor.Redact(ex.Message));
        _watcher.Start();
        _ageTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var r in _byKey.Values) r.RefreshAge();
            if (AgeColumn.FilterText.Length > 0 || _query.Terms.Any(t => t.Column == "age")) Rebuild();
        });
        _ageTimer.Start();
        cluster.PropertyChanged += OnClusterPropertyChanged;
        if (kind == ResourceKinds.Nodes) StartNodeStats();
    }

    private Dictionary<string, NodeUsageRow> _nodeUsage = new(StringComparer.Ordinal);
    private CancellationTokenSource? _statsCts;

    /// <summary>Where the CPU / Memory column numbers come from ("live usage" vs "requests").</summary>
    [ObservableProperty]
    public partial string? NodeStatsSource { get; set; }

    private void StartNodeStats()
    {
        // Reuse a fresh overview snapshot immediately, then keep refreshing while the list is open.
        if (_cluster.LastStats is { } last && DateTimeOffset.UtcNow - last.Timestamp < OverviewViewModel.RefreshInterval) ApplyStats(last);
        _statsCts = new CancellationTokenSource();
        var ct = _statsCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_cluster.Stats is { } collector)
                    {
                        var stats = await collector.CollectAsync(_cluster.NamespaceScope, ct);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _cluster.LastStats = stats;
                            ApplyStats(stats);
                        });
                    }
                    await Task.Delay(OverviewViewModel.RefreshInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is KubeApiException or HttpRequestException or SecurityPolicyException)
                {
                    try
                    {
                        await Task.Delay(OverviewViewModel.RefreshInterval, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, ct);
    }

    private void ApplyStats(TelltaleKube.Core.Metrics.ClusterStats stats)
    {
        var live = stats.Source != TelltaleKube.Core.Metrics.UsageSource.None;
        _nodeUsage = stats.Nodes.ToDictionary(n => n.Name, n => new NodeUsageRow(n, live), StringComparer.Ordinal);
        foreach (var row in _byKey.Values)
        {
            if (_nodeUsage.TryGetValue(row.Name, out var usage)) row.ApplyNodeUsage(usage);
        }
        NodeStatsSource = stats.Source switch
        {
            TelltaleKube.Core.Metrics.UsageSource.MetricsServer => "CPU / Memory: live usage (metrics-server)",
            TelltaleKube.Core.Metrics.UsageSource.KubeletSummary => "CPU / Memory: live usage (kubelet)",
            _ => "CPU / Memory: no live usage (no metrics-server); see CPU req / Mem req",
        };
        // Re-sort / re-filter if the user is sorting or filtering on usage.
        Rebuild();
    }

    private void OnClusterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClusterViewModel.IsCompact))
        {
            OnPropertyChanged(nameof(IsCompact));
            OnPropertyChanged(nameof(UseCompactRows));
        }
    }

    public ResourceKind Kind { get; }
    public string? Namespace { get; }
    public ClusterViewModel Cluster => _cluster;
    public bool ShowNamespace => Kind.Namespaced && Namespace is null;
    public string Title => Kind.Title;
    public bool IsCompact => _cluster.Main.IsCompact;

    /// <summary>
    /// Set by the view from the list's own width. A wide window can still give the list little room (tablet in
    /// landscape with the detail pane open), so layout follows the list, not just the window.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseCompactRows))]
    public partial bool IsNarrow { get; set; }

    /// <summary>Two-line rows, stacked toolbar and a sort picker instead of column headers.</summary>
    public bool UseCompactRows => IsCompact || IsNarrow;

    public ListColumnViewModel HealthColumn { get; }
    public ListColumnViewModel NameColumn { get; }
    public ListColumnViewModel NamespaceColumn { get; }
    public ListColumnViewModel Col1 { get; }
    public ListColumnViewModel Col2 { get; }
    public ListColumnViewModel Col3 { get; }
    public ListColumnViewModel Col4 { get; }
    public ListColumnViewModel Col5 { get; }
    public ListColumnViewModel Col6 { get; }
    public ListColumnViewModel Col7 { get; }
    public ListColumnViewModel Col8 { get; }

    /// <summary>Grid column widths shared by the header, filter and row grids (see Controls.GridLayout).</summary>
    public string ColumnLayout { get; }
    public ListColumnViewModel AgeColumn { get; }
    public IReadOnlyList<ListColumnViewModel> Columns { get; }

    public IReadOnlyList<ListColumnViewModel> SortOptions { get; }

    public IReadOnlyList<HealthFilterOption> HealthFilterOptions { get; } =
    [
        new(HealthScope.All, "All"),
        new(HealthScope.Healthy, "Healthy"),
        new(HealthScope.NeedsAttention, "Needs attention"),
        new(HealthScope.Failing, "Failing"),
    ];

    public static string FilterHelp =>
        "Terms are combined with AND. Examples:\n" +
        "  web                 any column contains 'web'\n" +
        "  status:Running      a specific column (header name)\n" +
        "  restarts:>3         numeric: > >= < <=  (also 1Gi, 2h, 2/3)\n" +
        "  age:<1h             created in the last hour\n" +
        "  label:app=api       labels\n" +
        "  sidecars:fluent     pods with a matching sidecar (also containers:, images:)\n" +
        "  name:web-*          wildcards\n" +
        "  =exact  !negate     e.g. namespace:!kube-system";

    public ObservableCollection<ResourceRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial HealthFilterOption HealthFilter { get; set; }

    [ObservableProperty]
    public partial bool ShowColumnFilters { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Shown { get; set; }

    [ObservableProperty]
    public partial bool HasActiveFilters { get; set; }

    [ObservableProperty]
    public partial ResourceRowViewModel? Selected { get; set; }

    public string CountText => Shown == Count ? Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{Shown} / {Count}";

    /// <summary>Phone layout: the column currently sorted on (null = default namespace/name order).</summary>
    public ListColumnViewModel? SortColumn
    {
        get => Columns.FirstOrDefault(c => c.Sort != SortDirection.None);
        set
        {
            if (ReferenceEquals(value, SortColumn)) return;
            SetSort(value, value is null ? SortDirection.None : PreferredDirection(value));
        }
    }

    public bool SortDescending => SortColumn?.Sort == SortDirection.Descending;

    partial void OnFilterChanged(string value)
    {
        _query = FilterQuery.Parse(value);
        PersistAndRebuild();
    }

    partial void OnHealthFilterChanged(HealthFilterOption value) => PersistAndRebuild();

    partial void OnShowColumnFiltersChanged(bool value)
    {
        _state.ShowColumnFilters = value;
    }

    internal void OnColumnFilterChanged(ListColumnViewModel column) => PersistAndRebuild();

    partial void OnSelectedChanged(ResourceRowViewModel? value)
    {
        if (value is not null) _cluster.OpenDetail(Kind, value.Object);
    }

    /// <summary>
    /// Header click cycles: preferred direction → reverse → default order. Health and restarts start descending
    /// (worst first); everything else starts ascending.
    /// </summary>
    [RelayCommand]
    private void SortBy(ListColumnViewModel? column)
    {
        if (column is null) return;
        var preferred = PreferredDirection(column);
        var reverse = preferred == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
        var next = column.Sort == SortDirection.None ? preferred : column.Sort == preferred ? reverse : SortDirection.None;
        SetSort(column, next);
    }

    private static SortDirection PreferredDirection(ListColumnViewModel column) =>
        column.Id == "health" || ColumnValue.NormalizeKey(column.Header) == "restarts" ? SortDirection.Descending : SortDirection.Ascending;

    [RelayCommand]
    private void ToggleSortDirection()
    {
        if (SortColumn is { } c) SetSort(c, c.Sort == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _restoring = true;
        Filter = "";
        HealthFilter = HealthFilterOptions[0];
        foreach (var c in Columns) c.FilterText = "";
        _restoring = false;
        PersistAndRebuild();
    }

    private void SetSort(ListColumnViewModel? column, SortDirection direction)
    {
        foreach (var c in Columns) c.Sort = ReferenceEquals(c, column) ? direction : SortDirection.None;
        _state.SortColumn = direction == SortDirection.None ? null : column?.Id;
        _state.SortDirection = direction;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        Rebuild();
    }

    private void PersistAndRebuild()
    {
        if (_restoring) return;
        _state.Filter = Filter;
        _state.HealthFilter = HealthFilter.Value;
        _state.ColumnFilters.Clear();
        foreach (var c in Columns.Where(c => c.FilterText.Length > 0)) _state.ColumnFilters[c.Id] = c.FilterText;
        Rebuild();
    }

    private void OnChanged(IReadOnlyList<KubeObject> snapshot)
    {
        // Coalesce bursts of watch events into at most ~4 UI updates per second.
        lock (_byKey)
        {
            _pending = snapshot;
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = Task.Delay(250).ContinueWith(_ => Dispatcher.UIThread.Post(Flush), TaskScheduler.Default);
    }

    private void Flush()
    {
        IReadOnlyList<KubeObject>? snapshot;
        lock (_byKey)
        {
            snapshot = _pending;
            _pending = null;
            _flushScheduled = false;
        }
        if (snapshot is null) return;

        IsLoading = false;
        Error = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in snapshot)
        {
            var key = string.IsNullOrEmpty(obj.Uid) ? $"{obj.Namespace}/{obj.Name}" : obj.Uid;
            seen.Add(key);
            if (_byKey.TryGetValue(key, out var row))
            {
                if (!ReferenceEquals(row.Object, obj)) row.Object = obj;
            }
            else
            {
                var added = new ResourceRowViewModel(this, obj, _columns);
                if (_nodeUsage.TryGetValue(added.Name, out var usage)) added.ApplyNodeUsage(usage);
                _byKey[key] = added;
            }
        }
        foreach (var gone in _byKey.Keys.Where(k => !seen.Contains(k)).ToList()) _byKey.Remove(gone);
        Count = _byKey.Count;
        Rebuild();

        if (_cluster.Detail is { } detail && detail.Kind == Kind && _byKey.Values.FirstOrDefault(r => r.Object.Uid == detail.Object.Uid) is { } current)
        {
            detail.UpdateObject(current.Object);
        }
    }

    private bool Matches(ResourceRowViewModel row, IReadOnlyList<ListColumnViewModel> columnFilters)
    {
        var healthOk = HealthFilter.Value switch
        {
            HealthScope.Healthy => row.Health is Health.Ok or Health.Neutral,
            HealthScope.NeedsAttention => row.Health is Health.Warning or Health.Error,
            HealthScope.Failing => row.Health == Health.Error,
            _ => true,
        };
        return healthOk
            && columnFilters.All(c => ColumnValue.Matches(row.Cell(c.Id), c.FilterText))
            && _query.Matches(row.QueryCells, row.Object.Labels);
    }

    private int CompareRows(ResourceRowViewModel a, ResourceRowViewModel b)
    {
        if (SortColumn is { } col)
        {
            var descending = col.Sort == SortDirection.Descending;
            int c;
            switch (col.Id)
            {
                case "age":
                    // Ascending age = newest first.
                    c = Nullable.Compare(b.Object.Created, a.Object.Created);
                    break;
                case "health":
                    c = Severity(a.Health).CompareTo(Severity(b.Health));
                    break;
                default:
                    var x = a.Cell(col.Id);
                    var y = b.Cell(col.Id);
                    // Empty cells always go last, whatever the direction.
                    if (x.Length == 0 != (y.Length == 0)) return x.Length == 0 ? 1 : -1;
                    c = ColumnValue.Compare(x, y);
                    break;
            }
            if (c != 0) return descending ? -c : c;
        }
        var ns = string.CompareOrdinal(a.Namespace, b.Namespace);
        return ns != 0 ? ns : ColumnValue.Compare(a.Name, b.Name);
    }

    private static int Severity(Health h) => h switch
    {
        Health.Error => 3,
        Health.Warning => 2,
        Health.Neutral => 1,
        _ => 0,
    };

    private void Rebuild()
    {
        var columnFilters = Columns.Where(c => c.IsFilterable && c.FilterText.Trim().Length > 0).ToList();
        HasActiveFilters = !_query.IsEmpty || columnFilters.Count > 0 || HealthFilter.Value != HealthScope.All;

        var wanted = _byKey.Values.Where(r => Matches(r, columnFilters)).ToList();
        wanted.Sort(CompareRows);
        Shown = wanted.Count;

        // Minimal in-place diff keeps scroll position and selection stable.
        var wantedSet = new HashSet<ResourceRowViewModel>(wanted);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!wantedSet.Contains(Rows[i])) Rows.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i])) continue;
            var existing = Rows.IndexOf(wanted[i]);
            if (existing >= 0) Rows.Move(existing, i);
            else Rows.Insert(i, wanted[i]);
        }
    }

    private string Header(int i) => i < _columns.Count ? _columns[i].Header : "";

    public async ValueTask DisposeAsync()
    {
        _statsCts?.Cancel();
        _statsCts?.Dispose();
        _ageTimer.Stop();
        _cluster.PropertyChanged -= OnClusterPropertyChanged;
        _watcher.Changed -= OnChanged;
        await _watcher.DisposeAsync();
    }
}
