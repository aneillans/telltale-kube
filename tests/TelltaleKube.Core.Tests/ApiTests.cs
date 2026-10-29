using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Core.Tests;

public class ApiTests
{
    private static readonly Uri Base = new("https://cluster.test/");

    [Fact]
    public async Task List_restores_kind_and_returns_resource_version()
    {
        var handler = new FakeHandler(req =>
        {
            Assert.Equal("/api/v1/namespaces/default/pods", req.RequestUri!.AbsolutePath);
            return Json("""{"metadata":{"resourceVersion":"42"},"items":[{"metadata":{"name":"web-1","namespace":"default","uid":"u1"}}]}""");
        });
        using var client = new KubeClient(handler, Base);

        var list = await client.ListAsync(ResourceKinds.Pods, "default", ct: TestContext.Current.CancellationToken);

        Assert.Equal("42", list.ResourceVersion);
        var pod = Assert.Single(list.Items);
        Assert.Equal("Pod", pod.Kind);
        Assert.Equal("v1", pod.ApiVersion);
        Assert.Equal("web-1", pod.Name);
    }

    [Fact]
    public async Task Api_errors_surface_the_server_message()
    {
        using var client = new KubeClient(new FakeHandler(_ => Json("""{"kind":"Status","message":"pods is forbidden: User \"bob\" cannot list"}""", HttpStatusCode.Forbidden)), Base);
        var ex = await Assert.ThrowsAsync<KubeApiException>(() => client.ListAsync(ResourceKinds.Pods, "default", ct: TestContext.Current.CancellationToken));
        Assert.True(ex.IsForbidden);
        Assert.Contains("cannot list", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_streams_events_and_raises_gone()
    {
        var body = """
            {"type":"ADDED","object":{"metadata":{"name":"a","resourceVersion":"2"}}}
            {"type":"BOOKMARK","object":{"metadata":{"resourceVersion":"3"}}}
            {"type":"ERROR","object":{"kind":"Status","code":410,"message":"too old resource version"}}

            """;
        using var client = new KubeClient(new FakeHandler(req =>
        {
            Assert.Contains("watch=1", req.RequestUri!.Query, StringComparison.Ordinal);
            Assert.Contains("resourceVersion=1", req.RequestUri!.Query, StringComparison.Ordinal);
            return Json(body);
        }), Base);

        var events = new List<WatchEvent>();
        var ex = await Assert.ThrowsAsync<KubeApiException>(async () =>
        {
            await foreach (var e in client.WatchAsync(ResourceKinds.Deployments, "default", "1", TestContext.Current.CancellationToken)) events.Add(e);
        });
        Assert.True(ex.IsGone);
        Assert.Equal(["ADDED", "BOOKMARK"], events.Select(e => e.Type));
        Assert.Equal("Deployment", events[0].Resource.Kind);
    }

    [Fact]
    public async Task Read_only_mode_blocks_mutations_before_they_are_sent()
    {
        var sent = 0;
        using var client = new KubeClient(new FakeHandler(_ =>
        {
            sent++;
            return Json("{}");
        }), Base, SecurityPolicy.Default with { ReadOnly = true });

        await Assert.ThrowsAsync<SecurityPolicyException>(() => client.DeleteAsync(ResourceKinds.Pods, "default", "x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SecurityPolicyException>(() => client.ScaleAsync(ResourceKinds.Deployments, "default", "x", 3, TestContext.Current.CancellationToken));
        Assert.Equal(0, sent);

        await client.ListAsync(ResourceKinds.Pods, "default", ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task Scale_sends_merge_patch_to_scale_subresource()
    {
        HttpRequestMessage? captured = null;
        string? content = null;
        using var client = new KubeClient(new FakeHandler(req =>
        {
            captured = req;
            content = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("{}");
        }), Base);

        await client.ScaleAsync(ResourceKinds.Deployments, "shop", "web", 5, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Patch, captured!.Method);
        Assert.Equal("/apis/apps/v1/namespaces/shop/deployments/web/scale", captured.RequestUri!.AbsolutePath);
        Assert.Equal("application/merge-patch+json", captured.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(5, JsonNode.Parse(content!)!["spec"]!["replicas"]!.GetValue<int>());
    }

    [Fact]
    public async Task Names_are_escaped_in_paths()
    {
        string? path = null;
        using var client = new KubeClient(new FakeHandler(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            return Json("""{"metadata":{"name":"x"}}""");
        }), Base);
        await client.GetAsync(ResourceKinds.Pods, "ns", "../../secrets", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("/secrets", path!.Replace("%2F", "", StringComparison.Ordinal).Replace("..", "", StringComparison.Ordinal)[..^1], StringComparison.Ordinal);
        Assert.Contains("%2F", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Exec_status_parsing()
    {
        Assert.Equal(0, ExecSession.ParseStatus("""{"status":"Success"}""").ExitCode);
        var failed = ExecSession.ParseStatus("""{"status":"Failure","message":"command terminated with non-zero exit code","details":{"causes":[{"reason":"ExitCode","message":"137"}]}}""");
        Assert.Equal(137, failed.ExitCode);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
