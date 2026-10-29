using System.Globalization;
using System.Text.Json.Nodes;

namespace TelltaleKube.Core.Resources;

public enum Health
{
    Neutral,
    Ok,
    Warning,
    Error,
}

/// <param name="Width">Relative width in the list (free-text columns such as messages get more room).</param>
public sealed record Column(string Header, Func<KubeObject, string> Value, double Width = 1);

public static class ResourcePresenter
{
    public static IReadOnlyList<Column> ColumnsFor(ResourceKind kind) => kind.Key switch
    {
        "pods" => [new("Ready", PodReady), new("Status", PodPhase), new("Restarts", PodRestarts), new("Node", o => o.GetString("spec.nodeName") ?? "")],
        "deployments.apps" => [new("Ready", o => $"{o.GetLong("status.readyReplicas") ?? 0}/{o.GetLong("spec.replicas") ?? 0}"), new("Up-to-date", o => N(o, "status.updatedReplicas")), new("Available", o => N(o, "status.availableReplicas"))],
        "statefulsets.apps" => [new("Ready", o => $"{o.GetLong("status.readyReplicas") ?? 0}/{o.GetLong("spec.replicas") ?? 0}")],
        "replicasets.apps" => [new("Desired", o => N(o, "spec.replicas")), new("Ready", o => N(o, "status.readyReplicas"))],
        "daemonsets.apps" => [new("Desired", o => N(o, "status.desiredNumberScheduled")), new("Ready", o => N(o, "status.numberReady"))],
        "jobs.batch" =>
        [
            new("Status", Workloads.StatusText),
            new("Completions", o => $"{o.GetLong("status.succeeded") ?? 0}/{o.GetLong("spec.completions") ?? 1}"),
            new("Duration", o => Workloads.FormatDuration(Workloads.Duration(o))),
            new("Owner", o => Workloads.Owners(o) is [var owner, ..] ? owner.Name : ""),
        ],
        "cronjobs.batch" =>
        [
            new("Schedule", o => o.GetString("spec.schedule") ?? ""),
            new("Active", Workloads.CronJobActive),
            new("Last run", o => Age(o.GetString("status.lastScheduleTime"))),
            new("Last success", o => Age(o.GetString("status.lastSuccessfulTime"))),
        ],
        "services" => [new("Type", o => o.GetString("spec.type") ?? ""), new("Cluster IP", o => o.GetString("spec.clusterIP") ?? ""), new("Ports", ServicePorts)],
        "ingresses.networking.k8s.io" => [new("Class", o => o.GetString("spec.ingressClassName") ?? ""), new("Hosts", IngressHosts)],
        "configmaps" => [new("Keys", o => (o["data"] as JsonObject)?.Count.ToString(CultureInfo.InvariantCulture) ?? "0")],
        "secrets" => [new("Type", o => o.GetString("type") ?? ""), new("Keys", o => (o["data"] as JsonObject)?.Count.ToString(CultureInfo.InvariantCulture) ?? "0")],
        "persistentvolumeclaims" => [new("Status", o => o.GetString("status.phase") ?? ""), new("Capacity", o => o.GetString("status.capacity.storage") ?? ""), new("Class", o => o.GetString("spec.storageClassName") ?? "")],
        "persistentvolumes" => [new("Status", o => o.GetString("status.phase") ?? ""), new("Capacity", o => o.GetString("spec.capacity.storage") ?? ""), new("Claim", o => o.GetString("spec.claimRef.name") ?? "")],
        // CPU / Memory are filled in by the list from live stats (metrics-server, kubelet, or requests); empty until then.
        "nodes" =>
        [
            new("Status", NodeReady), new("Roles", NodeRoles),
            new(NodeCpuColumn, _ => ""), new(NodeMemoryColumn, _ => ""),
            new("CPU req", _ => ""), new("Mem req", _ => ""),
            new("Pods", _ => ""),
            new("Disk", _ => ""),
        ],
        "namespaces" => [new("Status", o => o.GetString("status.phase") ?? "")],
        "events" => [new("Type", o => o.GetString("type") ?? ""), new("Reason", o => o.GetString("reason") ?? ""), new("Object", o => $"{o.GetString("involvedObject.kind")}/{o.GetString("involvedObject.name")}"), new("Message", o => o.GetString("message") ?? "", 2.5)],
        "customresourcedefinitions.apiextensions.k8s.io" =>
        [
            new("Group", o => o.GetString("spec.group") ?? ""),
            new("Kind", o => o.GetString("spec.names.kind") ?? ""),
            new("Scope", o => o.GetString("spec.scope") ?? ""),
            new("Versions", o => string.Join(',', (o["spec.versions"] as JsonArray ?? []).Where(v => v?["served"]?.GetValueKind() == System.Text.Json.JsonValueKind.True).Select(v => v?["name"]?.ToString()))),
        ],
        // Custom resources: the CRD's own printer columns; otherwise its Ready condition, which most operators set.
        _ when kind.PrinterColumns is { Count: > 0 } printer => PrinterColumns.ToColumns(printer),
        _ when kind.Category == ResourceCategory.CustomResources && kind != ResourceKinds.Crds => [new("Ready", o => ReadyCondition(o)?["status"]?.ToString() ?? ""), new("Message", o => ReadyCondition(o)?["message"]?.ToString() ?? "", 2.5)],
        _ => [],
    };

