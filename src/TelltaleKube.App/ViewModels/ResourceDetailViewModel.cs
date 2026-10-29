using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.App.Services;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Metrics;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;
using TelltaleKube.Core.Serialization;

namespace TelltaleKube.App.ViewModels;

public sealed record DetailField(string Label, string Value);

/// <summary>A Secret/ConfigMap key. Secret values stay masked until explicitly revealed.</summary>
public sealed partial class DataEntryViewModel(string key, string value, bool secret, MainViewModel main) : ViewModelBase
{
    public string Key { get; } = key;
    public bool IsSecret { get; } = secret;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    public partial bool Revealed { get; set; }

    public string Display => !IsSecret || Revealed ? value : "••••••••";

    [RelayCommand]
    private async Task ToggleReveal()
    {
        // Revealing requires the device owner (biometrics / passcode on mobile).
        if (!Revealed && !await Platform.Current.AuthenticateAsync($"Reveal '{Key}'")) return;
        Revealed = !Revealed;
    }

    [RelayCommand]
    private Task Copy() => IsSecret ? main.CopySecretAsync(value, $"'{Key}'") : main.CopyAsync(value, $"'{Key}'");
}

public sealed partial class ContainerCardViewModel(ResourceDetailViewModel owner, ContainerInfo info, JsonNode? spec) : ViewModelBase
{
    private readonly (TelltaleKube.Core.Metrics.Resources Requests, TelltaleKube.Core.Metrics.Resources Limits) _resources = StatsCollector.ContainerResources(spec);

    public ResourceDetailViewModel Owner { get; } = owner;
    public ContainerInfo Info { get; } = info;

    /// <summary>"cpu req 250m · lim 1 core   mem req 1.5Gi · lim 2Gi" (from the spec, always available).</summary>
    public string ResourcesText => $"cpu {Describe(_resources.Requests.CpuMillis, _resources.Limits.CpuMillis, Quantity.FormatCpu)}   mem {Describe(_resources.Requests.MemoryBytes, _resources.Limits.MemoryBytes, Quantity.FormatBytes)}";

    /// <summary>Live usage (metrics-server), e.g. "using 120m cpu (48% of request) · 300Mi mem (20% of limit)".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    public partial string? UsageText { get; set; }

    public bool HasUsage => UsageText is not null;

    public void SetUsage(TelltaleKube.Core.Metrics.Resources usage)
    {
        UsageText = $"using {Quantity.FormatCpu(usage.CpuMillis)} cpu{Ratio(usage.CpuMillis, _resources.Requests.CpuMillis, _resources.Limits.CpuMillis)}"
            + $" · {Quantity.FormatBytes(usage.MemoryBytes)} mem{Ratio(usage.MemoryBytes, _resources.Requests.MemoryBytes, _resources.Limits.MemoryBytes)}";
    }

    private static string Describe(long req, long lim, Func<long, string> f) =>
        req == 0 && lim == 0 ? "unset" : $"req {(req > 0 ? f(req) : "-")} · lim {(lim > 0 ? f(lim) : "-")}";

    private static string Ratio(long used, long req, long lim) =>
        lim > 0 ? $" ({NodeStats.Percent(used, lim):0}% of limit)" : req > 0 ? $" ({NodeStats.Percent(used, req):0}% of request)" : "";
    public string Name => Info.Name;
    public string Role => Info.RoleLabel;
    public bool IsMain => Info.Role == ContainerRole.Main;
    public string Image => Info.Image;
    public Health Health => Info.Health;
    public string State => Info.StateText;
    public string Restarts => Info.RestartCount == 0 ? "" : $"{Info.RestartCount} restart{(Info.RestartCount == 1 ? "" : "s")}";
    public string? LastExit => Info.LastTerminationText is { } t ? $"Last exit: {t}" : null;
    public bool HasLastExit => LastExit is not null;
    public bool HasLogs => Info.HasLogs;
    public bool HasPreviousLogs => Info.HasPreviousLogs;
    public bool CanExec => Owner.CanMutate && Info.CanExec;
}

