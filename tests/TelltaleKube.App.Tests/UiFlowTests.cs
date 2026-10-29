using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TelltaleKube.App.Controls;
using TelltaleKube.App.Services;
using TelltaleKube.App.ViewModels;
using TelltaleKube.App.Views;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.Tests;

/// <summary>
/// End-to-end UI tests: the real views and view models against an in-process fake API server, rendered with
/// Skia so screenshots of every key screen land in ./screenshots (uploaded by CI).
/// </summary>
public sealed class UiFlowTests : IDisposable
{
    private static readonly string ScreenshotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
    private readonly FakeApiServer _server = new();

    [AvaloniaFact]
    public async Task Import_connect_browse_and_inspect()
    {
        var (window, main) = await StartAsync(1280, 800);

        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: false);
        main.Home.Refresh();
        await Pump();
        Snap(window, "01-home");
        var card = Assert.Single(main.Home.Clusters);
        Assert.Equal("fake", card.Name);

        await main.Home.ConnectCommand.ExecuteAsync(card);
        var cluster = Assert.IsType<ClusterViewModel>(main.CurrentPage);
        await WaitFor(() => cluster.Content is OverviewViewModel { IsLoading: false, Tiles.Count: > 0 });
        Assert.Null(cluster.Error);
        Assert.Equal("v1.34.1", cluster.ServerVersion);
        Assert.Equal("shop", cluster.SelectedNamespace); // context default namespace
        Assert.Equal("Bearer test-token", _server.LastAuthorization);
        Snap(window, "02-overview");

        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Pods);
        var list = Assert.IsType<ResourceListViewModel>(cluster.Content);
        await WaitFor(() => list.Rows.Count == 4);
        Assert.Equal(Health.Error, list.Rows.Single(r => r.Name.StartsWith("worker", StringComparison.Ordinal)).Health);
        Snap(window, "03-pods");

        list.Selected = list.Rows.First(r => r.Name.StartsWith("web", StringComparison.Ordinal));
        await WaitFor(() => cluster.Detail is not null);
        Assert.True(cluster.Detail!.CanExec);
        Assert.Contains(cluster.Detail.Fields, f => f.Label == "Pod IP" && f.Value == "10.0.0.12");
        await Pump();
        Snap(window, "04-pod-detail");

        list.Filter = "worker";
        await Pump();
        Assert.Single(list.Rows);

        // Phone-sized window switches to the single-pane layout.
        window.Width = 400;
        window.Height = 820;
        await Pump();
        Assert.True(main.IsCompact);
        Assert.NotNull(cluster.CompactDetail);
        Assert.Null(cluster.WideDetail);
        Snap(window, "05-phone-detail");

        cluster.CloseDetailCommand.Execute(null);
        list.Filter = "";
        await Pump();
        Snap(window, "06-phone-list");

        await main.GoBack();
        Assert.IsType<HomeViewModel>(main.CurrentPage);
        Assert.Null(cluster.Client); // disconnect releases the client
        window.Close();
    }

    [AvaloniaFact]
    public async Task Columns_sort_and_filter()
    {
        var (window, main) = await StartAsync(1280, 800);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: false);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Pods);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 4);
        string[] Names() => [.. list.Rows.Select(r => r.Name.Split('-')[0] + (r.Name.Contains("fghij", StringComparison.Ordinal) ? "2" : ""))];

        // Default: namespace, then natural name order.
        Assert.Equal(["migrate", "web", "web2", "worker"], Names());

        // Restarts: worst first, then reversed, then back to default.
        var restarts = list.Columns.Single(c => c.Header == "Restarts");
        list.SortByCommand.Execute(restarts);
        Assert.Equal(SortDirection.Descending, restarts.Sort);
        Assert.Equal("worker", Names()[0]);
        Assert.Equal("web2", Names()[1]);
        list.SortByCommand.Execute(restarts);
        Assert.Equal(SortDirection.Ascending, restarts.Sort);
        Assert.Equal("worker", Names()[^1]);
        list.SortByCommand.Execute(restarts);
        Assert.Equal(SortDirection.None, restarts.Sort);
        Assert.Equal(["migrate", "web", "web2", "worker"], Names());

        // Health: failing first; only one column is sorted at a time.
        list.SortByCommand.Execute(list.NameColumn);
        list.SortByCommand.Execute(list.HealthColumn);
        Assert.Equal(SortDirection.None, list.NameColumn.Sort);
        Assert.Equal(["worker", "migrate"], Names()[..2]);

        // Query language, column filters, health filter.
        list.Filter = "restarts:>0";
        Assert.Equal(2, list.Rows.Count);
        Assert.Equal("2 / 4", list.CountText);
        Assert.True(list.HasActiveFilters);
        list.Filter = "status:running !fghij";
        Assert.Equal(["web"], Names());
        list.Filter = "";
        list.ShowColumnFilters = true;
        list.Columns.Single(c => c.Header == "Status").FilterText = "!Running";
        Assert.Equal(2, list.Rows.Count);
        list.HealthFilter = list.HealthFilterOptions.Single(o => o.Value == HealthScope.Failing);
        Assert.Equal(["worker"], Names());
        await Pump();
        Snap(window, "10-sort-filter");

        // Settings survive switching kinds and coming back.
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Deployments);
        await Pump();
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Pods);
        list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Count == 4);
        Assert.Equal(SortDirection.Descending, list.HealthColumn.Sort);
        Assert.Equal(HealthScope.Failing, list.HealthFilter.Value);
        Assert.True(list.ShowColumnFilters);
        Assert.Single(list.Rows);

        list.ClearFiltersCommand.Execute(null);
        Assert.Equal(4, list.Rows.Count);
        Assert.False(list.HasActiveFilters);

        // Phone layout: sort picker + direction toggle drive the same state.
        window.Width = 400;
        window.Height = 820;
        await Pump();
        list.SortColumn = list.NameColumn;
        Assert.Equal(SortDirection.Ascending, list.NameColumn.Sort);
        list.ToggleSortDirectionCommand.Execute(null);
        Assert.Equal("worker", Names()[0]);
        await Pump();
        Snap(window, "11-phone-sort");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sidecars_are_visualised_and_their_current_and_previous_logs_are_viewable()
    {
        var (window, main) = await StartAsync(1280, 800);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: false);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        cluster.SelectedNamespace = "db";
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Pods);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 1);

        // Pods list: container strip (init, native sidecar, main, 3 classic sidecars), kubectl-style READY/RESTARTS.
        var row = list.Rows[0];
        Assert.True(row.HasContainerStrip);
        Assert.Equal(["galera-init", "mesh-proxy", "mariadb-1", "mariadb-1-healthcheck", "fluent-bit", "slow-log-rotate"], row.Containers.Select(c => c.Info.Name));
        Assert.True(row.Containers.Single(c => c.Info.Name == "mariadb-1").IsMain);
        Assert.Equal(4, row.Containers.Count(c => c.IsSidecar));
        Assert.True(row.Containers[0].IsInit);
        Assert.Equal("5/5", row.C1);
        Assert.Equal("2", row.C3);
        Assert.Contains("OOMKilled", row.Containers.Single(c => c.Info.Name == "fluent-bit").Tooltip, StringComparison.Ordinal);
        list.Filter = "sidecars:fluent";
        Assert.Single(list.Rows);
        list.Filter = "sidecars:istio";
        Assert.Empty(list.Rows);
        list.Filter = "";

        // Detail: one card per container, previous logs only where a restart happened.
        list.Selected = list.Rows[0];
        await WaitFor(() => cluster.Detail is not null);
        var cards = cluster.Detail!.Containers;
        Assert.Equal(6, cards.Count);
        var fluent = cards.Single(c => c.Name == "fluent-bit");
        Assert.True(fluent.HasPreviousLogs);
        Assert.Equal("2 restarts", fluent.Restarts);
        Assert.StartsWith("Last exit: OOMKilled (exit 137)", fluent.LastExit, StringComparison.Ordinal);
        Assert.False(cards.Single(c => c.Name == "mariadb-1").HasPreviousLogs);
        Assert.False(cards.Single(c => c.Name == "galera-init").CanExec);
        await Pump();
        Snap(window, "12-pod-containers");
        window.Width = 400;
        await Pump();
        Snap(window, "14-phone-pod-containers");
        window.Width = 1280;
        await Pump();

        // Previous logs of the restarted sidecar, straight from its card.
        cluster.Detail.OpenPreviousLogsCommand.Execute(fluent);
        var logs = Assert.IsType<LogsViewModel>(main.CurrentPage);
        await WaitFor(() => !logs.IsStreaming && logs.Lines.Count == 2);
        Assert.True(logs.Previous);
        Assert.False(logs.CanFollow);
        Assert.Equal("fluent-bit", logs.SelectedSource!.Name);
        Assert.Contains("out of memory", logs.Lines[1].Text, StringComparison.Ordinal);
        Assert.Contains(_server.Requests, r => r.Contains("/log?", StringComparison.Ordinal) && r.Contains("previous=true", StringComparison.Ordinal) && r.Contains("container=fluent-bit", StringComparison.Ordinal));
        Assert.StartsWith("Restarted 2×", logs.PreviousInfo, StringComparison.Ordinal);

        // Switching to a container that never restarted turns "previous" off and streams current logs.
        logs.SelectedSource = logs.Sources.Single(s => s.Name == "mariadb-1");
        await WaitFor(() => !logs.IsStreaming && logs.Lines.Count == 3);
        Assert.False(logs.Previous);
        Assert.False(logs.CanShowPrevious);
        Assert.Equal("mariadb-1 line 1", logs.Lines[0].Text);
        Assert.Equal("mariadb-1", logs.Sources[0].Name); // main container first

        // All containers merged, each line prefixed with its container.
        logs.Follow = false;
        logs.SelectedSource = logs.Sources.Single(s => s.IsAll);
        await WaitFor(() => !logs.IsStreaming && logs.Lines.Count >= 15);
        Assert.True(logs.ShowPrefixes);
        Assert.Contains(logs.Lines, l => l.Prefix == "mesh-proxy" && l.Text == "mesh-proxy line 1");
        Assert.Contains(logs.Lines, l => l.Prefix == "galera-init");
        Assert.All(logs.Lines, l => Assert.True(l.HasPrefix));
        logs.Filter = "fluent";
        Assert.Equal(3, logs.Lines.Count);
        logs.Filter = "";
        await Pump();
        Snap(window, "13-logs-all-containers");

        // Previous across all containers only queries the restarted one.
        logs.Previous = true;
        await WaitFor(() => !logs.IsStreaming && logs.Lines.Count == 2);
        Assert.All(logs.Lines, l => Assert.Equal("fluent-bit", l.Prefix));

        await main.GoBack();
        cluster.Detail!.OpenContainerShellCommand.Execute(cards.Single(c => c.Name == "fluent-bit"));
        var shell = Assert.IsType<ShellViewModel>(main.CurrentPage);
        Assert.Equal("fluent-bit", shell.SelectedContainer);
        Assert.Equal("mariadb-1", shell.Containers[0]);
        Assert.DoesNotContain("galera-init", shell.Containers);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Resource_stats_without_prometheus()
    {
        var (window, main) = await StartAsync(1280, 900);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: true);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        cluster.SelectedNamespace = "All namespaces";
        await WaitFor(() => cluster.Content is OverviewViewModel { IsLoading: false, Gauges.Count: > 0 });
        var overview = (OverviewViewModel)cluster.Content!;

        // metrics-server: live usage gauges, requests vs allocatable (3 nodes × 4 cores), pods vs slots.
        Assert.StartsWith("Live usage from metrics-server", overview.UsageSourceText, StringComparison.Ordinal);
        Assert.Equal(["CPU usage", "Memory usage", "CPU requested", "Memory requested", "Pods"], overview.Gauges.Select(g => g.Label));
        var cpuReq = overview.Gauges.Single(g => g.Label == "CPU requested");
        Assert.Equal("1 core", cpuReq.Value);                       // 4 pods × 250m, all on node-a
        Assert.Equal(8.3, cpuReq.Percent);                          // of 12 cores
        Assert.Equal("4", overview.Gauges.Single(g => g.Label == "Pods").Value);

        // Nodes: pressure/readiness flags, per-node usage vs allocatable.
        Assert.Equal(3, overview.NodeRows.Count);
        Assert.Contains("NotReady", overview.NodeRows.Single(n => n.Name == "node-c").Flags, StringComparison.Ordinal);
        Assert.False(overview.NodeRows.Single(n => n.Name == "node-c").HasUsage);
        Assert.InRange(overview.NodeRows.Single(n => n.Name == "node-a").CpuUsage, 60, 71);
        Assert.Equal("4/110 pods", overview.NodeRows.Single(n => n.Name == "node-a").PodsText);

        // Top consumers, with usage relative to requests/limits.
        Assert.Equal("worker-5d8b9-klmno", overview.TopCpu[0].Name);
        Assert.Contains("360% of request", overview.TopCpu[0].Detail, StringComparison.Ordinal);
        Assert.Contains("94% of limit", overview.TopMemory[0].Detail, StringComparison.Ordinal);

        // A second sample feeds the sparkline history.
        await overview.LoadStatsAsync();
        Assert.True(overview.Gauges[0].HasHistory);
        Assert.True(overview.Gauges[0].History!.Count >= 2);
        await Pump();
        Snap(window, "15-resources");

        // Clicking a top consumer jumps to that pod.
        overview.OpenPodCommand.Execute(overview.TopCpu[0]);
        var list = Assert.IsType<ResourceListViewModel>(cluster.Content);
        await WaitFor(() => list.Count > 0);
        Assert.Equal(["worker-5d8b9-klmno"], list.Rows.Select(r => r.Name));

        // Pod detail: per-container usage against requests/limits.
        list.Selected = list.Rows[0];
        await WaitFor(() => cluster.Detail is { HasUsageFields: true } d && d.Containers[0].HasUsage);
        var card = cluster.Detail!.Containers[0];
        Assert.Equal("cpu req 250m · lim -   mem req 256Mi · lim 512Mi", card.ResourcesText);
        Assert.Equal("using 900m cpu (360% of request) · 480Mi mem (94% of limit)", card.UsageText);
        Assert.Contains(cluster.Detail.UsageFields, f => f.Label == "CPU used" && f.Value.StartsWith("900m", StringComparison.Ordinal));
        Snap(window, "16-pod-usage");

        // Node detail: utilisation + allocation.
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Nodes);
        var nodes = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => nodes.Rows.Count == 3);
        nodes.Selected = nodes.Rows.Single(r => r.Name == "node-a");
        await WaitFor(() => cluster.Detail is { HasUsageFields: true });
        Assert.Contains(cluster.Detail!.UsageFields, f => f.Label == "CPU requested" && f.Value.StartsWith("1 core (25%)", StringComparison.Ordinal));
        Assert.Contains(cluster.Detail.UsageFields, f => f.Label == "Pods" && f.Value == "4 of 110");

        await main.GoBack();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Resource_stats_degrade_to_requests_without_metrics_server()
    {
        _server.MetricsAvailable = false;
        var (window, main) = await StartAsync(1280, 900);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: false);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.Content is OverviewViewModel { IsLoading: false, Gauges.Count: > 0 });
        var overview = (OverviewViewModel)cluster.Content!;

        Assert.StartsWith("Requests and limits only", overview.UsageSourceText, StringComparison.Ordinal);
        Assert.DoesNotContain(overview.Gauges, g => g.Label.EndsWith("usage", StringComparison.Ordinal));
        Assert.Contains(overview.StatsNotes, n => n.Contains("metrics-server", StringComparison.Ordinal));
        Assert.False(overview.HasTopPods);
        Assert.StartsWith("req 1 core", overview.NodeRows.Single(n => n.Name == "node-a").CpuText, StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Requests, r => r.Contains("/proxy/", StringComparison.Ordinal) && !r.EndsWith("/stats/summary", StringComparison.Ordinal));
        await Pump();
        Snap(window, "17-resources-no-metrics");
        await main.GoBack();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Phone_overview_shows_stat_cards_two_per_row()
    {
        // Pixel 8: 412 × 915 dp.
        var (window, main) = await StartAsync(412, 915);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: true);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.Content is OverviewViewModel { IsLoading: false, Tiles.Count: > 0, Gauges.Count: > 0 });
        await Pump();
        Assert.True(main.IsCompact);

        var panels = window.GetVisualDescendants().OfType<CardPanel>().ToList();
        Assert.Equal(2, panels.Count);                       // stat tiles and resource gauges
        foreach (var panel in panels)
        {
            var cards = panel.Children.Where(c => c.IsVisible).ToList();
            Assert.True(cards.Count >= 2);
            Assert.Equal(cards[0].Bounds.Y, cards[1].Bounds.Y);              // side by side...
            Assert.True(cards[1].Bounds.X > cards[0].Bounds.Right);
            Assert.True(cards[1].Bounds.Right <= panel.Bounds.Width + 0.5); // ...within the screen
            Assert.True(cards[0].Bounds.Width >= 150);
        }
        // Bars stay inside their card (Fluent's 200px minimum would overflow a phone-width card).
        foreach (var bar in panels[1].GetVisualDescendants().OfType<ProgressBar>()) Assert.True(bar.Bounds.X >= 0 && bar.Bounds.Width <= ((Control)bar.GetVisualParent()!).Bounds.Width);
        Snap(window, "25-phone-overview");
        await main.GoBack();
        window.Close();
    }

    [Theory]
    [InlineData(380, 2, 184)]     // phone: two columns that fill the width
    [InlineData(300, 1, 300)]     // too narrow for two: one full-width column
    [InlineData(1000, 4, 220)]    // desktop: fixed-width cards
    public void Card_panel_layout(double available, int columns, double width) =>
        Assert.Equal((columns, width), CardPanel.Layout(available, 220, 150, 12));

    [AvaloniaFact]
    public async Task Tablet_landscape_with_detail_open_uses_the_narrow_list_layout()
    {
        // iPad Air landscape: wide enough for the three-pane layout, but the list itself gets little room.
        var (window, main) = await StartAsync(1180, 820);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: false);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Pods);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 4);

        Assert.False(main.IsCompact);
        Assert.False(list.IsNarrow);         // list has the full width: one-row toolbar + column headers

        list.Selected = list.Rows[0];
        await WaitFor(() => cluster.WideDetail is not null && list.IsNarrow);
        Assert.True(list.UseCompactRows);    // stacked toolbar, two-line rows, sort picker
        list.HealthFilter = list.HealthFilterOptions[2];
        await Pump();
        Snap(window, "18-tablet-detail");

        cluster.CloseDetailCommand.Execute(null);
        await WaitFor(() => !list.IsNarrow);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CronJob_runs_link_through_to_logs_and_owner_chains_go_all_the_way_up()
    {
        var (window, main) = await StartAsync(1440, 900);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: false);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        cluster.SelectedNamespace = "batch";
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.CronJobs);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 1);
        Assert.Equal("1", list.Rows[0].C2);       // Active
        Assert.Equal("1d", list.Rows[0].C4);      // Last success (2 days minus a few minutes ago)

        // CronJob detail: run history, newest first.
        list.Selected = list.Rows[0];
        await WaitFor(() => cluster.Detail is { Runs.Count: 3 });
        var cron = cluster.Detail!;
        Assert.Equal(["Running", "Failed (BackoffLimitExceeded)", "Succeeded"], cron.Runs.Select(r => r.Status));
        Assert.True(cron.HasFailedRun);
        Assert.Equal("3m25s", cron.Runs[2].Duration);
        await Pump();
        Snap(window, "19-cronjob-runs");

        // One tap from the CronJob to the running run's live logs.
        await cron.OpenLatestRunLogsCommand.ExecuteAsync(null);
        var logs = Assert.IsType<LogsViewModel>(main.CurrentPage);
        Assert.Contains("backup-29000003-pqrst", logs.Title, StringComparison.Ordinal);
        await WaitFor(() => logs.Lines.Count == 3);
        Assert.Equal("backup line 1", logs.Lines[0].Text);
        await main.GoBack();

        // ...and to the last failed run: its final attempt's pod.
        await cron.OpenLastFailedRunLogsCommand.ExecuteAsync(null);
        logs = Assert.IsType<LogsViewModel>(main.CurrentPage);
        Assert.Contains("backup-29000002-klmno", logs.Title, StringComparison.Ordinal);
        await main.GoBack();

        // Drill into a run: the Job shows its attempts (pods) and links back up to the CronJob.
        await cron.OpenRunCommand.ExecuteAsync(cron.Runs[1]);
        await WaitFor(() => cluster.Detail is { Kind.Kind: "Job", HasRelatedPods: true, HasOwnerChain: true });
        var job = cluster.Detail!;
        Assert.True(cluster.HasDetailHistory);
        Assert.Equal(2, job.RelatedPods.Count);
        Assert.Equal("Pods (attempts)", job.PodsTitle);
        Assert.Equal(["CronJob backup"], job.OwnerChain.Select(o => o.Text));
        Snap(window, "20-job-attempts");

        // Pod of that run: full chain Pod › Job › CronJob, every step clickable.
        await job.OpenRelatedPodCommand.ExecuteAsync(job.RelatedPods[0]);
        await WaitFor(() => cluster.Detail is { IsPod: true, OwnerChain.Count: 2 });
        var pod = cluster.Detail!;
        Assert.Equal(["Job backup-29000002", "CronJob backup"], pod.OwnerChain.Select(o => o.Text));
        Assert.All(pod.OwnerChain, o => Assert.True(o.CanOpen));
        await pod.OpenOwnerCommand.ExecuteAsync(pod.OwnerChain[1]);
        await WaitFor(() => cluster.Detail is { IsCronJob: true });

        // Back walks the trail: CronJob → Pod → Job → CronJob.
        cluster.DetailBackCommand.Execute(null);
        Assert.True(cluster.Detail!.IsPod);
        cluster.DetailBackCommand.Execute(null);
        Assert.Equal("Job", cluster.Detail!.Kind.Kind);

        // Deployment chain: Pod › ReplicaSet › Deployment, and the Deployment lists its pods.
        await cluster.OpenRelatedAsync(ResourceKinds.Pods, "batch", "api-7d9f8-xk2p4");
        await WaitFor(() => cluster.Detail is { IsPod: true, OwnerChain.Count: 2 });
        Assert.Equal(["ReplicaSet api-7d9f8", "Deployment api"], cluster.Detail!.OwnerChain.Select(o => o.Text));
        await cluster.Detail.OpenOwnerCommand.ExecuteAsync(cluster.Detail.OwnerChain[1]);
        await WaitFor(() => cluster.Detail is { Kind.Kind: "Deployment", HasRelatedPods: true });
        Assert.Equal("api-7d9f8-xk2p4", cluster.Detail!.RelatedPods.Single().Name);
        cluster.Detail.OpenLatestPodLogsCommand.Execute(null);
        Assert.IsType<LogsViewModel>(main.CurrentPage);
        await main.GoBack();

        // Run now = kubectl create job --from=cronjob, owned by the CronJob so it joins the run history.
        cluster.CloseDetailCommand.Execute(null);
        list.Selected = null;
        list.Selected = list.Rows[0];
        await WaitFor(() => cluster.Detail is { Runs.Count: 3 });
        cron = cluster.Detail!;
        var run = cron.RunNowCommand.ExecuteAsync(null);
        await WaitFor(() => main.Dialog is not null);
        main.Dialog!.ChooseCommand.Execute(main.Dialog.Buttons.Single(b => b.Result == "ok"));
        await run;
        var created = Assert.Single(_server.CreatedJobs);
        Assert.Equal("cron-uid", created["metadata"]!["ownerReferences"]![0]!["uid"]!.ToString());
        Assert.Equal("Never", created["spec"]!["template"]!["spec"]!["restartPolicy"]!.ToString());
        await WaitFor(() => cron.Runs.Count == 4);
        Assert.Contains(cron.Runs, r => r.IsManual);

        // Suspend / resume.
        var suspend = cron.ToggleSuspendCommand.ExecuteAsync(null);
        await WaitFor(() => main.Dialog is not null);
        main.Dialog!.ChooseCommand.Execute(main.Dialog.Buttons.Single(b => b.Result == "ok"));
        await suspend;
        Assert.True(cron.IsSuspended);
        Assert.Contains(_server.Requests, r => r.StartsWith("PATCH /apis/batch/v1/namespaces/batch/cronjobs/backup", StringComparison.Ordinal));

        await main.GoBack();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Node_list_shows_per_node_usage()
    {
        var (window, main) = await StartAsync(1280, 800);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: true);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Nodes);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 3 && list.Rows.All(r => r.HasNodeUsage));

        Assert.Equal(["Status", "Roles", "CPU", "Memory", "CPU req", "Mem req", "Pods", "Disk"], list.Columns.Skip(3).Take(8).Select(c => c.Header));
        Assert.StartsWith("CPU / Memory: live usage (metrics-server)", list.NodeStatsSource, StringComparison.Ordinal);
        var a = list.Rows.Single(r => r.Name == "node-a");
        Assert.EndsWith("%", a.C3, StringComparison.Ordinal);
        Assert.Equal("69%", a.C4);                                   // 11Gi of 16Gi
        Assert.Equal("4/110", a.C7);
        Assert.EndsWith("%", a.C5, StringComparison.Ordinal);
        Assert.Contains("NotReady", list.Rows.Single(r => r.Name == "node-c").C1, StringComparison.Ordinal);
        list.Filter = "pods:>3";
        Assert.Contains("node-a", list.Rows.Select(r => r.Name));
        list.Filter = "";

        // Sort by CPU (busiest first after reversing) and filter on usage.
        var cpu = list.Columns.Single(c => c.Header == "CPU");
        list.SortByCommand.Execute(cpu);
        list.SortByCommand.Execute(cpu);
        Assert.Equal(SortDirection.Descending, cpu.Sort);
        Assert.Equal("node-a", list.Rows[0].Name);
        list.Filter = "cpu:>50";
        Assert.Equal(["node-a"], list.Rows.Select(r => r.Name));
        list.Filter = "memory:<20";
        Assert.Equal(["node-b"], list.Rows.Select(r => r.Name));          // 3Gi of 16Gi
        Assert.NotEmpty(list.Rows.Single().C3);
        list.Filter = "";
        Assert.Equal("", list.Rows.Single(r => r.Name == "node-c").C3);   // NotReady: no live usage, no fake number
        Assert.Equal("node-c", list.Rows[^1].Name);                        // ...and it sorts last
        list.Filter = "";
        await Pump();
        Snap(window, "21-node-list");

        window.Width = 400;
        await Pump();
        Snap(window, "22-node-list-phone");
        await main.GoBack();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Custom_resources_are_grouped_by_product()
    {
        var (window, main) = await StartAsync(1280, 900);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: true);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.CustomFamilies.Any());

        // One collapsed row per product; built-in API groups never show up.
        Assert.Equal(["cert-manager", "example.com", "Flux", "Gateway API", "Helm (k3s)", "Longhorn"], cluster.CustomFamilies.Select(f => f.Title));
        Assert.Equal("3", cluster.CustomFamilies.Single(f => f.Title == "Longhorn").Badge);
        Assert.DoesNotContain(cluster.NavEntries, n => n.IsChild);
        Assert.Equal(["Gitrepositories", "Helmreleases"], cluster.FamilyKinds("fluxcd.io").Select(k => k.Title));

        // Clicking a family expands it in place; the current page and highlight stay put.
        var overview = cluster.Content;
        cluster.SelectedNav = cluster.CustomFamilies.Single(f => f.Title == "Longhorn");
        await WaitFor(() => cluster.NavEntries.Any(n => n.IsChild));
        Assert.Same(overview, cluster.Content);
        Assert.True(cluster.SelectedNav?.IsOverview);
        var index = cluster.NavEntries.ToList().FindIndex(n => n.Family == "longhorn.io");
        Assert.True(cluster.NavEntries[index].Expanded);
        Assert.Equal(["Engines", "Nodes", "Volumes"], cluster.NavEntries.Skip(index + 1).Take(3).Select(n => n.Title));
        Assert.Equal("lhv", cluster.NavEntries[index + 3].Badge);

        cluster.SelectedNav = cluster.NavEntries[index + 3];
        var list = Assert.IsType<ResourceListViewModel>(cluster.Content);
        Assert.Equal("volumes.longhorn.io", list.Kind.Key);
        await Pump();
        Snap(window, "23-custom-resource-groups");

        // Collapsing hides the kinds; opening a kind from elsewhere (e.g. an owner link) re-expands its family.
        cluster.ToggleFamily("longhorn.io");
        Assert.DoesNotContain(cluster.NavEntries, n => n.IsChild);
        Assert.Same(list, cluster.Content);
        var helmRelease = cluster.FindKind("helm.toolkit.fluxcd.io/v2", "HelmRelease");
        Assert.NotNull(helmRelease);
        cluster.ShowKind(helmRelease!);
        Assert.Equal("helmreleases.helm.toolkit.fluxcd.io", Assert.IsType<ResourceListViewModel>(cluster.Content).Kind.Key);
        Assert.Equal("Helmreleases", cluster.SelectedNav?.Title);
        await main.GoBack();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Custom_resources_show_their_printer_columns()
    {
        var (window, main) = await StartAsync(1280, 800);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: true);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.CustomFamilies.Any());
        cluster.ShowKind(cluster.FamilyKinds("cert-manager.io").Single(k => k.Title == "Certificaterequests").Kind!);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 3);

        // The CRD's columns (Age and Name come from the list itself), including the "wide" Status message.
        Assert.Equal(["Approved", "Denied", "Ready", "Issuer", "Requester", "Status"], list.Columns.Skip(3).Take(6).Select(c => c.Header));
        var failed = list.Rows.Single(r => r.Name == "api-tls-1");
        Assert.Equal("False", failed.C3);
        Assert.Equal("letsencrypt", failed.C4);
        Assert.Equal("Failed to wait for order resource to be ready", failed.C6);
        Assert.Equal(Health.Error, failed.Health);
        Assert.Equal(Health.Ok, list.Rows.Single(r => r.Name == "web-tls-1").Health);

        // Sortable and filterable like any other column.
        list.Filter = "ready:!True";
        Assert.Equal(["api-tls-1", "internal-ca-7"], list.Rows.Select(r => r.Name).Order());
        list.Filter = "issuer:internal approved:!True";
        Assert.Equal(["internal-ca-7"], list.Rows.Select(r => r.Name));
        list.Filter = "";
        list.SortByCommand.Execute(list.Columns.Single(c => c.Header == "Ready"));
        Assert.Equal(["api-tls-1", "web-tls-1", "internal-ca-7"], list.Rows.Select(r => r.Name));
        await Pump();
        Snap(window, "24-custom-resource-columns");

        // The detail pane lists the same fields.
        list.Selected = failed;
        await WaitFor(() => cluster.Detail is not null);
        Assert.Contains(cluster.Detail!.Fields, f => f.Label == "Issuer" && f.Value == "letsencrypt");
        await main.GoBack();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Node_list_falls_back_to_requests_without_metrics()
    {
        _server.MetricsAvailable = false;
        var (window, main) = await StartAsync(1280, 800);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: true);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Nodes);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 3 && list.Rows.All(r => r.HasNodeUsage));

        Assert.StartsWith("CPU / Memory: no live usage", list.NodeStatsSource, StringComparison.Ordinal);
        var a = list.Rows.Single(r => r.Name == "node-a");
        Assert.Equal("", a.C3);                                              // no fake live number
        Assert.Equal("25%", a.C5);                                           // CPU req: 4 × 250m of 4 cores
        await main.GoBack();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Port_forward_from_the_pod_with_link_copy_and_stop()
    {
        var (window, main) = await StartAsync(1280, 800);
        var opened = new List<Uri>();
        main.OpenUrl = uri =>
        {
            opened.Add(uri);
            return Task.CompletedTask;
        };
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: false);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Pods);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 4);
        list.Selected = list.Rows.First(r => r.Name.StartsWith("web", StringComparison.Ordinal));
        await WaitFor(() => cluster.Detail is { HasPodPorts: true });
        var detail = cluster.Detail!;

        // Declared port, one click to forward. Nothing opens by itself.
        var row = Assert.Single(detail.PodPorts);
        Assert.Equal("8080/TCP", row.Label);
        Assert.True(row.IsNotForwarded);
        detail.ForwardPortCommand.Execute(row);
        Assert.True(row.IsForwarded);
        var fwd = row.Forward!;
        Assert.Equal($"http://127.0.0.1:{fwd.Forwarder.LocalPort}/", fwd.UrlText);
        Assert.Empty(opened);
        Assert.Single(cluster.PortForwards);

        // Forwarding the same port again reuses the forward.
        detail.ForwardPortCommand.Execute(row);
        Assert.Single(cluster.PortForwards);

        // The link opens the browser; Copy puts the URL on the clipboard.
        await detail.OpenForwardCommand.ExecuteAsync(row);
        Assert.Equal(fwd.Url, Assert.Single(opened));
        string? copied = null;
        main.CopyToClipboard = t =>
        {
            copied = t;
            return Task.CompletedTask;
        };
        await detail.CopyForwardUrlCommand.ExecuteAsync(row);
        Assert.Equal(fwd.UrlText, copied);
        await Pump();
        Snap(window, "26-pod-port-forward");

        // The forward is listening on loopback right now.
        using (var probe = new System.Net.Sockets.TcpClient())
        {
            await probe.ConnectAsync(System.Net.IPAddress.Loopback, fwd.Forwarder.LocalPort, TestContext.Current.CancellationToken);
            Assert.True(probe.Connected);
        }

        // Port Forwards page lists it with a link back to the pod.
        cluster.SelectedNav = cluster.NavEntries.First(n => n.IsPortForwards);
        Assert.IsType<PortForwardsViewModel>(cluster.Content);
        await Pump();
        Snap(window, "27-port-forwards-page");

        // Stop from the pod panel: row flips back to "Forward", the listener is closed.
        list = null!;
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Pods);
        list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 4);
        list.Selected = list.Rows.First(r => r.Name == fwd.Pod);
        await WaitFor(() => cluster.Detail is { HasPodPorts: true } d && d.PodPorts[0].IsForwarded);
        await cluster.Detail!.StopForwardCommand.ExecuteAsync(cluster.Detail.PodPorts[0]);
        Assert.Empty(cluster.PortForwards);
        Assert.True(cluster.Detail.PodPorts[0].IsNotForwarded);
        using (var probe = new System.Net.Sockets.TcpClient())
        {
            await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(async () =>
                await probe.ConnectAsync(System.Net.IPAddress.Loopback, fwd.Forwarder.LocalPort, TestContext.Current.CancellationToken));
        }

        await main.GoBack();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Read_only_cluster_hides_and_blocks_mutations()
    {
        var (window, main) = await StartAsync(1280, 800);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: true);
        main.Home.Refresh();

        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = Assert.IsType<ClusterViewModel>(main.CurrentPage);
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        Assert.True(cluster.IsReadOnly);

        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Deployments);
        var list = Assert.IsType<ResourceListViewModel>(cluster.Content);
        await WaitFor(() => list.Rows.Count == 2);
        list.Selected = list.Rows[0];
        await WaitFor(() => cluster.Detail is not null);

        Assert.False(cluster.Detail!.CanMutate);
        Assert.False(cluster.Detail.CanScale);
        await Assert.ThrowsAsync<SecurityPolicyException>(() => cluster.Client!.DeleteAsync(ResourceKinds.Deployments, "shop", "web"));
        Assert.DoesNotContain(_server.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
        Snap(window, "07-read-only");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Delete_requires_confirmation_and_cancel_sends_nothing()
    {
        var (window, main) = await StartAsync(1280, 800);
        await main.State.ImportAsync(_server.Kubeconfig(), ["fake"], readOnly: false);
        main.Home.Refresh();
        await main.Home.ConnectCommand.ExecuteAsync(main.Home.Clusters[0]);
        var cluster = (ClusterViewModel)main.CurrentPage;
        await WaitFor(() => cluster.ServerVersion.Length > 0);
        cluster.SelectedNav = cluster.NavEntries.First(n => n.Kind == ResourceKinds.Pods);
        var list = (ResourceListViewModel)cluster.Content!;
        await WaitFor(() => list.Rows.Count == 4);
        list.Selected = list.Rows[0];
        await WaitFor(() => cluster.Detail is not null);

        var delete = cluster.Detail!.DeleteCommand.ExecuteAsync(null);
        await WaitFor(() => main.Dialog is not null);
        Snap(window, "08-confirm-delete");
        main.Dialog!.Cancel();
        await delete;
        Assert.DoesNotContain(_server.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));

        delete = cluster.Detail!.DeleteCommand.ExecuteAsync(null);
        await WaitFor(() => main.Dialog is not null);
        main.Dialog!.ChooseCommand.Execute(main.Dialog.Buttons.Single(b => b.Result == "ok"));
        await delete;
        Assert.Contains(_server.Requests, r => r.StartsWith("DELETE /api/v1/namespaces/shop/pods/", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Import_view_previews_contexts_and_flags_risky_auth()
    {
        var (window, main) = await StartAsync(900, 700);
        var import = new ImportViewModel(main, main.State, "");
        main.Navigate(import);
        import.Text = """
            clusters:
            - name: a
              cluster: { server: "https://a.example.com", insecure-skip-tls-verify: true }
            users:
            - name: u
              user:
                exec: { command: aws, args: [eks, get-token] }
            contexts:
            - name: risky
              context: { cluster: a, user: u }
            """;
        await Pump();
        var option = Assert.Single(import.Contexts);
        Assert.True(option.Warning);
        Assert.Contains("insecure TLS", option.Auth, StringComparison.Ordinal);
        Snap(window, "09-import");

        import.Text = "not: [valid";
        Assert.NotNull(import.Error);
        window.Close();
    }

    private static async Task<(Window, MainViewModel)> StartAsync(double width, double height)
    {
        var state = new AppState(new SecretVault(Directory.CreateTempSubdirectory("telltale-kube-ui").FullName, new EphemeralKeyProtector()));
        await state.InitialiseAsync();
        state.Settings.UseSystemKubeconfig = false; // keep the developer's ~/.kube/config out of the cluster list
        var main = new MainViewModel(state);
        var window = new MainWindow { DataContext = main, Width = width, Height = height };
        window.Show();
        await Pump();
        return (window, main);
    }

    private static async Task Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        await Pump();
    }

    private static void Snap(Window window, string name)
    {
        Directory.CreateDirectory(ScreenshotDir);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ScreenshotDir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    public void Dispose() => _server.Dispose();
}
