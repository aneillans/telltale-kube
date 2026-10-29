using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.App.Services;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.ViewModels;

/// <summary>A pod port: declared in a container spec, or an extra port the user forwarded by number.</summary>
public sealed partial class PodPortRow(int port, string? name, string protocol, string? container) : ViewModelBase
{
    public int Port { get; } = port;
    public string? Name { get; } = name;
    public string Protocol { get; } = protocol;
    public string? Container { get; } = container;

    /// <summary>"8080/TCP · http · app" (container omitted for single-container pods).</summary>
    public string Label => string.Join(" · ", new[] { $"{Port}/{Protocol}", Name, Container }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Kubernetes port-forwarding is TCP only.</summary>
    public bool IsTcp => Protocol == "TCP";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsForwarded), nameof(IsNotForwarded))]
    public partial PortForwardItem? Forward { get; set; }

    public bool IsForwarded => Forward is not null;
    public bool IsNotForwarded => Forward is null;
}

public sealed partial class ResourceDetailViewModel
{
    /// <summary>Pods: declared container ports and any active forwards, each with Forward or link / Copy / Stop.</summary>
    public ObservableCollection<PodPortRow> PodPorts { get; } = [];

    public bool HasPodPorts => PodPorts.Count > 0;

    /// <summary>Rebuilds the port rows and attaches active forwards. Called on rebuild and whenever forwards change.</summary>
    public void SyncPortForwards()
    {
        if (!IsPod) return;
        var multiContainer = (Object["spec.containers"] as JsonArray)?.Count > 1;
        var rows = (Object["spec.containers"] as JsonArray ?? [])
            .SelectMany(c => (c?["ports"] as JsonArray ?? []).Select(p => (Container: c?["name"]?.ToString(), Port: p)))
            .Select(x => new PodPortRow(
                int.TryParse(x.Port?["containerPort"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
                x.Port?["name"]?.ToString(),
                x.Port?["protocol"]?.ToString() ?? "TCP",
                multiContainer ? x.Container : null))
            .Where(r => r.Port > 0)
            .GroupBy(r => (r.Port, r.Protocol))
            .Select(g => g.First())
            .ToList();

        foreach (var fwd in _cluster.PortForwards.Where(f => f.Namespace == Object.Namespace && f.Pod == Object.Name))
        {
            var row = rows.FirstOrDefault(r => r.Port == fwd.RemotePort && r.IsTcp);
            if (row is null)
            {
                row = new PodPortRow(fwd.RemotePort, null, "TCP", null);
                rows.Add(row);
            }
            row.Forward = fwd;
        }

        // Same ports as shown: update rows in place (no flicker, the clicked row stays the same object).
        if (rows.Select(r => r.Label).SequenceEqual(PodPorts.Select(r => r.Label)))
        {
            for (var i = 0; i < rows.Count; i++) PodPorts[i].Forward = rows[i].Forward;
            return;
        }
        PodPorts.Clear();
        foreach (var r in rows) PodPorts.Add(r);
        OnPropertyChanged(nameof(HasPodPorts));
    }

    [RelayCommand]
    private void ForwardPort(PodPortRow? row)
    {
        if (row is null || !CanPortForward || !row.IsTcp) return;
        try
        {
            var item = _cluster.StartPortForward(Object.Namespace!, Object.Name, row.Port, row.Name);
            Main.Toast($"Forwarding {item.Local} → {Object.Name}:{row.Port}");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or PlatformNotSupportedException or InvalidOperationException)
        {
            Main.Toast(Redactor.Redact(ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private Task StopForward(PodPortRow? row) => row?.Forward is { } f ? _cluster.StopPortForward(f) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenForward(PodPortRow? row) => row?.Forward is { } f ? _cluster.OpenPortForward(f) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyForwardUrl(PodPortRow? row) => row?.Forward is { } f ? _cluster.CopyPortForwardUrl(f) : Task.CompletedTask;

    /// <summary>Port forwarding is desktop-only; on phones the port list is informational.</summary>
    public bool CanForwardPorts => CanPortForward && Platform.Current.IsDesktop;
}