public sealed partial class ResourceDetailViewModel : ViewModelBase
{
    private readonly ClusterViewModel _cluster;

    public ResourceDetailViewModel(ClusterViewModel cluster, ResourceKind kind, KubeObject obj)
    {
        _cluster = cluster;
        Kind = kind;
        Object = obj;
        Rebuild();
    }

    private static readonly string[] ContainerSpecFields = ["spec.initContainers", "spec.containers", "spec.ephemeralContainers"];

    public ResourceKind Kind { get; }
    public ClusterViewModel Cluster => _cluster;
    private MainViewModel Main => _cluster.Main;
    private KubeClient Client => _cluster.Client ?? throw new InvalidOperationException("Not connected.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Subtitle))]
    public partial KubeObject Object { get; set; }

    public string Name => Object.Name;
    public string Subtitle => Object.Namespace is { } ns ? $"{Kind.Kind} · {ns}" : Kind.Kind;

    public ObservableCollection<DetailField> Fields { get; } = [];
    public ObservableCollection<string> Labels { get; } = [];
    public ObservableCollection<DetailField> AnnotationList { get; } = [];
    public ObservableCollection<DataEntryViewModel> Data { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];

    /// <summary>Live usage vs. allocatable/requests (pods: totals; nodes: utilisation and allocation).</summary>
    public ObservableCollection<DetailField> UsageFields { get; } = [];

    public bool HasUsageFields => UsageFields.Count > 0;

    public ObservableCollection<ContainerCardViewModel> Containers { get; } = [];
    public bool HasContainers => Containers.Count > 0;

    [ObservableProperty]
    public partial string Yaml { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    [ObservableProperty]
    public partial string? EditError { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Set when the live object changed while the user was editing.</summary>
    [ObservableProperty]
    public partial bool HasRemoteChanges { get; set; }

    public bool CanMutate => _cluster.CanMutate;
    public bool IsPod => Kind.IsPod;
    public bool IsSecret => Kind.Key == "secrets";
    public bool HasData => Data.Count > 0;
    public bool CanScale => CanMutate && Kind.IsScalable;
    public bool CanRestart => CanMutate && Kind.IsRestartable;
    public bool IsNode => Kind.Key == "nodes";
    public bool CanCordon => CanMutate && IsNode;
    public bool IsCordoned => Object.GetString("spec.unschedulable") == "true";
    public bool CanExec => CanMutate && IsPod && Object.GetString("status.phase") == "Running";
    public bool CanPortForward => CanMutate && IsPod && Platform.Current.IsDesktop;

    public void UpdateObject(KubeObject live)
    {
        if (live.ResourceVersion == Object.ResourceVersion) return;
        if (IsEditing)
        {
            HasRemoteChanges = true;
            return;
        }
        Object = live;
        Rebuild();
        // Status changed (a run started/finished, pods moved on): refresh linked resources, at most every few seconds.
        if (DateTimeOffset.UtcNow - _relatedLoadedAt > TimeSpan.FromSeconds(3)) _ = LoadRelatedAsync();
    }

    private void Rebuild()
    {
        Fields.Clear();
        Labels.Clear();
        AnnotationList.Clear();
        Containers.Clear();
        Data.Clear();

        if (Object.Namespace is { } ns) Fields.Add(new("Namespace", ns));
        if (Object.Created is { } created) Fields.Add(new("Created", $"{created.LocalDateTime:g} ({ResourcePresenter.Age(Object)} ago)"));
        foreach (var c in ResourcePresenter.ColumnsFor(Kind)) Fields.Add(new(c.Header, c.Value(Object)));
        AddKindSpecificFields();
        Fields.Add(new("UID", Object.Uid));

        foreach (var (k, v) in Object.Labels) Labels.Add($"{k}={v}");
        foreach (var (k, v) in Object.Annotations.Where(a => a.Key != "kubectl.kubernetes.io/last-applied-configuration"))
        {
            AnnotationList.Add(new(k, v.Length > 300 ? v[..300] + "…" : v));
        }

        if (Kind.Key is "secrets" or "configmaps" && Object["data"] is JsonObject data)
        {
            foreach (var (k, v) in data.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                var raw = v?.ToString() ?? "";
                Data.Add(new DataEntryViewModel(k, IsSecret ? DecodeSecret(raw) : raw, IsSecret, Main));
            }
        }
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(IsCordoned));
        OnPropertyChanged(nameof(CanExec));
        if (IsPod)
        {
            var specs = ContainerSpecFields
                .SelectMany(f => Object[f] as JsonArray ?? [])
                .Where(c => c?["name"] is not null)
                .GroupBy(c => c!["name"]!.ToString())
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var c in PodContainers.Of(Object)) Containers.Add(new ContainerCardViewModel(this, c, specs.GetValueOrDefault(c.Name)));
        }
        OnPropertyChanged(nameof(HasContainers));
        SyncPortForwards();
        Yaml = BuildYaml(revealSecrets: false);
    }

    private void AddKindSpecificFields()
    {
        switch (Kind.Key)
        {
            case "pods":
                Add("Pod IP", Object.GetString("status.podIP"));
                Add("QoS", Object.GetString("status.qosClass"));
                Add("Service account", Object.GetString("spec.serviceAccountName"));
                break;
            case "deployments.apps" or "statefulsets.apps" or "daemonsets.apps":
                Add("Strategy", Object.GetString("spec.strategy.type") ?? Object.GetString("spec.updateStrategy.type"));
                Add("Selector", string.Join(", ", (Object["spec.selector.matchLabels"] as JsonObject ?? []).Select(kv => $"{kv.Key}={kv.Value}")));
                foreach (var c in Object["spec.template.spec.containers"] as JsonArray ?? []) Add($"Image ({c?["name"]})", c?["image"]?.ToString());
                break;
            case "services":
                Add("Selector", string.Join(", ", (Object["spec.selector"] as JsonObject ?? []).Select(kv => $"{kv.Key}={kv.Value}")));
                Add("External IPs", string.Join(", ", (Object["status.loadBalancer.ingress"] as JsonArray ?? []).Select(i => i?["ip"]?.ToString() ?? i?["hostname"]?.ToString())));
                break;
            case "nodes":
                Add("OS", $"{Object.GetString("status.nodeInfo.osImage")} ({Object.GetString("status.nodeInfo.architecture")})");
                Add("Kubelet", Object.GetString("status.nodeInfo.kubeletVersion"));
                Add("Container runtime", Object.GetString("status.nodeInfo.containerRuntimeVersion"));
                Add("CPU (allocatable)", Object.GetString("status.allocatable.cpu"));
                Add("Memory (allocatable)", Quantity.FormatBytes(Quantity.ParseBytes(Object.GetString("status.allocatable.memory"))));
                Add("Pods (allocatable)", Object.GetString("status.allocatable.pods"));
                Add("Internal IP", (Object["status.addresses"] as JsonArray)?.FirstOrDefault(a => a?["type"]?.ToString() == "InternalIP")?["address"]?.ToString());
                break;
        }

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Fields.Add(new(label, value));
        }
    }

