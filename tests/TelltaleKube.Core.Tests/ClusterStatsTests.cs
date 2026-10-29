using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Metrics;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Core.Tests;

public class ClusterStatsTests
{
    private const string Nodes = """
        {"items":[
          {"metadata":{"name":"node-a"},"status":{"allocatable":{"cpu":"4","memory":"8Gi","pods":"110"},
            "conditions":[{"type":"Ready","status":"True"},{"type":"MemoryPressure","status":"True"}]}},
          {"metadata":{"name":"node-b"},"spec":{"unschedulable":true},"status":{"allocatable":{"cpu":"2000m","memory":"4Gi","pods":"110"},
            "conditions":[{"type":"Ready","status":"False"}]}}]}
        """;

    private const string Pods = """
        {"items":[
          {"metadata":{"name":"web","namespace":"shop"},"spec":{"nodeName":"node-a","containers":[
             {"name":"app","resources":{"requests":{"cpu":"500m","memory":"1Gi"},"limits":{"cpu":"1","memory":"2Gi"}}},
             {"name":"fluent-bit","resources":{"requests":{"cpu":"50m","memory":"64Mi"}}}]}},
          {"metadata":{"name":"db","namespace":"data"},"spec":{"nodeName":"node-a","containers":[
             {"name":"db","resources":{"requests":{"cpu":"1","memory":"2Gi"}}}]}},
          {"metadata":{"name":"pending","namespace":"shop"},"spec":{"containers":[{"name":"x","resources":{"requests":{"cpu":"8"}}}]}}]}
        """;

    private const string NodeMetrics = """
        {"items":[{"metadata":{"name":"node-a"},"usage":{"cpu":"1200000000n","memory":"3Gi"}},
                  {"metadata":{"name":"node-b"},"usage":{"cpu":"100m","memory":"512Mi"}}]}
        """;

    private const string PodMetrics = """
        {"items":[{"metadata":{"name":"web","namespace":"shop"},"containers":[
                     {"name":"app","usage":{"cpu":"300m","memory":"700Mi"}},{"name":"fluent-bit","usage":{"cpu":"5m","memory":"20Mi"}}]},
                  {"metadata":{"name":"db","namespace":"data"},"containers":[{"name":"db","usage":{"cpu":"900m","memory":"1500Mi"}}]}]}
        """;

    private const string Summary = """
        {"node":{"cpu":{"usageNanoCores":750000000},"memory":{"workingSetBytes":2147483648},"fs":{"usedBytes":40000000000,"capacityBytes":100000000000}},
         "pods":[{"podRef":{"name":"web","namespace":"shop"},"containers":[{"name":"app","cpu":{"usageNanoCores":250000000},"memory":{"workingSetBytes":104857600}}]}]}
        """;

    [Fact]
    public async Task Requests_limits_and_metrics_server_usage_are_aggregated()
    {
        using var client = Client(metrics: true, summary: false);
        var stats = await new StatsCollector(client).CollectAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(UsageSource.MetricsServer, stats.Source);
        var a = stats.Nodes.Single(n => n.Name == "node-a");
        Assert.Equal(1550, a.Requests.CpuMillis);                       // 500m + 50m + 1
        Assert.Equal((3L << 30) + (64L << 20), a.Requests.MemoryBytes); // 1Gi + 64Mi + 2Gi
        Assert.Equal(1000, a.Limits.CpuMillis);
        Assert.Equal(2, a.PodCount);                                     // the unscheduled pod is not counted
        Assert.Equal(1200, a.Usage!.Value.CpuMillis);
        Assert.Equal(["MemoryPressure"], a.Pressures);
        Assert.True(a.Ready);

        var b = stats.Nodes.Single(n => n.Name == "node-b");
        Assert.False(b.Ready);
        Assert.True(b.Unschedulable);

        Assert.Equal(6000, stats.Allocatable.CpuMillis);
        Assert.Equal(1300, stats.Usage!.Value.CpuMillis);
        Assert.Equal(220, stats.PodCapacity);
        Assert.Null(stats.FsUsed);

        var web = stats.Pods.Single(p => p.Name == "web");
        Assert.Equal(305, web.Usage.CpuMillis);
        Assert.Equal(550, web.Requests.CpuMillis);
        Assert.Equal(5, web.Containers["fluent-bit"].CpuMillis);
        Assert.Empty(stats.Notes);
    }

