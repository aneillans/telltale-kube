using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.ViewModels;

/// <summary>One step in the "Controlled by" chain, e.g. ReplicaSet web-6f9c → Deployment web.</summary>
public sealed record OwnerLink(string Kind, string Name, ResourceKind? ResourceKind, string? Namespace)
{
    public bool CanOpen => ResourceKind is not null;
    public string Text => $"{Kind} {Name}";
}

public sealed record RelatedPodRow(KubeObject Pod)
{
    public string Name => Pod.Name;
    public string Status => ResourcePresenter.PodPhase(Pod);
    public Health Health => ResourcePresenter.HealthOf(ResourceKinds.Pods, Pod);
    public string Age => ResourcePresenter.Age(Pod);
    public int Restarts => PodContainers.Of(Pod).Where(c => c.Role is ContainerRole.Main or ContainerRole.Sidecar).Sum(c => c.RestartCount);
    public bool HasPreviousLogs => PodContainers.Of(Pod).Any(c => c.HasPreviousLogs);
    public string Detail => $"{Status} · {Age}{(Restarts > 0 ? $" · {Restarts} restarts" : "")}{(Pod.GetString("spec.nodeName") is { Length: > 0 } n ? $" · {n}" : "")}";
}

public sealed record JobRunRow(KubeObject Job)
{
    public string Name => Job.Name;
    public Workloads.JobState State => Workloads.StateOf(Job);
    public string Status => Workloads.StatusText(Job);
    public Health Health => Workloads.HealthOf(State);
    public bool IsManual => Job.Annotations.TryGetValue("cronjob.kubernetes.io/instantiate", out var v) && v == "manual";
    public string Started => Workloads.StartTime(Job) is { } t ? $"{ResourcePresenter.FormatAge(DateTimeOffset.UtcNow - t)} ago" : "";
    public string Duration => Workloads.FormatDuration(Workloads.Duration(Job));
    public string Detail => $"{Status} · started {Started} · took {Duration}{(IsManual ? " · manual" : "")}";
}

public sealed partial class ResourceDetailViewModel
{
    private const int MaxOwnerDepth = 6;
    private DateTimeOffset _relatedLoadedAt;

    /// <summary>The full ownership chain upwards, e.g. Pod → ReplicaSet → Deployment, or Pod → Job → CronJob.</summary>
    public ObservableCollection<OwnerLink> OwnerChain { get; } = [];

    /// <summary>Jobs, Deployments, StatefulSets, DaemonSets, ReplicaSets: their pods, newest first.</summary>
    public ObservableCollection<RelatedPodRow> RelatedPods { get; } = [];

    public ObservableCollection<JobRunRow> Runs { get; } = [];

    public bool HasOwnerChain => OwnerChain.Count > 0;
    public bool HasRelatedPods => RelatedPods.Count > 0;
    public bool HasRuns => Runs.Count > 0;
    public bool IsCronJob => Kind.Key == "cronjobs.batch";
    public bool ShowsPods => Kind.Key is "jobs.batch" or "deployments.apps" or "statefulsets.apps" or "daemonsets.apps" or "replicasets.apps";
    public bool IsSuspended => Object.GetString("spec.suspend") == "true";
    public bool CanRunNow => CanMutate && IsCronJob;
    public bool CanSuspend => CanMutate && IsCronJob;
    public bool HasFailedRun => Runs.Any(r => r.State == Workloads.JobState.Failed);
    public string PodsTitle => Kind.Key == "jobs.batch" ? "Pods (attempts)" : "Pods";

    [ObservableProperty]
    public partial string? RelatedNote { get; set; }