    /// <summary>YAML for display. Secret data is masked unless explicitly revealed; managedFields is dropped as noise.</summary>
    private string BuildYaml(bool revealSecrets)
    {
        var copy = Object.Json.DeepClone().AsObject();
        (copy["metadata"] as JsonObject)?.Remove("managedFields");
        if (IsSecret && !revealSecrets && copy["data"] is JsonObject data)
        {
            foreach (var k in data.Select(d => d.Key).ToList()) data[k] = "<hidden>";
        }
        return YamlJson.ToYaml(copy);
    }

    private static string DecodeSecret(string b64)
    {
        try
        {
            var bytes = Convert.FromBase64String(b64);
            var text = Encoding.UTF8.GetString(bytes);
            return text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') ? $"<{bytes.Length} bytes binary>" : text;
        }
        catch (FormatException)
        {
            return b64;
        }
    }

    /// <summary>Fetches live usage for a pod (per container) or a node. Silently does nothing without a usage source.</summary>
    public async Task LoadUsageAsync()
    {
        var stats = _cluster.Stats;
        if (stats is null) return;
        try
        {
            if (IsPod && Object.Namespace is { } ns)
            {
                var (req, lim) = StatsCollector.PodResources(Object);
                var usage = await stats.PodContainerUsageAsync(ns, Object.Name);
                UsageFields.Clear();
                if (usage is not null)
                {
                    foreach (var card in Containers)
                    {
                        if (usage.TryGetValue(card.Name, out var u)) card.SetUsage(u);
                    }
                    var total = usage.Values.Aggregate(default(TelltaleKube.Core.Metrics.Resources), (a, b) => a + b);
                    UsageFields.Add(new("CPU used", $"{Quantity.FormatCpu(total.CpuMillis)}{Of(total.CpuMillis, req.CpuMillis, lim.CpuMillis)}"));
                    UsageFields.Add(new("Memory used", $"{Quantity.FormatBytes(total.MemoryBytes)}{Of(total.MemoryBytes, req.MemoryBytes, lim.MemoryBytes)}"));
                }
                UsageFields.Add(new("CPU req / limit", $"{Quantity.FormatCpu(req.CpuMillis)} / {(lim.CpuMillis > 0 ? Quantity.FormatCpu(lim.CpuMillis) : "none")}"));
                UsageFields.Add(new("Memory req / limit", $"{Quantity.FormatBytes(req.MemoryBytes)} / {(lim.MemoryBytes > 0 ? Quantity.FormatBytes(lim.MemoryBytes) : "none")}"));
            }
            else if (IsNode)
            {
                var snapshot = _cluster.LastStats is { } last && DateTimeOffset.UtcNow - last.Timestamp < TimeSpan.FromSeconds(30)
                    ? last
                    : _cluster.LastStats = await stats.CollectAsync(_cluster.NamespaceScope);
                if (snapshot.Nodes.FirstOrDefault(n => n.Name == Object.Name) is not { } n) return;
                UsageFields.Clear();
                if (n.Usage is { } u)
                {
                    UsageFields.Add(new("CPU used", $"{Quantity.FormatCpu(u.CpuMillis)} of {Quantity.FormatCpu(n.Allocatable.CpuMillis)} ({NodeStats.Percent(u.CpuMillis, n.Allocatable.CpuMillis):0}%)"));
                    UsageFields.Add(new("Memory used", $"{Quantity.FormatBytes(u.MemoryBytes)} of {Quantity.FormatBytes(n.Allocatable.MemoryBytes)} ({NodeStats.Percent(u.MemoryBytes, n.Allocatable.MemoryBytes):0}%)"));
                }
                UsageFields.Add(new("CPU requested", $"{Quantity.FormatCpu(n.Requests.CpuMillis)} ({NodeStats.Percent(n.Requests.CpuMillis, n.Allocatable.CpuMillis):0}%), limits {NodeStats.Percent(n.Limits.CpuMillis, n.Allocatable.CpuMillis):0}%"));
                UsageFields.Add(new("Memory requested", $"{Quantity.FormatBytes(n.Requests.MemoryBytes)} ({NodeStats.Percent(n.Requests.MemoryBytes, n.Allocatable.MemoryBytes):0}%), limits {NodeStats.Percent(n.Limits.MemoryBytes, n.Allocatable.MemoryBytes):0}%"));
                UsageFields.Add(new("Pods", $"{n.PodCount} of {n.PodCapacity}"));
                if (n.FsUsedBytes is { } fu && n.FsCapacityBytes is { } fc and > 0) UsageFields.Add(new("Disk", $"{Quantity.FormatBytes(fu)} of {Quantity.FormatBytes(fc)} ({NodeStats.Percent(fu, fc):0}%)"));
                if (n.Pressures.Count > 0) UsageFields.Add(new("Pressure", string.Join(", ", n.Pressures)));
            }
        }
        catch (KubeApiException)
        {
            // Usage is best-effort.
        }
        OnPropertyChanged(nameof(HasUsageFields));

        static string Of(long used, long req, long lim) =>
            lim > 0 ? $" ({NodeStats.Percent(used, lim):0}% of limit)" : req > 0 ? $" ({NodeStats.Percent(used, req):0}% of request)" : "";
    }

