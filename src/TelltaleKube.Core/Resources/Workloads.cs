using System.Globalization;
using System.Text.Json.Nodes;

namespace TelltaleKube.Core.Resources;

/// <summary>Helpers that connect workloads to what they own: CronJob → Jobs → Pods, controllers → Pods, child → owner.</summary>
public static class Workloads
{
    public enum JobState
    {
        Pending,
        Running,
        Succeeded,
        Failed,
        Suspended,
    }

    public static JobState StateOf(KubeObject job)
    {
        foreach (var c in job["status.conditions"] as JsonArray ?? [])
        {
            if (c?["status"]?.ToString() != "True") continue;
            switch (c["type"]?.ToString())
            {
                case "Complete" or "SuccessCriteriaMet":
                    return JobState.Succeeded;
                case "Failed" or "FailureTarget":
                    return JobState.Failed;
                case "Suspended":
                    return JobState.Suspended;
            }
        }
        return (job.GetLong("status.active") ?? 0) > 0 ? JobState.Running : JobState.Pending;
    }

    public static Health HealthOf(JobState state) => state switch
    {
        JobState.Succeeded or JobState.Running => Health.Ok,
        JobState.Failed => Health.Error,
        JobState.Suspended => Health.Neutral,
        _ => Health.Warning,
    };

    public static DateTimeOffset? StartTime(KubeObject job) => Time(job.GetString("status.startTime")) ?? job.Created;

    /// <summary>Run time: start → completion (or now while running / failed-without-completion → last transition).</summary>
    public static TimeSpan? Duration(KubeObject job)
    {
        if (StartTime(job) is not { } start) return null;
        var end = Time(job.GetString("status.completionTime"))
            ?? (StateOf(job) == JobState.Failed
                ? (job["status.conditions"] as JsonArray ?? []).Select(c => Time(c?["lastTransitionTime"]?.ToString())).Max()
                : null)
            ?? DateTimeOffset.UtcNow;
        return end - start;
    }

    public static string FormatDuration(TimeSpan? d)
    {
        if (d is not { } span) return "";
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
            : $"{(int)span.TotalDays}d{span.Hours}h";
    }

    public static bool IsOwnedBy(KubeObject child, KubeObject owner) =>
        (child["metadata.ownerReferences"] as JsonArray ?? []).Any(r => r?["uid"]?.ToString() is { Length: > 0 } uid && uid == owner.Uid);

    /// <summary>The controlling owner (ownerReference with controller: true), or the first owner.</summary>
    public static IReadOnlyList<(string ApiVersion, string Kind, string Name)> Owners(KubeObject obj) =>
        (obj["metadata.ownerReferences"] as JsonArray ?? [])
            .OrderByDescending(r => r?["controller"]?.ToString() == "true")
            .Select(r => (r?["apiVersion"]?.ToString() ?? "", r?["kind"]?.ToString() ?? "", r?["name"]?.ToString() ?? ""))
            .Where(r => r.Item2.Length > 0 && r.Item3.Length > 0)
            .ToList();

    /// <summary>
    /// Converts a workload's <c>spec.selector</c> into a label selector query string, e.g.
    /// <c>app=web,tier in (a,b),!legacy</c>. Returns null when there is no selector (never select "everything").
    /// </summary>
    public static string? PodSelector(KubeObject workload)
    {
        // Services use a plain map; workloads use matchLabels/matchExpressions.
        var selector = workload["spec.selector"];
        var parts = new List<string>();
        var labels = selector?["matchLabels"] as JsonObject ?? (selector is JsonObject plain && plain["matchExpressions"] is null && plain["matchLabels"] is null ? plain : null);
        foreach (var (k, v) in labels ?? [])
        {
            parts.Add($"{k}={v}");
        }
        foreach (var e in selector?["matchExpressions"] as JsonArray ?? [])
        {
            var key = e?["key"]?.ToString();
            var values = string.Join(',', (e?["values"] as JsonArray ?? []).Select(v => v?.ToString()));
            switch (e?["operator"]?.ToString())
            {
                case "In": parts.Add($"{key} in ({values})"); break;
                case "NotIn": parts.Add($"{key} notin ({values})"); break;
                case "Exists": parts.Add($"{key}"); break;
                case "DoesNotExist": parts.Add($"!{key}"); break;
            }
        }
        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    /// <summary>
    /// Builds a Job from a CronJob's template, like <c>kubectl create job --from=cronjob/NAME</c>: same spec, owned by
    /// the CronJob (so it shows up in its run history and is garbage-collected with it), annotated as manual.
    /// </summary>
    public static JsonObject JobFromCronJob(KubeObject cronJob, DateTimeOffset now)
    {
        var template = cronJob["spec.jobTemplate"]?.DeepClone() as JsonObject ?? [];
        var meta = template["metadata"] as JsonObject ?? [];
        var suffix = "-manual-" + now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)[^6..];
        var name = cronJob.Name.Length + suffix.Length > 63 ? cronJob.Name[..(63 - suffix.Length)] + suffix : cronJob.Name + suffix;

        var annotations = meta["annotations"]?.DeepClone() as JsonObject ?? [];
        annotations["cronjob.kubernetes.io/instantiate"] = "manual";
        return new JsonObject
        {
            ["apiVersion"] = "batch/v1",
            ["kind"] = "Job",
            ["metadata"] = new JsonObject
            {
                ["name"] = name,
                ["namespace"] = cronJob.Namespace,
                ["labels"] = meta["labels"]?.DeepClone(),
                ["annotations"] = annotations,
                ["ownerReferences"] = new JsonArray(new JsonObject
                {
                    ["apiVersion"] = "batch/v1",
                    ["kind"] = "CronJob",
                    ["name"] = cronJob.Name,
                    ["uid"] = cronJob.Uid,
                    ["controller"] = true,
                    ["blockOwnerDeletion"] = true,
                }),
            },
            ["spec"] = template["spec"]?.DeepClone() ?? new JsonObject(),
        };
    }

    /// <summary>CronJob "last result": failed when the latest schedule is newer than the latest success and nothing is running.</summary>
    public static Health CronJobHealth(KubeObject cron)
    {
        if (cron.GetString("spec.suspend") == "true") return Health.Neutral;
        var lastSchedule = Time(cron.GetString("status.lastScheduleTime"));
        var lastSuccess = Time(cron.GetString("status.lastSuccessfulTime"));
        var active = (cron["status.active"] as JsonArray)?.Count ?? 0;
        if (lastSchedule is null || active > 0) return Health.Ok;
        return lastSuccess is null || lastSuccess < lastSchedule ? Health.Error : Health.Ok;
    }

    public static string CronJobActive(KubeObject cron) =>
        cron.GetString("spec.suspend") == "true" ? "suspended" : ((cron["status.active"] as JsonArray)?.Count ?? 0).ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static string StatusText(KubeObject job)
    {
        var state = StateOf(job);
        if (state != JobState.Failed) return state.ToString();
        var reason = (job["status.conditions"] as JsonArray ?? [])
            .FirstOrDefault(c => c?["type"]?.ToString() == "Failed" && c?["status"]?.ToString() == "True")?["reason"]?.ToString();
        return reason is null ? "Failed" : $"Failed ({reason})";
    }
}
