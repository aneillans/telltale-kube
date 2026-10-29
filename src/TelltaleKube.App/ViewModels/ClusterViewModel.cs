using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.App.Services;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Metrics;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.ViewModels;

public sealed class NavGroup(string title, IEnumerable<NavItem> items)
{
    public string Title { get; } = title;
    public ObservableCollection<NavItem> Items { get; } = [.. items];
}

/// <summary>An entry in the cluster sidebar: the overview, a resource kind, or port forwards. Badge is the kubectl short name.</summary>
public sealed record NavItem(string Title, ResourceKind? Kind, string Badge, bool IsOverview = false, bool IsPortForwards = false, bool IsHeader = false)
{
    public bool IsEntry => !IsHeader && Family is null;

    /// <summary>Set on a collapsible custom-resource family row (e.g. "Longhorn"); <see cref="Kind"/> is null.</summary>
    public string? Family { get; init; }

    public bool IsFamily => Family is not null;
    public bool Expanded { get; init; }
    public string Chevron => Expanded ? "▾" : "▸";

    public bool IsChild { get; init; }

    public Avalonia.Thickness Indent => IsChild ? new(16, 0, 0, 0) : default;
    public string? ToolTip { get; init; }
}

public sealed partial class PortForwardItem(PortForwarder forwarder, string scheme) : ViewModelBase
{
    public PortForwarder Forwarder { get; } = forwarder;
    public string Namespace => Forwarder.Namespace;
    public string Pod => Forwarder.Pod;
    public int RemotePort => Forwarder.RemotePort;
    public string Target => $"{Forwarder.Namespace}/{Forwarder.Pod}:{Forwarder.RemotePort}";
    public string Local => $"127.0.0.1:{Forwarder.LocalPort}";
    public Uri Url { get; } = new($"{scheme}://127.0.0.1:{forwarder.LocalPort}/");
    public string UrlText => Url.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LastError { get; set; }

    public bool HasError => LastError is not null;
}

public sealed partial class ClusterViewModel : PageViewModel
{
    private const string AllNamespaces = "All namespaces";
    private bool _allowInsecureForSession;