    public async Task LoadEventsAsync()
    {
        if (Kind.Key == "events" || string.IsNullOrEmpty(Object.Uid)) return;
        try
        {
            var list = await Client.ListAsync(ResourceKinds.Events, Kind.Namespaced ? Object.Namespace : null, fieldSelector: $"involvedObject.uid={Object.Uid}");
            Events.Clear();
            foreach (var e in list.Items.OrderByDescending(e => e.GetString("lastTimestamp") ?? e.GetString("eventTime") ?? "", StringComparer.Ordinal))
            {
                var type = e.GetString("type") ?? "";
                Events.Add(new EventRow(type, e.GetString("reason") ?? "", "", e.GetString("message") ?? "",
                    ResourcePresenter.Age(e.GetString("lastTimestamp") ?? e.GetString("eventTime")), type == "Warning" ? Health.Warning : Health.Neutral));
            }
        }
        catch (KubeApiException)
        {
            // Events may be forbidden; the tab simply stays empty.
        }
    }

    [RelayCommand]
    private async Task RevealYaml()
    {
        if (!IsSecret) return;
        if (await Platform.Current.AuthenticateAsync("Reveal secret values")) Yaml = BuildYaml(revealSecrets: true);
    }

    [RelayCommand]
    private Task CopyName() => Main.CopyAsync(Object.Name, "Name");