    /// <summary>Loads the owner chain and, depending on kind, runs or pods. Safe to call repeatedly.</summary>
    public async Task LoadRelatedAsync()
    {
        _relatedLoadedAt = DateTimeOffset.UtcNow;
        if (_cluster.Client is not { } client) return;
        try
        {
            await LoadOwnerChainAsync(client);
            if (IsCronJob) await LoadRunsAsync(client);
            else if (ShowsPods) await LoadPodsAsync(client);
        }
        catch (Exception ex) when (ex is KubeApiException or SecurityPolicyException or HttpRequestException)
        {
            RelatedNote = Redactor.Redact(ex.Message);
        }
        OnPropertyChanged(nameof(HasOwnerChain));
        OnPropertyChanged(nameof(HasRelatedPods));
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(HasFailedRun));
        OnPropertyChanged(nameof(IsSuspended));
    }

    /// <summary>Walks controller ownerReferences upwards (fetching each owner to find its owner).</summary>
    private async Task LoadOwnerChainAsync(KubeClient client)
    {
        var chain = new List<OwnerLink>();
        var current = Object;
        for (var depth = 0; depth < MaxOwnerDepth; depth++)
        {
            if (Workloads.Owners(current) is not [var owner, ..]) break;
            var kind = _cluster.FindKind(owner.ApiVersion, owner.Kind);
            chain.Add(new OwnerLink(owner.Kind, owner.Name, kind, current.Namespace));
            if (kind is null) break;
            try
            {
                current = await client.GetAsync(kind, kind.Namespaced ? current.Namespace : null, owner.Name);
            }
            catch (KubeApiException)
            {
                break; // owner deleted or not readable: show the link we have and stop
            }
        }
        if (!chain.SequenceEqual(OwnerChain))
        {
            OwnerChain.Clear();
            foreach (var link in chain) OwnerChain.Add(link);
        }
    }

    private async Task LoadRunsAsync(KubeClient client)
    {
        var jobs = await client.ListAsync(ResourceKinds.Jobs, Object.Namespace);
        var runs = jobs.Items.Where(j => Workloads.IsOwnedBy(j, Object))
            .OrderByDescending(j => Workloads.StartTime(j) ?? DateTimeOffset.MinValue)
            .Select(j => new JobRunRow(j))
            .ToList();
        Runs.Clear();
        foreach (var r in runs) Runs.Add(r);
        RelatedNote = runs.Count == 0
            ? "No runs are kept. Finished jobs are removed by successfulJobsHistoryLimit / failedJobsHistoryLimit or ttlSecondsAfterFinished."
            : null;
    }

    private async Task LoadPodsAsync(KubeClient client)
    {
        var pods = await PodsOfAsync(client, Object);
        RelatedPods.Clear();
        foreach (var p in pods.Take(50)) RelatedPods.Add(new RelatedPodRow(p));
        RelatedNote = pods.Count == 0 && Kind.Key == "jobs.batch"
            ? "This job's pods have been cleaned up (ttlSecondsAfterFinished), so its logs are gone."
            : null;
    }

    /// <summary>Pods of a workload, newest first. Job pods are additionally matched by ownership.</summary>
    private static async Task<IReadOnlyList<KubeObject>> PodsOfAsync(KubeClient client, KubeObject workload)
    {
        if (Workloads.PodSelector(workload) is not { } selector) return [];
        var list = await client.ListAsync(ResourceKinds.Pods, workload.Namespace, selector);
        return list.Items
            .Where(p => workload.Kind != "Job" || Workloads.IsOwnedBy(p, workload) || Workloads.Owners(p).Count == 0)
            .OrderByDescending(p => p.Created ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>The pod whose logs you most likely want: a running one, else the newest.</summary>
    private static KubeObject? BestPod(IReadOnlyList<KubeObject> pods) =>
        pods.FirstOrDefault(p => p.GetString("status.phase") == "Running") ?? (pods.Count > 0 ? pods[0] : null);

    [RelayCommand]
    private Task OpenOwner(OwnerLink? link) =>
        link?.ResourceKind is { } kind ? _cluster.OpenRelatedAsync(kind, link.Namespace, link.Name) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenRelatedPod(RelatedPodRow? row) =>
        row is null ? Task.CompletedTask : _cluster.OpenRelatedAsync(ResourceKinds.Pods, row.Pod.Namespace, row.Name);

    [RelayCommand]
    private void OpenRelatedPodLogs(RelatedPodRow? row)
    {
        if (row is not null) Main.Navigate(new LogsViewModel(_cluster, row.Pod));
    }

    [RelayCommand]
    private void OpenRelatedPodPreviousLogs(RelatedPodRow? row)
    {
        if (row is not null) Main.Navigate(new LogsViewModel(_cluster, row.Pod, previous: true));
    }

    /// <summary>Workload header button: logs of the running (or newest) pod.</summary>
    [RelayCommand]
    private void OpenLatestPodLogs()
    {
        if (BestPod(RelatedPods.Select(r => r.Pod).ToList()) is { } pod) Main.Navigate(new LogsViewModel(_cluster, pod));
        else Main.Toast(RelatedNote ?? "No pods found.", isError: true);
    }

    [RelayCommand]
    private Task OpenRun(JobRunRow? run) =>
        run is null ? Task.CompletedTask : _cluster.OpenRelatedAsync(ResourceKinds.Jobs, run.Job.Namespace, run.Name);

    /// <summary>Straight from a CronJob run to its pod's logs (running → live, finished → final output).</summary>
    [RelayCommand]
    private async Task OpenRunLogs(JobRunRow? run)
    {
        if (run is null || _cluster.Client is not { } client) return;
        try
        {
            var pods = await PodsOfAsync(client, run.Job);
            if (BestPod(pods) is { } pod) Main.Navigate(new LogsViewModel(_cluster, pod));
            else Main.Toast($"The pods of {run.Name} have been cleaned up (ttlSecondsAfterFinished), so its logs are gone.", isError: true);
        }
        catch (KubeApiException ex)
        {
            Main.Toast(Redactor.Redact(ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private Task OpenLatestRunLogs() => Runs.Count > 0 ? OpenRunLogs(Runs[0]) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenLastFailedRunLogs() =>
        Runs.FirstOrDefault(r => r.State == Workloads.JobState.Failed) is { } failed ? OpenRunLogs(failed) : Task.CompletedTask;

    [RelayCommand]
    private async Task RunNow()
    {
        if (!CanRunNow || _cluster.Client is not { } client) return;
        if (!await Main.ConfirmAsync("Run now", $"Start a new run of '{Object.Name}' now, outside its schedule ({Object.GetString("spec.schedule")})?", "Run now")) return;
        await Run(async () =>
        {
            await client.CreateAsync(ResourceKinds.Jobs, Workloads.JobFromCronJob(Object, DateTimeOffset.UtcNow));
            await LoadRelatedAsync();
        }, "Run started");
    }

    [RelayCommand]
    private async Task ToggleSuspend()
    {
        if (!CanSuspend || _cluster.Client is not { } client) return;
        var suspend = !IsSuspended;
        if (!await Main.ConfirmAsync(suspend ? "Suspend" : "Resume",
                suspend ? $"Stop scheduling new runs of '{Object.Name}'? Running jobs are not affected." : $"Resume the schedule of '{Object.Name}'?",
                suspend ? "Suspend" : "Resume"))
        {
            return;
        }
        await Run(async () =>
        {
            await client.MergePatchAsync(Kind, Object.Namespace, Object.Name, new JsonObject { ["spec"] = new JsonObject { ["suspend"] = suspend } });
            Object = await client.GetAsync(Kind, Object.Namespace, Object.Name);
            Rebuild();
            OnPropertyChanged(nameof(IsSuspended));
        }, suspend ? "Suspended" : "Resumed");
    }
}
