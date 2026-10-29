using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TelltaleKube.Core.Api;

/// <summary>
/// An interactive exec stream using the Kubernetes channel protocol (v4/v5.channel.k8s.io):
/// each binary frame is prefixed with a channel byte: 0 stdin, 1 stdout, 2 stderr, 3 status, 4 resize, 255 close (v5).
/// </summary>
public sealed class ExecSession : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    internal ExecSession(WebSocket ws) => _ws = ws;

    /// <summary>Reads output until the remote side closes. <paramref name="onOutput"/> receives (isStderr, text).</summary>
    public async Task<ExecResult> ReadAllAsync(Action<bool, string> onOutput, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var decoderOut = Encoding.UTF8.GetDecoder();
        var decoderErr = Encoding.UTF8.GetDecoder();
        var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return new ExecResult(null, "connection closed");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (message.Length == 0) continue;
                var data = message.GetBuffer().AsSpan(0, (int)message.Length);
                var channel = data[0];
                var payload = data[1..];
                switch (channel)
                {
                    case 1:
                    case 2:
                        var decoder = channel == 1 ? decoderOut : decoderErr;
                        var chars = new char[decoder.GetCharCount(payload, flush: false)];
                        decoder.GetChars(payload, chars, flush: false);
                        if (chars.Length > 0) onOutput(channel == 2, new string(chars));
                        break;
                    case 3:
                        return ParseStatus(Encoding.UTF8.GetString(payload));
                }
            }
            return new ExecResult(null, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task SendInputAsync(string text, CancellationToken ct) => SendAsync(0, Encoding.UTF8.GetBytes(text), ct);

    private async Task SendAsync(byte channel, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[payload.Length + 1];
        frame[0] = channel;
        payload.CopyTo(frame, 1);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    internal static ExecResult ParseStatus(string json)
    {
        var node = JsonNode.Parse(json);
        if (node?["status"]?.ToString() == "Success") return new ExecResult(0, null);
        var causes = node?["details"]?["causes"]?.AsArray();
        var exit = causes?.FirstOrDefault(c => c?["reason"]?.ToString() == "ExitCode")?["message"]?.ToString();
        return new ExecResult(int.TryParse(exit, out var code) ? code : null, node?["message"]?.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }
        _ws.Dispose();
        _sendLock.Dispose();
    }
}

public sealed record ExecResult(int? ExitCode, string? Message);