    /// <summary>The <c>Ready</c> status condition (cert-manager, Flux, Argo, Crossplane, … all set one).</summary>
    public static JsonNode? ReadyCondition(KubeObject o) =>
        (o["status.conditions"] as JsonArray)?.FirstOrDefault(c => c?["type"]?.ToString() == "Ready");

    public static Health HealthOf(ResourceKind kind, KubeObject o)
    {
        if (o.IsTerminating) return Health.Warning;
        switch (kind.Key)
        {
            case "pods":
                var phase = PodPhase(o);
                if (phase is "Running" && PodAllReady(o)) return Health.Ok;
                if (phase is "Succeeded" or "Completed") return Health.Neutral;
                if (phase is "Pending" or "ContainerCreating" or "PodInitializing" or "Running" || (phase.StartsWith("Init:", StringComparison.Ordinal) && char.IsDigit(phase[5]))) return Health.Warning;
                return Health.Error;
            case "deployments.apps" or "statefulsets.apps" or "replicasets.apps":
                var want = o.GetLong("spec.replicas") ?? 0;
                var ready = o.GetLong("status.readyReplicas") ?? 0;
                return ready >= want ? Health.Ok : ready == 0 && want > 0 ? Health.Error : Health.Warning;
            case "daemonsets.apps":
                return (o.GetLong("status.numberReady") ?? 0) >= (o.GetLong("status.desiredNumberScheduled") ?? 0) ? Health.Ok : Health.Warning;
            case "nodes":
                var nodeStatus = NodeReady(o);
                return !nodeStatus.StartsWith("Ready", StringComparison.Ordinal) ? Health.Error
                    : nodeStatus.Contains("Pressure", StringComparison.Ordinal) || nodeStatus.Contains("NetworkUnavailable", StringComparison.Ordinal) ? Health.Warning
                    : Health.Ok;
            case "events":
                return o.GetString("type") == "Warning" ? Health.Warning : Health.Neutral;
            case "jobs.batch":
                // A job that failed once but succeeded on retry is healthy: judge by its final condition.
                return Workloads.HealthOf(Workloads.StateOf(o));
            case "cronjobs.batch":
                return Workloads.CronJobHealth(o);
            case "persistentvolumeclaims":
                return o.GetString("status.phase") == "Bound" ? Health.Ok : Health.Warning;
            default:
                return ReadyCondition(o)?["status"]?.ToString() switch
                {
                    "True" => Health.Ok,
                    "False" => Health.Error,
                    "Unknown" => Health.Warning,
                    _ => Health.Neutral,
                };
        }
    }

    public static string Age(KubeObject o) => o.Created is { } c ? FormatAge(DateTimeOffset.UtcNow - c) : "";

    public static string Age(string? timestamp) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? FormatAge(DateTimeOffset.UtcNow - d) : "";

    /// <summary>kubectl-style compact age: 45s, 12m, 5h, 3d, 2y.</summary>
    public static string FormatAge(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h"
            : span.TotalDays < 365 ? $"{(int)span.TotalDays}d"
            : $"{(int)(span.TotalDays / 365)}y";
    }

