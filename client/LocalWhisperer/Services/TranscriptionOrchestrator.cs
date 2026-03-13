using LocalWhisperer.Models;

namespace LocalWhisperer.Services;

/// <summary>
/// Wires AudioCaptureService → WebSocketService → TextInjectionService.
/// Partials are shown in the overlay only; text is injected into the focused
/// input field only on final results to avoid clipboard race conditions.
/// </summary>
public class TranscriptionOrchestrator
{
    private readonly AudioCaptureService _audio;
    private readonly WebSocketService _ws;
    private readonly TextInjectionService _textInjection;
    private readonly AppSettings _settings;

    private bool _injectText = false;

    public bool IsRecording { get; private set; }

    /// <summary>Raised when the microphone device is lost mid-session.</summary>
    public event Action? MicrophoneDeviceLost;

    /// <summary>Raised immediately when recording starts or stops.</summary>
    public event Action<bool>? RecordingStateChanged;

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
        _audio.DeviceLost         += OnDeviceLost;
        _ws.TranscriptionReceived += OnTranscription;
    }

    /// <summary>Start recording and streaming to the server.</summary>
    /// <param name="injectText">
    /// When true, transcriptions are injected into the focused input field.
    /// Pass false for test/logging-only mode.
    /// </param>
    public void StartRecording(bool injectText = false)
    {
        if (IsRecording) return;
        _injectText = injectText;
        IsRecording = true;
        RecordingStateChanged?.Invoke(true);
        _audio.StartCapture(_settings.MicrophoneDeviceIndex);
    }

    public async Task StopRecordingAsync()
    {
        if (!IsRecording) return;
        IsRecording = false;
        RecordingStateChanged?.Invoke(false);
        _audio.StopCapture();
        try { await _ws.SendStopAsync(); }
        catch { /* connection error handled via WebSocketService.ConnectionError */ }
    }

    private void OnDeviceLost(Exception _)
    {
        // Device disappeared mid-session — stop cleanly without sending audio_stop
        // (server will reset on next connect anyway)
        IsRecording = false;
        RecordingStateChanged?.Invoke(false);
        MicrophoneDeviceLost?.Invoke();
    }

    private async void OnAudioData(byte[] pcm)
    {
        if (!IsRecording) return; // Guard against NAudio race condition delivering frames after StopCapture()
        try { await _ws.SendAudioAsync(pcm); }
        catch { /* connection error handled via WebSocketService.ConnectionError */ }
    }

    private void OnTranscription(TranscriptionResult result)
    {
        TranscriptionUpdated?.Invoke(result.Text, result.IsFinal);

        if (!_injectText) return;

        // Only inject on final — partial injection via clipboard causes race
        // conditions between restore timers when text > 50 chars.
        // Partials are shown in the overlay only.
        if (result.IsFinal)
            _textInjection.InjectText(result.Text);
    }
}
