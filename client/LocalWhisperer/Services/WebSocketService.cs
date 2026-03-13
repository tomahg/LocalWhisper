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

    public event Action<TranscriptionResult>? TranscriptionReceived;
    public event Action<Exception>? ConnectionError;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string url)
    {
        await DisconnectAsync();

        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(url), _cts.Token);

        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendAudioAsync(byte[] pcmData)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _ws.SendAsync(
            new ArraySegment<byte>(pcmData),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            _cts!.Token);
    }

    public async Task SendStopAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;

        var msg = JsonSerializer.SerializeToUtf8Bytes(new { type = "audio_stop" });
        await _ws.SendAsync(
            new ArraySegment<byte>(msg),
            WebSocketMessageType.Text,
            endOfMessage: true,
            _cts!.Token);
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
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
    }
}