    [RelayCommand]
    private Task CopyYaml() => Main.CopyAsync(Yaml, "YAML");

    [RelayCommand]
    private void BeginEdit()
    {
        if (!CanMutate) return;
        var copy = Object.Json.DeepClone().AsObject();
        (copy["metadata"] as JsonObject)?.Remove("managedFields");
        EditText = YamlJson.ToYaml(copy);
        EditError = null;
        HasRemoteChanges = false;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditText = "";
        HasRemoteChanges = false;
    }

    [RelayCommand]
    private async Task SaveEdit()
    {
        try
        {
            var json = YamlJson.FromYamlSingle(EditText);
            var edited = new KubeObject(json);
            if (edited.Name != Object.Name || edited.Namespace != Object.Namespace || edited.Kind != Object.Kind)
            {
                EditError = "Changing the kind, name or namespace is not supported here.";
                return;
            }
            IsBusy = true;
            // PUT with the original resourceVersion: a concurrent change yields 409 instead of a silent overwrite.
            var updated = await Client.ReplaceAsync(Kind, json);
            IsEditing = false;
            EditText = "";
            Object = updated;
            Rebuild();
            Main.Toast($"{Kind.Kind} {Object.Name} updated");
        }
        catch (FormatException ex)
        {
            EditError = ex.Message;
        }
        catch (KubeApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            EditError = "The object was changed by someone else. Cancel and edit again to pick up the latest version.";
        }
        catch (Exception ex)
        {
            EditError = Redactor.Redact(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        // Namespaces and cluster-scoped objects need the name typed back: a mis-tap on a phone must not delete prod.
        var typed = !Kind.Namespaced ? Object.Name : null;
        if (!await Main.ConfirmAsync($"Delete {Kind.Kind}", $"Delete {Kind.Kind.ToLowerInvariant()} '{Object.Name}'{(Object.Namespace is { } ns ? $" in namespace '{ns}'" : "")}? This cannot be undone.", "Delete", destructive: true, typeToConfirm: typed))
        {
            return;
        }
        await Run(() => Client.DeleteAsync(Kind, Object.Namespace, Object.Name), $"Deleting {Object.Name}");
        _cluster.Detail = null;
    }

    [RelayCommand]
    private async Task Scale()
    {
        var current = Object.GetLong("spec.replicas") ?? 0;
        var dialog = new DialogViewModel($"Scale {Object.Name}", $"Currently {current} replica(s).", new DialogButton("Cancel", "cancel"), new DialogButton("Scale", "ok", IsPrimary: true))
        {
            InputLabel = "Replicas",
        };
        dialog.InputText = current.ToString(CultureInfo.InvariantCulture);
        if (await Main.ShowAsync(dialog) != "ok") return;
        if (!int.TryParse(dialog.InputText, NumberStyles.None, CultureInfo.InvariantCulture, out var replicas) || replicas > 1000)
        {
            Main.Toast("Enter a replica count between 0 and 1000.", isError: true);
            return;
        }
        if (replicas == 0 && !await Main.ConfirmAsync("Scale to zero", $"Scaling '{Object.Name}' to 0 stops all of its pods.", "Scale to 0", destructive: true)) return;
        await Run(() => Client.ScaleAsync(Kind, Object.Namespace, Object.Name, replicas), $"Scaled to {replicas}");
    }

    [RelayCommand]
    private async Task Restart()
    {
        if (!await Main.ConfirmAsync("Rollout restart", $"Restart all pods of '{Object.Name}' with a rolling update?", "Restart")) return;
        await Run(() => Client.RolloutRestartAsync(Kind, Object.Namespace, Object.Name), "Restart triggered");
    }

    [RelayCommand]
    private async Task ToggleCordon()
    {
        var cordon = !IsCordoned;
        if (!await Main.ConfirmAsync(cordon ? "Cordon node" : "Uncordon node", cordon ? $"Stop scheduling new pods on '{Object.Name}'?" : $"Allow scheduling on '{Object.Name}' again?", cordon ? "Cordon" : "Uncordon")) return;
        await Run(() => Client.CordonAsync(Object.Name, cordon), cordon ? "Node cordoned" : "Node uncordoned");
    }

    [RelayCommand]
    private void OpenLogs()
    {
        if (IsPod) Main.Navigate(new LogsViewModel(_cluster, Object));
    }

    [RelayCommand]
    private void OpenContainerLogs(ContainerCardViewModel? card)
    {
        if (card is not null) Main.Navigate(new LogsViewModel(_cluster, Object, card.Info.Name, previous: false));
    }

    [RelayCommand]
    private void OpenPreviousLogs(ContainerCardViewModel? card)
    {
        if (card is not null) Main.Navigate(new LogsViewModel(_cluster, Object, card.Info.Name, previous: true));
    }

    [RelayCommand]
    private void OpenContainerShell(ContainerCardViewModel? card)
    {
        if (card is { CanExec: true }) Main.Navigate(new ShellViewModel(_cluster, Object, card.Info.Name));
    }

    [RelayCommand]
    private void OpenShell()
    {
        if (CanExec) Main.Navigate(new ShellViewModel(_cluster, Object));
    }

    [RelayCommand]
    private async Task PortForward()
    {
        var ports = (Object["spec.containers"] as JsonArray ?? [])
            .SelectMany(c => c?["ports"] as JsonArray ?? [])
            .Select(p => p?["containerPort"]?.ToString())
            .Where(p => p is not null)
            .ToList();
        var dialog = new DialogViewModel($"Forward a port of {Object.Name}", "Forward any pod port to 127.0.0.1 on this computer. Only local apps can reach it.",
            new DialogButton("Cancel", "cancel"), new DialogButton("Start", "ok", IsPrimary: true))
        {
            InputLabel = "Pod port",
        };
        dialog.InputText = ports.FirstOrDefault() ?? "80";
        if (await Main.ShowAsync(dialog) != "ok") return;
        if (!int.TryParse(dialog.InputText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            Main.Toast("Enter a port between 1 and 65535.", isError: true);
            return;
        }
        try
        {
            _cluster.StartPortForward(Object.Namespace!, Object.Name, port);
        }
        catch (Exception ex)
        {
            Main.Toast(Redactor.Redact(ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private async Task Refresh()
    {
        try
        {
            Object = await Client.GetAsync(Kind, Object.Namespace, Object.Name);
            Rebuild();
            await LoadEventsAsync();
            await LoadUsageAsync();
        }
        catch (Exception ex)
        {
            Main.Toast(Redactor.Redact(ex.Message), isError: true);
        }
    }

    private async Task Run(Func<Task> action, string success)
    {
        IsBusy = true;
        try
        {
            await action();
            Main.Toast(success);
        }
        catch (Exception ex)
        {
            Main.Toast(Redactor.Redact(ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
