using LocalWhisperer.Models;

namespace LocalWhisperer.Services;

/// <summary>
/// Wires AudioCaptureService → WebSocketService → TextInjectionService.
/// Manages partial-text replacement: when a new partial arrives, the previous
/// partial is erased with backspaces before the new text is injected.
/// </summary>
public class TranscriptionOrchestrator
{
    private readonly AudioCaptureService _audio;
    private readonly WebSocketService _ws;
    private readonly TextInjectionService _textInjection;
    private readonly AppSettings _settings;

    private string _lastPartial = "";
    private bool _injectText = false;

    /// <summary>
    /// Raised on the thread that receives WebSocket messages.
    /// UI must marshal to DispatcherQueue.
    /// </summary>
    public event Action<string, bool>? TranscriptionUpdated;

    public TranscriptionOrchestrator(
        AudioCaptureService audio,
        WebSocketService ws,
        TextInjectionService textInjection,
        AppSettings settings)
    {
        _audio = audio;
        _ws = ws;
        _textInjection = textInjection;
        _settings = settings;

        _audio.AudioDataAvailable += OnAudioData;
        _ws.TranscriptionReceived += OnTranscription;
    }

    /// <summary>Start recording and streaming to the server.</summary>
    /// <param name="injectText">
    /// When true, transcriptions are injected into the focused input field.
    /// Pass false for test/logging-only mode.
    /// </param>
    public void StartRecording(bool injectText = false)
    {
        _injectText = injectText;
        _lastPartial = "";
        _audio.StartCapture(_settings.MicrophoneDeviceIndex);
    }

    /// <summary>Stop recording and wait for the final transcription result.</summary>
    public async Task StopRecordingAsync()
    {
        _audio.StopCapture();
        await _ws.SendStopAsync();
    }

    private async void OnAudioData(byte[] pcm)
    {
        try { await _ws.SendAudioAsync(pcm); }
        catch { /* connection error handled via WebSocketService.ConnectionError */ }
    }

    private void OnTranscription(TranscriptionResult result)
    {
        TranscriptionUpdated?.Invoke(result.Text, result.IsFinal);

        if (!_injectText) return;

        // Erase the previous partial, then write the new text.
        _textInjection.SendBackspaces(_lastPartial.Length);
        _textInjection.InjectText(result.Text);
        _lastPartial = result.IsFinal ? "" : result.Text;
    }
}
