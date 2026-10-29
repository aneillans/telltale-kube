using System.Globalization;
using System.Text.Json.Nodes;

namespace TelltaleKube.Core.Resources;

public enum ContainerRole
{
    /// <summary>The application container (default-container annotation, name match, or first container).</summary>
    Main,

    /// <summary>A helper running alongside the main container: any other regular container, or a native sidecar.</summary>
    Sidecar,

    Init,

    /// <summary>A debug container added with <c>kubectl debug</c>.</summary>
    Ephemeral,
}

public enum ContainerStateKind
{
    Unknown,
    Waiting,
    Running,
    Terminated,
}

public sealed record ContainerTermination(string Reason, int? ExitCode, DateTimeOffset? FinishedAt, string? Message);

/// <summary>Spec + status of one container in a pod, joined by name (statuses are not in spec order).</summary>
public sealed record ContainerInfo(
    string Name,
    ContainerRole Role,
    bool IsNativeSidecar,
    string Image,
    ContainerStateKind State,
    string? StateReason,
    bool Ready,
    int RestartCount,
    ContainerTermination? LastTermination,
    DateTimeOffset? StartedAt)
{
    /// <summary>Logs of the previous instance exist once the container has restarted.</summary>
    public bool HasPreviousLogs => LastTermination is not null || RestartCount > 0;

    /// <summary>Containers that can serve logs (init containers that never ran cannot).</summary>
    public bool HasLogs => State is ContainerStateKind.Running or ContainerStateKind.Terminated || HasPreviousLogs;

    public bool CanExec => State == ContainerStateKind.Running && Role != ContainerRole.Ephemeral;

    public string RoleLabel => Role switch
    {
        ContainerRole.Main => "main",
        ContainerRole.Sidecar => IsNativeSidecar ? "sidecar (native)" : "sidecar",
        ContainerRole.Init => "init",
        _ => "debug",
    };

    public string StateText => State switch
    {
        ContainerStateKind.Running => Ready ? "Running" : "Running (not ready)",
        ContainerStateKind.Waiting => StateReason ?? "Waiting",
        ContainerStateKind.Terminated => StateReason ?? "Terminated",
        _ => "Unknown",
    };

    public Health Health => State switch
    {
        ContainerStateKind.Running => Ready || Role == ContainerRole.Ephemeral ? Health.Ok : Health.Warning,
        ContainerStateKind.Waiting => StateReason is "CrashLoopBackOff" or "ImagePullBackOff" or "ErrImagePull" or "CreateContainerConfigError" or "InvalidImageName" or "RunContainerError"
            ? Health.Error
            : Health.Warning,
        // A finished init container (exit 0) is healthy; a regular one that stopped is not running.
        ContainerStateKind.Terminated => StateReason == "Completed" ? Health.Neutral : Health.Error,
        _ => Health.Neutral,
    };

    public string? LastTerminationText => LastTermination is { } t
        ? $"{t.Reason}{(t.ExitCode is { } c ? $" (exit {c})" : "")}{(t.FinishedAt is { } f ? $", {ResourcePresenter.FormatAge(DateTimeOffset.UtcNow - f)} ago" : "")}"
        : null;
}

public static class PodContainers
{
    public const string DefaultContainerAnnotation = "kubectl.kubernetes.io/default-container";

    /// <summary>
    /// All containers of a pod in lifecycle order: init containers (native sidecars stay in their init position),
    /// then regular containers in spec order, then ephemeral containers.
    /// </summary>
    public static IReadOnlyList<ContainerInfo> Of(KubeObject pod)
    {
        var statuses = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var field in (string[])["status.initContainerStatuses", "status.containerStatuses", "status.ephemeralContainerStatuses"])
        {
            foreach (var s in pod[field] as JsonArray ?? [])
            {
                if (s?["name"]?.ToString() is { } n) statuses[n] = s;
            }
        }

        var regular = (pod["spec.containers"] as JsonArray ?? []).Select(c => c?["name"]?.ToString() ?? "").Where(n => n.Length > 0).ToList();
        var main = MainContainer(pod, regular);
        var result = new List<ContainerInfo>();

