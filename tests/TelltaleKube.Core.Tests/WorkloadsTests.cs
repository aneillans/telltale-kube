using System.Text.Json.Nodes;
using TelltaleKube.Core.Resources;

namespace TelltaleKube.Core.Tests;

public class WorkloadsTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    [Theory]
    [InlineData("""{"status":{"conditions":[{"type":"Complete","status":"True"}],"succeeded":1}}""", Workloads.JobState.Succeeded)]
    [InlineData("""{"status":{"conditions":[{"type":"Failed","status":"True","reason":"BackoffLimitExceeded"}],"failed":3}}""", Workloads.JobState.Failed)]
    [InlineData("""{"status":{"active":1,"failed":1}}""", Workloads.JobState.Running)]
    [InlineData("""{"status":{"conditions":[{"type":"Suspended","status":"True"}]}}""", Workloads.JobState.Suspended)]
    [InlineData("""{"status":{}}""", Workloads.JobState.Pending)]
    public void Job_state_comes_from_conditions(string json, Workloads.JobState expected) => Assert.Equal(expected, Workloads.StateOf(O(json)));

    [Fact]
    public void Retried_job_that_succeeded_is_healthy_and_failed_job_shows_reason()
    {
        var retried = O("""{"status":{"failed":2,"succeeded":1,"conditions":[{"type":"Complete","status":"True"}]}}""");
        Assert.Equal(Health.Ok, ResourcePresenter.HealthOf(ResourceKinds.Jobs, retried));
        var failed = O("""{"status":{"conditions":[{"type":"Failed","status":"True","reason":"DeadlineExceeded"}]}}""");
        Assert.Equal("Failed (DeadlineExceeded)", Workloads.StatusText(failed));
        Assert.Equal(Health.Error, ResourcePresenter.HealthOf(ResourceKinds.Jobs, failed));
    }

    [Fact]
    public void Duration_uses_start_and_completion()
    {
        var job = O("""{"status":{"startTime":"2026-10-28T10:00:00Z","completionTime":"2026-10-28T10:03:25Z","conditions":[{"type":"Complete","status":"True"}]}}""");
        Assert.Equal(TimeSpan.FromSeconds(205), Workloads.Duration(job));
        Assert.Equal("3m25s", Workloads.FormatDuration(Workloads.Duration(job)));
        Assert.Equal("45s", Workloads.FormatDuration(TimeSpan.FromSeconds(45)));
        Assert.Equal("2h05m", Workloads.FormatDuration(TimeSpan.FromMinutes(125)));
    }

    [Fact]
    public void Selectors_cover_match_labels_and_expressions()
    {
        var deploy = O("""
            {"spec":{"selector":{"matchLabels":{"app":"web"},"matchExpressions":[
              {"key":"tier","operator":"In","values":["a","b"]},{"key":"legacy","operator":"DoesNotExist"},{"key":"canary","operator":"Exists"}]}}}
            """);
        Assert.Equal("app=web,tier in (a,b),!legacy,canary", Workloads.PodSelector(deploy));
        Assert.Equal("app=api", Workloads.PodSelector(O("""{"spec":{"selector":{"app":"api"}}}""")));   // Service-style
        Assert.Null(Workloads.PodSelector(O("""{"spec":{}}""")));                                      // never "all pods"
    }

    [Fact]
    public void Ownership_and_owner_chain_order()
    {
        var job = O("""{"metadata":{"name":"j","uid":"job-uid"}}""");
        var pod = O("""
            {"metadata":{"ownerReferences":[
              {"apiVersion":"v1","kind":"Node","name":"n1","uid":"x"},
              {"apiVersion":"batch/v1","kind":"Job","name":"j","uid":"job-uid","controller":true}]}}
            """);
        Assert.True(Workloads.IsOwnedBy(pod, job));
        Assert.Equal(("batch/v1", "Job", "j"), Workloads.Owners(pod)[0]); // controller first
        Assert.False(Workloads.IsOwnedBy(O("""{"metadata":{}}"""), job));
    }

    [Fact]
    public void Manual_run_copies_the_template_and_is_owned_by_the_cronjob()
    {
        var cron = O("""
            {"metadata":{"name":"nightly-database-backup-with-a-really-long-name-xx","namespace":"ops","uid":"cron-uid"},
             "spec":{"schedule":"0 2 * * *","jobTemplate":{"metadata":{"labels":{"team":"db"}},
               "spec":{"backoffLimit":2,"template":{"spec":{"restartPolicy":"Never","containers":[{"name":"backup","image":"b:1"}]}}}}}}
            """);
        var job = Workloads.JobFromCronJob(cron, DateTimeOffset.FromUnixTimeSeconds(1_800_123_456));
        var name = job["metadata"]!["name"]!.ToString();

        Assert.True(name.Length <= 63);
        Assert.EndsWith("-manual-123456", name, StringComparison.Ordinal);
        Assert.Equal("ops", job["metadata"]!["namespace"]!.ToString());
        Assert.Equal("manual", job["metadata"]!["annotations"]!["cronjob.kubernetes.io/instantiate"]!.ToString());
        Assert.Equal("db", job["metadata"]!["labels"]!["team"]!.ToString());
        Assert.Equal("cron-uid", job["metadata"]!["ownerReferences"]![0]!["uid"]!.ToString());
        Assert.Equal(2, job["spec"]!["backoffLimit"]!.GetValue<int>());
        Assert.True(Workloads.IsOwnedBy(new KubeObject(job), cron));
    }

    [Fact]
    public void Cronjob_health_reflects_last_run()
    {
        Assert.Equal(Health.Error, Workloads.CronJobHealth(O("""{"status":{"lastScheduleTime":"2026-10-28T02:00:00Z","lastSuccessfulTime":"2026-10-27T02:03:00Z"}}""")));
        Assert.Equal(Health.Ok, Workloads.CronJobHealth(O("""{"status":{"lastScheduleTime":"2026-10-28T02:00:00Z","lastSuccessfulTime":"2026-10-28T02:03:00Z"}}""")));
        Assert.Equal(Health.Ok, Workloads.CronJobHealth(O("""{"status":{"lastScheduleTime":"2026-10-28T02:00:00Z","active":[{"name":"j"}]}}""")));
        Assert.Equal(Health.Neutral, Workloads.CronJobHealth(O("""{"spec":{"suspend":true},"status":{"lastScheduleTime":"2026-10-28T02:00:00Z"}}""")));
        Assert.Equal("suspended", Workloads.CronJobActive(O("""{"spec":{"suspend":true}}""")));
    }
}
