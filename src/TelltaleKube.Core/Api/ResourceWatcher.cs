using TelltaleKube.Core.Resources;

namespace TelltaleKube.Core.Api;

/// <summary>
/// Keeps an up-to-date set of objects using list + watch (an informer without the cache machinery).
/// Reconnects with exponential backoff and relists when the watch expires (HTTP 410).
/// </summary>
public sealed class ResourceWatcher : IAsyncDisposable
{
    private readonly KubeClient _client;
    private readonly ResourceKind _kind;
    private readonly string? _namespace;
    private readonly Dictionary<string, KubeObject> _items = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private Task? _loop;

    public ResourceWatcher(KubeClient client, ResourceKind kind, string? ns)
    {
        _client = client;
        _kind = kind;
        _namespace = kind.Namespaced ? ns : null;
    }

    /// <summary>Raised (on a background thread) with a full snapshot whenever the set changes.</summary>
    public event Action<IReadOnlyList<KubeObject>>? Changed;

    /// <summary>Raised when the connection fails; the watcher keeps retrying.</summary>
    public event Action<Exception>? Faulted;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        string? rv = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (rv is null)
                {
                    var list = await _client.ListAsync(_kind, _namespace, ct: ct).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _items.Clear();
                        foreach (var o in list.Items) _items[Key(o)] = o;
                    }
                    rv = list.ResourceVersion;
                    Publish();
                }

                await foreach (var ev in _client.WatchAsync(_kind, _namespace, rv, ct).ConfigureAwait(false))
                {
                    backoff = TimeSpan.FromSeconds(1);
                    rv = ev.Resource.ResourceVersion ?? rv;
                    if (ev.Type == "BOOKMARK") continue;
                    lock (_lock)
                    {
                        if (ev.Type == "DELETED") _items.Remove(Key(ev.Resource));
                        else _items[Key(ev.Resource)] = ev.Resource;
                    }
                    Publish();
                }
                // Server closed the watch normally (timeoutSeconds): resume from the last resourceVersion.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (KubeApiException ex) when (ex.IsGone)
            {
                rv = null; // resourceVersion too old: relist
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
                if (ex is KubeApiException { IsForbidden: true } or Security.SecurityPolicyException) return;
                rv = null;
                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private static string Key(KubeObject o) => string.IsNullOrEmpty(o.Uid) ? $"{o.Namespace}/{o.Name}" : o.Uid;

    private void Publish()
    {
        List<KubeObject> snapshot;
        lock (_lock) snapshot = [.. _items.Values];
        Changed?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
    }
}