        foreach (var c in pod["spec.initContainers"] as JsonArray ?? [])
        {
            var native = c?["restartPolicy"]?.ToString() == "Always";
            result.Add(Build(c, native ? ContainerRole.Sidecar : ContainerRole.Init, native, statuses));
        }
        foreach (var c in pod["spec.containers"] as JsonArray ?? [])
        {
            result.Add(Build(c, c?["name"]?.ToString() == main ? ContainerRole.Main : ContainerRole.Sidecar, false, statuses));
        }
        foreach (var c in pod["spec.ephemeralContainers"] as JsonArray ?? [])
        {
            result.Add(Build(c, ContainerRole.Ephemeral, false, statuses));
        }
        return result;
    }

    /// <summary>
    /// Picks the application container: the <c>kubectl.kubernetes.io/default-container</c> annotation, else a container
    /// named after the workload (app/name labels, or the pod name without its generated suffix), else the first one.
    /// </summary>
    public static string? MainContainer(KubeObject pod, IReadOnlyList<string> regular)
    {
        if (regular.Count == 0) return null;
        if (regular.Count == 1) return regular[0];

        if (pod.Annotations.TryGetValue(DefaultContainerAnnotation, out var annotated) && regular.Contains(annotated)) return annotated;

        var labels = pod.Labels;
        var candidates = new List<string>();
        foreach (var key in (string[])["app.kubernetes.io/name", "app.kubernetes.io/component", "app", "k8s-app", "io.kompose.service", "component"])
        {
            if (labels.TryGetValue(key, out var v) && v.Length > 0) candidates.Add(v);
        }
        if (pod.Json["metadata"]?["generateName"]?.ToString() is { Length: > 1 } gen) candidates.Add(gen.TrimEnd('-'));
        if (pod["metadata.ownerReferences.0.name"]?.ToString() is { Length: > 0 } owner) candidates.Add(owner);

        foreach (var candidate in candidates)
        {
            if (regular.FirstOrDefault(r => string.Equals(r, candidate, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        }
        return regular[0];
    }

    /// <summary>Ready count the way kubectl shows it: regular containers plus native sidecars.</summary>
    public static (int Ready, int Total) ReadyCount(IReadOnlyList<ContainerInfo> containers)
    {
        var counted = containers.Where(c => c.Role == ContainerRole.Main || (c.Role == ContainerRole.Sidecar)).ToList();
        return (counted.Count(c => c.Ready), counted.Count);
    }

    private static ContainerInfo Build(JsonNode? spec, ContainerRole role, bool native, Dictionary<string, JsonNode> statuses)
    {
        var name = spec?["name"]?.ToString() ?? "";
        statuses.TryGetValue(name, out var status);
        var state = status?["state"];
        ContainerStateKind kind;
        string? reason = null;
        DateTimeOffset? started = null;
        if (state?["running"] is { } running)
        {
            kind = ContainerStateKind.Running;
            started = Time(running["startedAt"]);
        }
        else if (state?["waiting"] is { } waiting)
        {
            kind = ContainerStateKind.Waiting;
            reason = waiting["reason"]?.ToString();
        }
        else if (state?["terminated"] is { } terminated)
        {
            kind = ContainerStateKind.Terminated;
            reason = terminated["reason"]?.ToString() ?? "Terminated";
        }
        else
        {
            kind = status is null ? ContainerStateKind.Unknown : ContainerStateKind.Waiting;
        }

        ContainerTermination? last = null;
        if (status?["lastState"]?["terminated"] is { } lt)
        {
            last = new ContainerTermination(
                lt["reason"]?.ToString() ?? "Terminated",
                int.TryParse(lt["exitCode"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null,
                Time(lt["finishedAt"]),
                lt["message"]?.ToString());
        }

        return new ContainerInfo(
            name,
            role,
            native,
            spec?["image"]?.ToString() ?? "",
            kind,
            reason,
            status?["ready"]?.ToString() == "true" || (role == ContainerRole.Init && reason == "Completed"),
            int.TryParse(status?["restartCount"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var restarts) ? restarts : 0,
            last,
            started);
    }

    private static DateTimeOffset? Time(JsonNode? node) =>
        DateTimeOffset.TryParse(node?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
