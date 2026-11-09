using TelltaleKube.Core.Api;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Core.Tests;

public class DemoClusterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_client_is_read_only_and_reports_a_version()
    {
        using var client = KubeClient.CreateDemo();

        Assert.True(client.Policy.ReadOnly);
        Assert.Equal("v1.34.1", await client.GetServerVersionAsync(Ct));
    }

    [Fact]
    public async Task Lists_are_scoped_by_namespace()
    {
        using var client = KubeClient.CreateDemo();

        var all = await client.ListAsync(ResourceKinds.Pods, ct: Ct);
        var shop = await client.ListAsync(ResourceKinds.Pods, "shop", ct: Ct);

        Assert.Equal(9, all.Items.Count);
        Assert.Equal(6, shop.Items.Count);
        Assert.All(shop.Items, p => Assert.Equal("shop", p.Namespace));
        Assert.Equal("100", shop.ResourceVersion);
    }

    [Fact]
    public async Task Label_and_field_selectors_filter_lists()
    {
        using var client = KubeClient.CreateDemo();

        var web = await client.ListAsync(ResourceKinds.Pods, "shop", "app=web", ct: Ct);
        var pending = await client.ListAsync(ResourceKinds.Pods, "shop", fieldSelector: "status.phase!=Running", ct: Ct);
        var worker = await client.GetAsync(ResourceKinds.Pods, "shop", "worker-5d8b9-klmno", Ct);
        var events = await client.ListAsync(ResourceKinds.Events, "shop", fieldSelector: $"involvedObject.uid={worker.Uid}", ct: Ct);

        Assert.Equal(2, web.Items.Count);
        Assert.Equal("migrate-xyz12", Assert.Single(pending.Items).Name);
        Assert.Contains("worker-5d8b9-klmno", Assert.Single(events.Items).GetString("message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_objects_are_not_found_and_unknown_kinds_are_empty()
    {
        using var client = KubeClient.CreateDemo();

        var ex = await Assert.ThrowsAsync<KubeApiException>(() => client.GetAsync(ResourceKinds.Pods, "shop", "nope", Ct));
        var secrets = await client.ListAsync(ResourceKinds.Secrets, "shop", ct: Ct);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public async Task Writes_are_blocked_before_reaching_the_sample_data()
    {
        using var client = KubeClient.CreateDemo();

        await Assert.ThrowsAsync<SecurityPolicyException>(() => client.DeleteAsync(ResourceKinds.Pods, "shop", "web-6f9c7d-abcde", Ct));
        await Assert.ThrowsAsync<SecurityPolicyException>(() => client.ScaleAsync(ResourceKinds.Deployments, "shop", "web", 0, Ct));
    }

    [Fact]
    public async Task Watches_stay_open_without_events_until_cancelled()
    {
        using var client = KubeClient.CreateDemo();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in client.WatchAsync(ResourceKinds.Pods, "shop", "100", cts.Token)) Assert.Fail($"Unexpected {e.Type}");
        });
    }

    [Fact]
    public async Task Logs_stream_sample_lines_and_previous_is_only_for_the_crashing_pod()
    {
        using var client = KubeClient.CreateDemo();

        var lines = new List<string>();
        await foreach (var line in client.StreamLogsAsync("shop", "worker-5d8b9-klmno", "app", follow: true, previous: true, ct: Ct)) lines.Add(line);
        var ex = await Assert.ThrowsAsync<KubeApiException>(async () =>
        {
            await foreach (var _ in client.StreamLogsAsync("shop", "web-6f9c7d-abcde", "app", follow: false, previous: true, ct: Ct))
            {
            }
        });

        Assert.Contains(lines, l => l.Contains("connection refused", StringComparison.Ordinal));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public async Task Metrics_change_between_samples()
    {
        using var client = KubeClient.CreateDemo();

        var first = (await client.GetJsonAsync("apis/metrics.k8s.io/v1beta1/nodes", Ct))!.ToJsonString();
        var second = (await client.GetJsonAsync("apis/metrics.k8s.io/v1beta1/nodes", Ct))!.ToJsonString();
        var pod = await client.GetJsonAsync("apis/metrics.k8s.io/v1beta1/namespaces/shop/pods/web-6f9c7d-abcde", Ct);

        Assert.NotEqual(first, second);
        Assert.Equal("120m", pod!["containers"]![0]!["usage"]!["cpu"]!.ToString());
    }

    [Fact]
    public async Task Custom_resources_are_discovered()
    {
        using var client = KubeClient.CreateDemo();

        var kinds = await client.DiscoverCustomKindsAsync(Ct);

        Assert.Contains(kinds, k => k.Kind == "Certificate");
    }
}
