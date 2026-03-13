using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LocalWhisperer.Models;

namespace LocalWhisperer.Services;

/// <summary>
/// Manages the WebSocket connection to the transcription server.
/// Sends raw PCM audio as binary frames and receives JSON transcription results.
/// </summary>
public class WebSocketService : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string? _serverUrl;
    // WebSocket sends must be serialized — concurrent sends throw InvalidOperationException
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<TranscriptionResult>? TranscriptionReceived;
    public event Action<Exception>? ConnectionError;
    public event Action? ConnectionRestored;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public bool AutoReconnect { get; set; } = true;

    public async Task ConnectAsync(string url)
    {
        _serverUrl = url;
        await DisconnectAsync();
        await ConnectCoreAsync(url);
    }

    private async Task ConnectCoreAsync(string url)
    {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        // Respond to server pings to keep the connection alive during silence
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await _ws.ConnectAsync(new Uri(url), _cts.Token);

        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendAudioAsync(byte[] pcmData)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync(_cts!.Token);
        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(pcmData),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                _cts.Token);
        }
        finally { _sendLock.Release(); }
    }

    public async Task SendStopAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = JsonSerializer.SerializeToUtf8Bytes(new { type = "audio_stop" });
        await _sendLock.WaitAsync(_cts!.Token);
        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(msg),
                WebSocketMessageType.Text,
                endOfMessage: true,
                _cts.Token);
        }
        finally { _sendLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        if (_ws is null) return;

        _cts?.Cancel();

        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { /* ignore close errors */ }
        }

        _ws.Dispose();
        _ws = null;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();

        try
        {
            while (_ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.Clear();

                    var transcription = JsonSerializer.Deserialize<TranscriptionResult>(json);
                    if (transcription is not null)
                        TranscriptionReceived?.Invoke(transcription);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(ex);
            if (AutoReconnect && _serverUrl is not null)
                _ = Task.Run(ReconnectLoopAsync);
        }
    }

    private async Task ReconnectLoopAsync()
    {
        var delay = 2;
        while (_serverUrl is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            try
            {
                await ConnectCoreAsync(_serverUrl);
                ConnectionRestored?.Invoke();
                return;
            }
            catch { }
            delay = Math.Min(delay * 2, 30);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
        _sendLock.Dispose();
    }
}