    public static string PodPhase(KubeObject o)
    {
        if (o.IsTerminating) return "Terminating";
        var containers = PodContainers.Of(o);

        // Pod still initialising: kubectl-style "Init:1/3" or "Init:CrashLoopBackOff".
        var init = containers.Where(c => c.Role == ContainerRole.Init).ToList();
        var initDone = init.Count(c => c.State == ContainerStateKind.Terminated && c.StateReason == "Completed");
        if (init.Count > 0 && initDone < init.Count && o.GetString("status.phase") == "Pending")
        {
            var stuck = init.FirstOrDefault(c => c.State == ContainerStateKind.Waiting && c.StateReason is { } r && r != "PodInitializing"
                || c.State == ContainerStateKind.Terminated && c.StateReason != "Completed");
            return stuck is not null ? $"Init:{stuck.StateReason}" : $"Init:{initDone}/{init.Count}";
        }

        // A waiting/terminated container reason (CrashLoopBackOff, ImagePullBackOff, OOMKilled) is more useful than the phase.
        foreach (var c in containers.Where(c => c.Role is ContainerRole.Main or ContainerRole.Sidecar))
        {
            if (c.State is ContainerStateKind.Waiting or ContainerStateKind.Terminated && !string.IsNullOrEmpty(c.StateReason) && c.StateReason != "Completed")
            {
                return c.StateReason;
            }
        }
        return o.GetString("status.phase") ?? "Unknown";
    }

    /// <summary>Names of all containers that can be logged/exec'd, main container first.</summary>
    public static IReadOnlyList<string> ContainerNames(KubeObject pod) =>
        PodContainers.Of(pod).OrderBy(c => c.Role == ContainerRole.Main ? 0 : 1).Select(c => c.Name).ToList();

    private static string PodReady(KubeObject o)
    {
        var (ready, total) = PodContainers.ReadyCount(PodContainers.Of(o));
        return $"{ready}/{total}";
    }

    private static bool PodAllReady(KubeObject o)
    {
        var (ready, total) = PodContainers.ReadyCount(PodContainers.Of(o));
        return ready == total;
    }

    private static string PodRestarts(KubeObject o) =>
        PodContainers.Of(o).Where(c => c.Role is ContainerRole.Main or ContainerRole.Sidecar).Sum(c => c.RestartCount).ToString(CultureInfo.InvariantCulture);

    public const string NodeCpuColumn = "CPU";
    public const string NodeMemoryColumn = "Memory";

    /// <summary>kubectl-style node status, plus active pressure conditions: "Ready,SchedulingDisabled,MemoryPressure".</summary>
    private static string NodeReady(KubeObject o)
    {
        var conditions = o["status.conditions"] as JsonArray ?? [];
        var ready = conditions.FirstOrDefault(c => c?["type"]?.ToString() == "Ready");
        var parts = new List<string> { ready?["status"]?.ToString() == "True" ? "Ready" : "NotReady" };
        if (o.GetString("spec.unschedulable") == "true") parts.Add("SchedulingDisabled");
        parts.AddRange(conditions
            .Where(c => c?["type"]?.ToString() is "MemoryPressure" or "DiskPressure" or "PIDPressure" or "NetworkUnavailable" && c?["status"]?.ToString() == "True")
            .Select(c => c!["type"]!.ToString()));
        return string.Join(',', parts);
    }

    private static string NodeRoles(KubeObject o)
    {
        var roles = o.Labels.Keys
            .Where(k => k.StartsWith("node-role.kubernetes.io/", StringComparison.Ordinal))
            .Select(k => k["node-role.kubernetes.io/".Length..]);
        var joined = string.Join(",", roles);
        return joined.Length == 0 ? "<none>" : joined;
    }

    private static string ServicePorts(KubeObject o) =>
        string.Join(",", (o["spec.ports"] as JsonArray ?? []).Select(p => $"{p?["port"]}/{p?["protocol"] ?? "TCP"}"));

    private static string IngressHosts(KubeObject o) =>
        string.Join(",", (o["spec.rules"] as JsonArray ?? []).Select(r => r?["host"]?.ToString() ?? "*"));

    private static string N(KubeObject o, string path) => (o.GetLong(path) ?? 0).ToString(CultureInfo.InvariantCulture);
}
