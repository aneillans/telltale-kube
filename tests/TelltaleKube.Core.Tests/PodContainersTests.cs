using System.Text.Json.Nodes;
using TelltaleKube.Core.Resources;

namespace TelltaleKube.Core.Tests;

public class PodContainersTests
{
    private static KubeObject Load(string name) =>
        new(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!.AsObject());

    private static KubeObject Pod(string json) => new(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void Classic_sidecars_are_identified_by_workload_name_not_status_order()
    {
        // Real-world StatefulSet pod: 4 regular containers, statuses returned alphabetically, no default-container annotation.
        var pod = Load("mariadb-pod.json");
        var containers = PodContainers.Of(pod);

        Assert.Equal(["mariadb-1", "mariadb-1-healthcheck", "fluent-bit", "slow-log-rotate"], containers.Select(c => c.Name));
        Assert.Equal(ContainerRole.Main, containers[0].Role);
        Assert.All(containers.Skip(1), c => Assert.Equal(ContainerRole.Sidecar, c.Role));
        Assert.All(containers, c => Assert.Equal(ContainerStateKind.Running, c.State));
        Assert.Equal("fluent/fluent-bit:5.1.5", containers[2].Image);

        var cols = ResourcePresenter.ColumnsFor(ResourceKinds.Pods);
        Assert.Equal("4/4", cols.Single(c => c.Header == "Ready").Value(pod));
        Assert.Equal("Running", ResourcePresenter.PodPhase(pod));
        Assert.Equal(Health.Ok, ResourcePresenter.HealthOf(ResourceKinds.Pods, pod));
        Assert.Equal("mariadb-1", ResourcePresenter.ContainerNames(pod)[0]);
    }

    [Fact]
    public void Default_container_annotation_wins()
    {
        var pod = Pod("""
            {"metadata":{"name":"x-1","annotations":{"kubectl.kubernetes.io/default-container":"app"},"labels":{"app":"proxy"}},
             "spec":{"containers":[{"name":"proxy"},{"name":"app"}]}}
            """);
        var c = PodContainers.Of(pod);
        Assert.Equal(ContainerRole.Sidecar, c[0].Role);
        Assert.Equal(ContainerRole.Main, c[1].Role);
    }

    [Fact]
    public void Falls_back_to_first_container()
    {
        var pod = Pod("""{"metadata":{"name":"p"},"spec":{"containers":[{"name":"a"},{"name":"istio-proxy"}]}}""");
        Assert.Equal(ContainerRole.Main, PodContainers.Of(pod)[0].Role);
        Assert.Equal(ContainerStateKind.Unknown, PodContainers.Of(pod)[0].State);
    }

    [Fact]
    public void Native_sidecars_init_and_ephemeral_containers()
    {
        var pod = Pod("""
            {"metadata":{"name":"web-abc","labels":{"app":"web"}},
             "spec":{
               "initContainers":[{"name":"migrate","image":"m"},{"name":"istio-proxy","image":"envoy","restartPolicy":"Always"}],
               "containers":[{"name":"web","image":"w"}],
               "ephemeralContainers":[{"name":"debugger","image":"busybox"}]},
             "status":{"phase":"Running",
               "initContainerStatuses":[
                 {"name":"migrate","ready":false,"restartCount":0,"state":{"terminated":{"reason":"Completed","exitCode":0}}},
                 {"name":"istio-proxy","ready":true,"restartCount":2,"state":{"running":{}},
                  "lastState":{"terminated":{"reason":"OOMKilled","exitCode":137,"finishedAt":"2026-10-28T07:00:00Z"}}}],
               "containerStatuses":[{"name":"web","ready":true,"restartCount":1,"state":{"running":{}}}],
               "ephemeralContainerStatuses":[{"name":"debugger","state":{"running":{}}}]}}
            """);
        var c = PodContainers.Of(pod);

        Assert.Equal([ContainerRole.Init, ContainerRole.Sidecar, ContainerRole.Main, ContainerRole.Ephemeral], c.Select(x => x.Role));
        Assert.True(c[1].IsNativeSidecar);
        Assert.Equal("sidecar (native)", c[1].RoleLabel);
        Assert.True(c[1].HasPreviousLogs);
        Assert.Equal(137, c[1].LastTermination!.ExitCode);
        Assert.StartsWith("OOMKilled (exit 137)", c[1].LastTerminationText, StringComparison.Ordinal);
        Assert.Equal(Health.Neutral, c[0].Health);   // completed init
        Assert.False(c[3].CanExec);                    // ephemeral
        Assert.True(c[2].HasPreviousLogs);             // restartCount > 0

        // kubectl semantics: native sidecars count toward READY and RESTARTS; init/ephemeral do not.
        var cols = ResourcePresenter.ColumnsFor(ResourceKinds.Pods);
        Assert.Equal("2/2", cols.Single(x => x.Header == "Ready").Value(pod));
        Assert.Equal("3", cols.Single(x => x.Header == "Restarts").Value(pod));
    }

    [Fact]
    public void Init_progress_is_shown_while_pending()
    {
        var pod = Pod("""
            {"metadata":{"name":"p"},
             "spec":{"initContainers":[{"name":"a"},{"name":"b"}],"containers":[{"name":"app"}]},
             "status":{"phase":"Pending",
               "initContainerStatuses":[{"name":"a","state":{"terminated":{"reason":"Completed","exitCode":0}}},{"name":"b","state":{"running":{}}}],
               "containerStatuses":[{"name":"app","state":{"waiting":{"reason":"PodInitializing"}}}]}}
            """);
        Assert.Equal("Init:1/2", ResourcePresenter.PodPhase(pod));
        Assert.Equal(Health.Warning, ResourcePresenter.HealthOf(ResourceKinds.Pods, pod));

        var failing = Pod("""
            {"metadata":{"name":"p"},
             "spec":{"initContainers":[{"name":"a"}],"containers":[{"name":"app"}]},
             "status":{"phase":"Pending","initContainerStatuses":[{"name":"a","state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}
            """);
        Assert.Equal("Init:CrashLoopBackOff", ResourcePresenter.PodPhase(failing));
        Assert.Equal(Health.Error, ResourcePresenter.HealthOf(ResourceKinds.Pods, failing));
    }
}