    [Fact]
    public async Task Falls_back_to_kubelet_summary_without_metrics_server()
    {
        using var client = Client(metrics: false, summary: true);
        var stats = await new StatsCollector(client).CollectAsync("shop", TestContext.Current.CancellationToken);

        Assert.Equal(UsageSource.KubeletSummary, stats.Source);
        Assert.Equal(750, stats.Nodes[0].Usage!.Value.CpuMillis);
        Assert.Equal(40_000_000_000, stats.Nodes[0].FsUsedBytes);
        Assert.Equal(200_000_000_000, stats.FsCapacity);
        Assert.All(stats.Pods, p => Assert.Equal("shop", p.Namespace));
        Assert.Equal(250, stats.Pods[0].Usage.CpuMillis);
    }

    [Fact]
    public async Task Without_any_usage_source_requests_still_work()
    {
        using var client = Client(metrics: false, summary: false);
        var stats = await new StatsCollector(client).CollectAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(UsageSource.None, stats.Source);
        Assert.Null(stats.Usage);
        Assert.Equal(1550, stats.Requests.CpuMillis);
        Assert.Contains(stats.Notes, n => n.Contains("metrics-server", StringComparison.Ordinal));
    }

    [Fact]
    public void Effective_pod_requests_follow_scheduler_rules()
    {
        var pod = new KubeObject(JsonNode.Parse("""
            {"spec":{
              "initContainers":[
                {"name":"migrate","resources":{"requests":{"cpu":"2","memory":"128Mi"}}},
                {"name":"proxy","restartPolicy":"Always","resources":{"requests":{"cpu":"100m","memory":"64Mi"}}}],
              "containers":[{"name":"app","resources":{"requests":{"cpu":"500m","memory":"1Gi"}}}],
              "overhead":{"cpu":"10m"}}}
            """)!.AsObject());
        var (req, _) = StatsCollector.PodResources(pod);
        // CPU: max(500m + 100m sidecar, 2000m init) + 10m overhead; memory: max(1Gi + 64Mi, 128Mi)
        Assert.Equal(2010, req.CpuMillis);
        Assert.Equal((1L << 30) + (64L << 20), req.MemoryBytes);
    }

    [Theory]
    [InlineData(250, "250m")]
    [InlineData(1000, "1 core")]
    [InlineData(1500, "1.5 cores")]
    [InlineData(12000, "12 cores")]
    public void Cpu_formatting(long millis, string expected) => Assert.Equal(expected, Quantity.FormatCpu(millis));

    [Fact]
    public async Task Read_only_mode_allows_only_the_kubelet_summary_proxy_path()
    {
        using var client = Client(metrics: false, summary: true, readOnly: true);
        var stats = await new StatsCollector(client).CollectAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(UsageSource.KubeletSummary, stats.Source);

        var exec = typeof(KubeClient).GetMethod("GetJsonAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var other = (Task<JsonNode?>)exec.Invoke(client, ["api/v1/namespaces/x/services/s/proxy/admin", CancellationToken.None])!;
        await Assert.ThrowsAsync<SecurityPolicyException>(() => other);
    }

    private static KubeClient Client(bool metrics, bool summary, bool readOnly = false) =>
        new(new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/v1/nodes" => Ok(Nodes),
                "/api/v1/pods" => Ok(Pods),
                "/apis/metrics.k8s.io/v1beta1/nodes" when metrics => Ok(NodeMetrics),
                "/apis/metrics.k8s.io/v1beta1/pods" when metrics => Ok(PodMetrics),
                _ when path.EndsWith("/proxy/stats/summary", StringComparison.Ordinal) && summary => Ok(Summary),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""") },
            };
        }), new Uri("https://cluster.test/"), SecurityPolicy.Default with { ReadOnly = readOnly });

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
