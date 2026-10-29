using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleKube.Core.Api;
using TelltaleKube.Core.Resources;
using TelltaleKube.Core.Security;

namespace TelltaleKube.App.ViewModels;

/// <summary>
/// A lightweight line-oriented shell into a container (no terminal emulator: keeps the app small and works
/// well with on-screen keyboards). Commands are sent on Enter; output is streamed back.
/// </summary>
public sealed partial class ShellViewModel : PageViewModel
{
    private const int MaxChars = 256 * 1024;
    private readonly ClusterViewModel _cluster;
    private readonly KubeObject _pod;
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = [];
    private int _historyIndex;
    private ExecSession? _session;
    private CancellationTokenSource? _cts;

    public ShellViewModel(ClusterViewModel cluster, KubeObject pod, string? container = null)
    {
        _cluster = cluster;
        _pod = pod;
        // Running main container first, then running sidecars (native sidecars included).
        foreach (var c in PodContainers.Of(pod).Where(c => c.CanExec).OrderBy(c => c.Role == ContainerRole.Main ? 0 : 1)) Containers.Add(c.Name);
        SelectedContainer = Containers.FirstOrDefault(c => c == container) ?? Containers.FirstOrDefault();
    }

    public override string Title => $"Shell · {_pod.Name}";
    public ObservableCollection<string> Containers { get; } = [];
    public bool HasMultipleContainers => Containers.Count > 1;

    [ObservableProperty]
    public partial string? SelectedContainer { get; set; }

    [ObservableProperty]
    public partial string Output { get; set; } = "";

    [ObservableProperty]
    public partial string Input { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsConnected { get; set; }

    partial void OnSelectedContainerChanged(string? value) => _ = ConnectAsync();

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await DisconnectAsync();
        var client = _cluster.Client;
        if (client is null) return;
        _cts = new CancellationTokenSource();
        Append($"Connecting to {_pod.Name}/{SelectedContainer}…\n");
        try
        {
            // Prefer bash, fall back to sh; stderr is merged so prompts and errors are visible.
            _session = await client.ExecAsync(_pod.Namespace!, _pod.Name, SelectedContainer,
                ["/bin/sh", "-c", "if command -v bash >/dev/null 2>&1; then exec bash -i 2>&1; else exec sh -i 2>&1; fi"], tty: false, _cts.Token);
            IsConnected = true;
            var session = _session;
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await session.ReadAllAsync((_, text) => Dispatcher.UIThread.Post(() => Append(text)), token);
                    Dispatcher.UIThread.Post(() => Append($"\n[session ended{(result.ExitCode is { } c ? $", exit code {c}" : "")}{(result.Message is { } m ? $": {m}" : "")}]\n"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Dispatcher.UIThread.Post(() => Append($"\n[{Redactor.Redact(ex.Message)}]\n"));
                }
                finally
                {
                    Dispatcher.UIThread.Post(() => IsConnected = false);
                }
            }, token);
        }
        catch (Exception ex)
        {
            Append($"[{Redactor.Redact(ex.Message)}]\n");
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task Send()
    {
        if (_session is null) return;
        var line = Input;
        Input = "";
        if (line.Length > 0)
        {
            _history.Add(line);
            _historyIndex = _history.Count;
        }
        Append(line + "\n");
        try
        {
            await _session.SendInputAsync(line + "\n", _cts?.Token ?? default);
        }
        catch (Exception ex)
        {
            Append($"[{Redactor.Redact(ex.Message)}]\n");
        }
    }

    [RelayCommand]
    private void HistoryUp()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        Input = _history[_historyIndex];
    }

    [RelayCommand]
    private void HistoryDown()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        Input = _historyIndex < _history.Count ? _history[_historyIndex] : "";
    }

    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        Output = "";
    }

    private void Append(string text)
    {
        _buffer.Append(AnsiRegex().Replace(text, ""));
        if (_buffer.Length > MaxChars) _buffer.Remove(0, _buffer.Length - MaxChars);
        Output = _buffer.ToString();
    }

    private async Task DisconnectAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_session is not null) await _session.DisposeAsync();
        _session = null;
        _cts?.Dispose();
        _cts = null;
        IsConnected = false;
    }

    protected override ValueTask OnClosedAsync() => new(DisconnectAsync());

    [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07]*\x07|[@-Z\\-_])")]
    private static partial Regex AnsiRegex();
}
