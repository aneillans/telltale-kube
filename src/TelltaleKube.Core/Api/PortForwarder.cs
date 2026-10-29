using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace TelltaleKube.Core.Api;

/// <summary>
/// Forwards a local loopback port to a pod port (like <c>kubectl port-forward</c>). Binds to 127.0.0.1 only,
/// so forwarded services are never exposed to the local network. Desktop only.
/// </summary>
public sealed class PortForwarder : IAsyncDisposable
{
    private readonly KubeClient _client;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private PortForwarder(KubeClient client, string ns, string pod, int remotePort, TcpListener listener)
    {
        _client = client;
        Namespace = ns;
        Pod = pod;
        RemotePort = remotePort;
        _listener = listener;
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public string Namespace { get; }
    public string Pod { get; }
    public int RemotePort { get; }
    public int LocalPort { get; }
    public event Action<string>? Error;

    /// <param name="localPort">0 picks a port: the same number as the pod port when it is free and unprivileged
    /// (8080 → 127.0.0.1:8080, easy to remember), otherwise any free port.</param>
    public static PortForwarder Start(KubeClient client, string ns, string pod, int remotePort, int localPort = 0)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            throw new PlatformNotSupportedException("Port forwarding is only available on desktop.");
        }
        var listener = localPort == 0 && remotePort >= 1024 ? TryListen(remotePort) : null;
        if (listener is null)
        {
            listener = new TcpListener(IPAddress.Loopback, localPort);
            listener.Start();
        }
        return new PortForwarder(client, ns, pod, remotePort, listener);
    }

    public static string GuessScheme(int port, string? portName) =>
        port is 443 or 8443 || (portName?.Contains("https", StringComparison.OrdinalIgnoreCase) ?? false) ? "https" : "http";

    private static TcpListener? TryListen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            listener.Dispose();
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleAsync(tcp);
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            using (tcp)
            using (var ws = await _client.OpenPortForwardAsync(Namespace, Pod, RemotePort, linked.Token).ConfigureAwait(false))
            {
                var net = tcp.GetStream();
                var up = PumpUpAsync(net, ws, linked.Token);
                var down = PumpDownAsync(ws, net, linked.Token);
                await Task.WhenAny(up, down).ConfigureAwait(false);
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or KubeApiException or OperationCanceledException or Security.SecurityPolicyException)
        {
            if (!_cts.IsCancellationRequested && ex is not OperationCanceledException) Error?.Invoke(ex.Message);
        }
    }

    private static async Task PumpUpAsync(NetworkStream net, WebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            int read;
            while ((read = await net.ReadAsync(buffer.AsMemory(1, buffer.Length - 1), ct).ConfigureAwait(false)) > 0)
            {
                buffer[0] = 0; // data channel
                await ws.SendAsync(buffer.AsMemory(0, read + 1), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task PumpDownAsync(WebSocket ws, NetworkStream net, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        // The first frame on each channel carries the 2-byte port number and no payload data.
        var seenPortHeader = new bool[2];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close || result.Count == 0) return;
                var channel = buffer[0];
                var offset = 1;
                if (channel < 2 && !seenPortHeader[channel])
                {
                    seenPortHeader[channel] = true;
                    offset = 3;
                }
                if (result.Count <= offset) continue;
                if (channel == 0)
                {
                    await net.WriteAsync(buffer.AsMemory(offset, result.Count - offset), ct).ConfigureAwait(false);
                }
                else if (channel == 1)
                {
                    Error?.Invoke(Encoding.UTF8.GetString(buffer, offset, result.Count - offset));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _listener.Dispose();
        _cts.Dispose();
    }
}
