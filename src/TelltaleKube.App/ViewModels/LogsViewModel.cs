using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.ViewModels;

public sealed record LogSource(ContainerInfo? Container)
{
    public bool IsAll => Container is null;
    public string Name => Container?.Name ?? "All containers";
    public string Detail => Container is { } c
        ? c.RestartCount > 0 ? $"{c.RoleLabel} · {c.RestartCount} restarts" : c.RoleLabel
        : "merged, prefixed";
}

/// <summary>One log line; <see cref="Prefix"/> is set when several containers are merged.</summary>
public sealed record LogLine(string? Prefix, string Text, IBrush? PrefixBrush)
{
    public bool HasPrefix => Prefix is not null;
    public override string ToString() => Prefix is null ? Text : $"[{Prefix}] {Text}";
}

/// <summary>
/// Streams container logs: any container (main, sidecars, init, debug) or all of them merged, the current or the
/// previous (pre-restart) instance, with follow, timestamps and filtering. Holds a bounded buffer.
/// </summary>
public sealed partial class LogsViewModel : PageViewModel
{
    private const int MaxLines = 5000;

    // Distinguishable on both light and dark backgrounds.
    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#1FA99C")), new SolidColorBrush(Color.Parse("#7C6CF2")), new SolidColorBrush(Color.Parse("#E0A100")),
        new SolidColorBrush(Color.Parse("#E5689A")), new SolidColorBrush(Color.Parse("#3B8FE5")), new SolidColorBrush(Color.Parse("#8AA34A")),
    ];

    private readonly ClusterViewModel _cluster;
    private readonly KubeObject _pod;
    private readonly List<LogLine> _all = [];
    private bool _initialising = true;
    private CancellationTokenSource? _cts;

    public LogsViewModel(ClusterViewModel cluster, KubeObject pod, string? container = null, bool previous = false)
    {
        _cluster = cluster;
        _pod = pod;
        var containers = PodContainers.Of(pod).Where(c => c.HasLogs || c.Role != ContainerRole.Init).ToList();
        // Main container first, then the rest in lifecycle order.
        foreach (var c in containers.OrderBy(c => c.Role == ContainerRole.Main ? 0 : 1)) Sources.Add(new LogSource(c));
        if (containers.Count > 1) Sources.Add(new LogSource((ContainerInfo?)null));

        SelectedSource = Sources.FirstOrDefault(s => s.Container?.Name == container) ?? Sources.FirstOrDefault();
        Previous = previous && CanShowPrevious;
        _initialising = false;
        Restart();
    }

    public override string Title => $"Logs · {_pod.Name}";
    public ObservableCollection<LogSource> Sources { get; } = [];
    public ObservableCollection<LogLine> Lines { get; } = [];
    public bool HasMultipleSources => Sources.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowPrevious), nameof(PreviousInfo), nameof(ShowPrefixes))]
    public partial LogSource? SelectedSource { get; set; }

    [ObservableProperty]
    public partial bool Follow { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFollow), nameof(PreviousInfo))]
    public partial bool Previous { get; set; }

    [ObservableProperty]
    public partial bool Timestamps { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool IsStreaming { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>Previous logs only exist once a container has restarted.</summary>
    public bool CanShowPrevious => SelectedSource switch
    {
        { Container: { } c } => c.HasPreviousLogs,
        { IsAll: true } => Sources.Any(s => s.Container?.HasPreviousLogs == true),
        _ => false,
    };

    /// <summary>A finished, previous instance cannot be followed.</summary>
    public bool CanFollow => !Previous;

    public bool ShowPrefixes => SelectedSource?.IsAll == true;

    public string PreviousInfo => SelectedSource switch
    {
        { Container: { HasPreviousLogs: true } c } => $"Restarted {c.RestartCount}×{(c.LastTerminationText is { } t ? $" · last exit: {t}" : "")}",
        { Container: { } } => "No previous instance (this container hasn't restarted)",
        { IsAll: true } when CanShowPrevious => Previous ? "Showing previous instances of restarted containers" : "Some containers have restarted",
        _ => "No container has restarted",
    };

    partial void OnSelectedSourceChanged(LogSource? value)
    {
        if (_initialising) return;
        if (Previous && !CanShowPrevious)
        {
            Previous = false; // triggers Restart
            return;
        }
        Restart();
    }

    partial void OnPreviousChanged(bool value)
    {
        if (!_initialising) Restart();
    }

    partial void OnTimestampsChanged(bool value) => Restart();
    partial void OnFollowChanged(bool value) => Restart();
    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void Restart()
    {
        if (_initialising) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _all.Clear();
        Lines.Clear();
        _ = StreamAsync(_cts.Token);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private Task CopyAll() => _cluster.Main.CopyAsync(string.Join('\n', Lines), "Logs");

    private async Task StreamAsync(CancellationToken ct)
    {
        var client = _cluster.Client;
        if (client is null || SelectedSource is not { } source) return;

        IsStreaming = true;
        Status = Previous ? "Previous instance" : null;
        var containers = source.Container is { } single
            ? [single]
            : Sources.Select(s => s.Container).OfType<ContainerInfo>().Where(c => Previous ? c.HasPreviousLogs : c.HasLogs).ToList();
        var prefixed = source.IsAll;
        var tail = prefixed ? 500 : 1000;
        var follow = Follow && !Previous;

        try
        {
            var results = await Task.WhenAll(containers.Select((c, i) =>
                StreamOneAsync(client, c.Name, prefixed ? c.Name : null, Palette[i % Palette.Length], follow, tail, ct)));
            var errors = results.Where(r => r is not null).ToList();
            Status = errors.Count > 0 ? string.Join(" · ", errors) : Previous ? "Previous instance · end of log" : "Stream ended";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped";
        }
        finally
        {
            IsStreaming = false;
        }
    }

    /// <summary>Streams one container into the buffer; returns a user-facing error, or null on success.</summary>
    private async Task<string?> StreamOneAsync(KubeClient client, string container, string? prefix, IBrush brush, bool follow, int tail, CancellationToken ct)
    {
        var batch = new List<LogLine>();
        var lastFlush = DateTime.UtcNow;
        try
        {
            await foreach (var line in client.StreamLogsAsync(_pod.Namespace!, _pod.Name, container, follow, tail, Previous, Timestamps, ct))
            {
                batch.Add(new LogLine(prefix, line, prefix is null ? null : brush));
                if (batch.Count >= 200 || DateTime.UtcNow - lastFlush > TimeSpan.FromMilliseconds(200))
                {
                    var flush = batch;
                    batch = [];
                    lastFlush = DateTime.UtcNow;
                    await Dispatcher.UIThread.InvokeAsync(() => Append(flush));
                }
            }
            return null;
        }
        catch (KubeApiException ex) when (Previous && ex.Status == HttpStatusCode.BadRequest)
        {
            return $"No previous instance of '{container}'";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return prefix is null ? Redactor.Redact(ex.Message) : $"{container}: {Redactor.Redact(ex.Message)}";
        }
        finally
        {
            if (batch.Count > 0) await Dispatcher.UIThread.InvokeAsync(() => Append(batch));
        }
    }

    private void Append(List<LogLine> lines)
    {
        _all.AddRange(lines);
        var overflow = _all.Count - MaxLines;
        if (overflow > 0) _all.RemoveRange(0, overflow);
        if (Filter.Length > 0 || overflow > 0)
        {
            ApplyFilter();
            return;
        }
        foreach (var l in lines) Lines.Add(l);
    }

    private void ApplyFilter()
    {
        Lines.Clear();
        foreach (var l in _all)
        {
            if (Filter.Length == 0 || l.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || (l.Prefix?.Contains(Filter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                Lines.Add(l);
            }
        }
    }

    protected override ValueTask OnClosedAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