    public ClusterViewModel(MainViewModel main, ClusterEntry entry)
    {
        Main = main;
        Entry = entry;
        Groups.Add(new NavGroup("Cluster", [new NavItem("Overview", null, "all", IsOverview: true), Nav(ResourceKinds.Nodes, "no"), Nav(ResourceKinds.Namespaces, "ns"), Nav(ResourceKinds.Events, "ev")]));
        Groups.Add(new NavGroup("Workloads", [Nav(ResourceKinds.Pods, "po"), Nav(ResourceKinds.Deployments, "deploy"), Nav(ResourceKinds.StatefulSets, "sts"), Nav(ResourceKinds.DaemonSets, "ds"), Nav(ResourceKinds.ReplicaSets, "rs"), Nav(ResourceKinds.Jobs, "job"), Nav(ResourceKinds.CronJobs, "cj")]));
        Groups.Add(new NavGroup("Network", [Nav(ResourceKinds.Services, "svc"), Nav(ResourceKinds.Ingresses, "ing"), Nav(ResourceKinds.NetworkPolicies, "netpol"), Nav(ResourceKinds.Endpoints, "eps")]));
        Groups.Add(new NavGroup("Config", [Nav(ResourceKinds.ConfigMaps, "cm"), Nav(ResourceKinds.Secrets, "secret"), Nav(ResourceKinds.Hpas, "hpa"), Nav(ResourceKinds.PodDisruptionBudgets, "pdb"), Nav(ResourceKinds.ResourceQuotas, "quota")]));
        Groups.Add(new NavGroup("Storage", [Nav(ResourceKinds.Pvcs, "pvc"), Nav(ResourceKinds.Pvs, "pv"), Nav(ResourceKinds.StorageClasses, "sc")]));
        Groups.Add(new NavGroup("Access Control", [Nav(ResourceKinds.ServiceAccounts, "sa"), Nav(ResourceKinds.Roles, "role"), Nav(ResourceKinds.RoleBindings, "rb"), Nav(ResourceKinds.ClusterRoles, "cr"), Nav(ResourceKinds.ClusterRoleBindings, "crb")]));
        CustomGroup = new NavGroup("Custom Resources", [Nav(ResourceKinds.Crds, "crd")]);
        Groups.Add(CustomGroup);
        if (Platform.Current.IsDesktop) Groups[0].Items.Add(new NavItem("Port Forwards", null, "pf", IsPortForwards: true));
        foreach (var g in Groups)
        {
            NavEntries.Add(new NavItem(g.Title, null, "", IsHeader: true));
            foreach (var item in g.Items) NavEntries.Add(item);
        }
        main.PropertyChanged += OnMainPropertyChanged;
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsCompact)) return;
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(PaneOpen));
        OnPropertyChanged(nameof(WideDetail));
        OnPropertyChanged(nameof(CompactDetail));
    }

    public bool IsCompact => Main.IsCompact;

    /// <summary>Sidebar is always visible on wide layouts; on phones it is a toggled overlay.</summary>
    public bool PaneOpen
    {
        get => !IsCompact || IsNavOpen;
        set => IsNavOpen = value;
    }

    /// <summary>Detail as a side panel (wide) or as a full-screen page (compact); exactly one is non-null.</summary>
    public ResourceDetailViewModel? WideDetail => IsCompact ? null : Detail;

    public ResourceDetailViewModel? CompactDetail => IsCompact ? Detail : null;

    partial void OnIsNavOpenChanged(bool value) => OnPropertyChanged(nameof(PaneOpen));

    public MainViewModel Main { get; }
    public ClusterEntry Entry { get; }
    public override string Title => Entry.DisplayName;
    public ObservableCollection<NavGroup> Groups { get; } = [];
    public NavGroup CustomGroup { get; }

    /// <summary>Flattened sidebar (group headers + entries) so a single list owns the selection.</summary>
    public ObservableCollection<NavItem> NavEntries { get; } = [];
    public ObservableCollection<string> Namespaces { get; } = [AllNamespaces];
    public ObservableCollection<PortForwardItem> PortForwards { get; } = [];

    /// <summary>Sort and filter settings per resource kind, kept while connected (survive kind/namespace switches).</summary>
    public Dictionary<string, ListViewState> ListStates { get; } = new(StringComparer.Ordinal);
    public KubeClient? Client { get; private set; }

    public StatsCollector? Stats { get; private set; }

    /// <summary>The most recent stats snapshot (used by node/pod details without another round trip).</summary>
    public ClusterStats? LastStats { get; set; }

    /// <summary>Cluster CPU/memory usage % samples, kept while connected so sparklines survive navigation.</summary>
    public List<(DateTimeOffset At, double Cpu, double Memory)> UsageHistory { get; } = [];

    public bool IsReadOnly => Client?.Policy.ReadOnly ?? Entry.IsReadOnly;
    public bool CanMutate => !IsReadOnly;
    public bool IsDesktop => Platform.Current.IsDesktop;

    [ObservableProperty]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string ServerVersion { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedNamespace { get; set; } = AllNamespaces;

    [ObservableProperty]
    public partial NavItem? SelectedNav { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? Content { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(WideDetail), nameof(CompactDetail))]
    public partial ResourceDetailViewModel? Detail { get; set; }

    [ObservableProperty]
    public partial bool IsNavOpen { get; set; }

    public bool HasDetail => Detail is not null;
    public string? NamespaceScope => SelectedNamespace == AllNamespaces ? null : SelectedNamespace;

    public async Task ConnectAsync()
    {
        IsConnecting = true;
        Error = null;
        try
        {
            Client?.Dispose();
            var policy = new SecurityPolicy
            {
                ReadOnly = Entry.IsReadOnly,
                AllowInsecureTls = Entry.AllowInsecureTls || _allowInsecureForSession,
                AllowExecPlugins = Platform.Current.IsDesktop,
                AllowBasicAuth = Main.State.Settings.AllowBasicAuth,
            };
            Client = new KubeClient(Entry.Config.Resolve(Entry.ContextName), policy, Main.ExecApprover);
            Stats = new StatsCollector(Client);
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(CanMutate));
            ServerVersion = await Client.GetServerVersionAsync();
            await LoadNamespacesAsync();
            if (Entry.DefaultNamespace is { Length: > 0 } ns && Namespaces.Contains(ns)) SelectedNamespace = ns;
            SelectedNav = NavEntries.First(n => n.IsOverview);
            _ = DiscoverCustomResourcesAsync();
        }
        catch (SecurityPolicyException ex) when (ex.Message.Contains("insecure-skip-tls-verify", StringComparison.Ordinal))
        {
            IsConnecting = false;
            if (await Main.ConfirmAsync("Insecure TLS", $"'{Entry.DisplayName}' disables TLS certificate verification. Anyone on the network path could impersonate this cluster and capture your credentials.\n\nConnect anyway for this session?", "Connect insecurely", destructive: true))
            {
                _allowInsecureForSession = true;
                await ConnectAsync();
                return;
            }
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = Redactor.Redact(ex.Message);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private Task Retry() => ConnectAsync();

    private async Task LoadNamespacesAsync()
    {
        try
        {
            var list = await Client!.ListAsync(ResourceKinds.Namespaces);
            var names = list.Items.Select(i => i.Name).Order(StringComparer.Ordinal).ToList();
            Namespaces.Clear();
            Namespaces.Add(AllNamespaces);
            foreach (var n in names) Namespaces.Add(n);
        }
        catch (KubeApiException ex) when (ex.IsForbidden)
        {
            // Namespace-scoped users cannot list namespaces: fall back to the context's namespace.
            if (Entry.DefaultNamespace is { Length: > 0 } ns && !Namespaces.Contains(ns)) Namespaces.Add(ns);
        }
    }

    private async Task DiscoverCustomResourcesAsync()
    {
        try
        {
            var kinds = await Client!.DiscoverCustomKindsAsync();
            foreach (var family in kinds.GroupBy(k => CustomResourceFamily.KeyOf(k.Group), StringComparer.Ordinal)
                         .Select(g => (Key: g.Key, Title: CustomResourceFamily.TitleOf(g.First().Group), Kinds: g.ToList()))
                         .OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase))
            {
                var duplicate = family.Kinds.GroupBy(k => k.Plural, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
                var children = family.Kinds
                    .OrderBy(k => k.Plural, StringComparer.Ordinal)
                    .Select(k => new NavItem(Capitalise(k.Plural) + (duplicate.Contains(k.Plural) ? $" ({k.Group.Split('.')[0]})" : ""), k, k.ShortName ?? "")
                    {
                        IsChild = true,
                        ToolTip = $"{k.Kind} · {k.ApiVersion}",
                    })
                    .ToList();
                _customFamilies[family.Key] = children;
                NavEntries.Add(new NavItem(family.Title, null, children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    Family = family.Key,
                    ToolTip = string.Join(", ", family.Kinds.Select(k => k.Group).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                });
            }
        }
        catch (Exception)
        {
            // Discovery is best-effort.
        }
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Discovered custom kinds per family key, listed in the sidebar only while the family is expanded.</summary>
    private readonly Dictionary<string, List<NavItem>> _customFamilies = new(StringComparer.Ordinal);

    public IEnumerable<NavItem> CustomFamilies => NavEntries.Where(n => n.IsFamily);

    public IReadOnlyList<NavItem> FamilyKinds(string family) => _customFamilies.TryGetValue(family, out var kinds) ? kinds : [];

    public void ToggleFamily(string family, bool? expand = null)
    {
        var index = NavEntries.ToList().FindIndex(n => n.Family == family);
        if (index < 0 || !_customFamilies.TryGetValue(family, out var children)) return;
        var row = NavEntries[index];
        var open = expand ?? !row.Expanded;
        if (open == row.Expanded) return;
        var selected = SelectedNav;
        _restoringNav = true;
        try
        {
            NavEntries[index] = row with { Expanded = open };
            if (open)
            {
                for (var i = 0; i < children.Count; i++) NavEntries.Insert(index + 1 + i, children[i]);
            }
            else
            {
                foreach (var child in children) NavEntries.Remove(child);
            }
            // Keep the highlighted entry (if still listed) without reloading the content.
            SelectedNav = selected is not null && NavEntries.Contains(selected) ? selected : null;
        }
        finally
        {
            _restoringNav = false;
        }
    }

    private bool _restoringNav;
    private NavItem? _currentNav;

    /// <summary>Opens the list for a kind from any sidebar entry, expanding its custom-resource family if needed.</summary>
    public void ShowKind(ResourceKind kind)
    {
        foreach (var (family, kinds) in _customFamilies)
        {
            if (kinds.FirstOrDefault(k => k.Kind == kind) is { } child)
            {
                ToggleFamily(family, expand: true);
                SelectedNav = child;
                return;
            }
        }
        if (NavEntries.FirstOrDefault(n => n.Kind == kind) is { } entry) SelectedNav = entry;
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (_restoringNav || value is null || Client is null) return;
        if (value.IsHeader || value.IsFamily)
        {
            // Headers aren't selectable; a family row toggles. Either way the previous entry stays highlighted.
            var current = _currentNav;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (value.Family is { } family) ToggleFamily(family);
                _restoringNav = true;
                SelectedNav = current is not null && NavEntries.Contains(current) ? current : null;
                _restoringNav = false;
            });
            return;
        }
        _currentNav = value;
        Detail = null;
        _detailHistory.Clear();
        OnPropertyChanged(nameof(HasDetailHistory));
        IsNavOpen = false;
        var previous = Content;
        if (value.IsOverview)
        {
            var overview = new OverviewViewModel(this);
            Content = overview;
            _ = overview.LoadAsync();
        }
        else if (value.IsPortForwards)
        {
            Content = new PortForwardsViewModel(this);
        }
        else if (value.Kind is { } kind)
        {
            Content = new ResourceListViewModel(this, kind, NamespaceScope);
        }
        if (previous is ResourceListViewModel old) _ = old.DisposeAsync();
        if (previous is OverviewViewModel oldOverview) oldOverview.StopAutoRefresh();
    }

    partial void OnSelectedNamespaceChanged(string value)
    {
        if (Content is ResourceListViewModel list && list.Kind.Namespaced) OnSelectedNavChanged(_currentNav);
        else if (Content is OverviewViewModel overview) _ = overview.LoadAsync();
    }

    /// <summary>Jumps to the pods list filtered to one pod (used by "top consumers" on the overview).</summary>
    public void ShowPod(string ns, string name)
    {
        if (!ListStates.TryGetValue(ResourceKinds.Pods.Key, out var state)) ListStates[ResourceKinds.Pods.Key] = state = new ListViewState();
        state.Filter = $"name:={name}";
        if (Namespaces.Contains(ns)) SelectedNamespace = ns;
        SelectedNav = NavEntries.First(n => n.Kind == ResourceKinds.Pods);
    }

    /// <summary>Details visited by following links (owner chain, runs, pods), for the detail pane's back button.</summary>
    private readonly Stack<ResourceDetailViewModel> _detailHistory = new();

    public bool HasDetailHistory => _detailHistory.Count > 0;

    public void OpenDetail(ResourceKind kind, KubeObject obj) => OpenDetail(kind, obj, keepHistory: false);

    private void OpenDetail(ResourceKind kind, KubeObject obj, bool keepHistory)
    {
        if (keepHistory && Detail is { } current) _detailHistory.Push(current);
        else if (!keepHistory) _detailHistory.Clear();
        Detail = new ResourceDetailViewModel(this, kind, obj);
        OnPropertyChanged(nameof(HasDetailHistory));
        _ = Detail.LoadEventsAsync();
        _ = Detail.LoadUsageAsync();
        _ = Detail.LoadRelatedAsync();
    }

    public async Task OpenRelatedAsync(ResourceKind kind, string? ns, string name)
    {
        try
        {
            var obj = await Client!.GetAsync(kind, kind.Namespaced ? ns : null, name);
            OpenDetail(kind, obj, keepHistory: true);
        }
        catch (Exception ex) when (ex is KubeApiException or SecurityPolicyException)
        {
            Main.Toast($"{kind.Kind} {name}: {Redactor.Redact(ex.Message)}", isError: true);
        }
    }

    [RelayCommand]
    private void DetailBack()
    {
        if (_detailHistory.Count == 0) return;
        Detail = _detailHistory.Pop();
        OnPropertyChanged(nameof(HasDetailHistory));
        _ = Detail.RefreshCommand.ExecuteAsync(null);
    }

    public ResourceKind? FindKind(string apiVersion, string kind) =>
        ResourceKinds.BuiltIn.FirstOrDefault(k => k.Kind == kind && k.ApiVersion == apiVersion)
        ?? _customFamilies.Values.SelectMany(f => f).Select(n => n.Kind).FirstOrDefault(k => k is not null && k.Kind == kind && k.ApiVersion == apiVersion)
        ?? ResourceKinds.FindByKind(apiVersion, kind);

    [RelayCommand]
    private void CloseDetail()
    {
        _detailHistory.Clear();
        OnPropertyChanged(nameof(HasDetailHistory));
        Detail = null;
    }

    [RelayCommand]
    private void ToggleNav() => IsNavOpen = !IsNavOpen;

    /// <summary>
    /// Starts (or returns the existing) forward to a pod port. Nothing opens automatically: the pod panel and the
    /// Port Forwards page show a clickable link with Copy / Stop next to it.
    /// </summary>
    public PortForwardItem StartPortForward(string ns, string pod, int remotePort, string? portName = null)
    {
        if (PortForwards.FirstOrDefault(p => p.Namespace == ns && p.Pod == pod && p.RemotePort == remotePort) is { } existing) return existing;
        var fwd = PortForwarder.Start(Client!, ns, pod, remotePort);
        var item = new PortForwardItem(fwd, PortForwarder.GuessScheme(remotePort, portName));
        fwd.Error += msg => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            item.LastError = Redactor.Redact(msg);
            Main.Toast($"Port forward {pod}:{remotePort}: {item.LastError}", isError: true);
        });
        PortForwards.Add(item);
        Detail?.SyncPortForwards();
        return item;
    }

    [RelayCommand]
    public async Task StopPortForward(PortForwardItem? item)
    {
        if (item is null) return;
        PortForwards.Remove(item);
        Detail?.SyncPortForwards();
        await item.Forwarder.DisposeAsync();
        Main.Toast($"Stopped forwarding {item.Local}");
    }

    [RelayCommand]
    public async Task OpenPortForward(PortForwardItem? item)
    {
        if (item is not null && Main.OpenUrl is not null) await Main.OpenUrl(item.Url);
    }

    [RelayCommand]
    public Task CopyPortForwardUrl(PortForwardItem? item) => item is null ? Task.CompletedTask : Main.CopyAsync(item.UrlText, "Link");

    [RelayCommand]
    private Task ShowPortForwardPod(PortForwardItem? item) =>
        item is null ? Task.CompletedTask : OpenRelatedAsync(ResourceKinds.Pods, item.Namespace, item.Pod);

    protected override async ValueTask OnClosedAsync()
    {
        Main.PropertyChanged -= OnMainPropertyChanged;
        if (Content is ResourceListViewModel list) await list.DisposeAsync();
        if (Content is OverviewViewModel overview) overview.StopAutoRefresh();
        foreach (var pf in PortForwards) await pf.Forwarder.DisposeAsync();
        PortForwards.Clear();
        Client?.Dispose();
        Client = null;
    }

    private static NavItem Nav(ResourceKind kind, string badge) => new(kind.Title, kind, badge);
}

public sealed class PortForwardsViewModel(ClusterViewModel cluster) : ViewModelBase
{
    public ClusterViewModel Cluster { get; } = cluster;
}
