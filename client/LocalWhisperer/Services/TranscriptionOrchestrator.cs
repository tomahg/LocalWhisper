using LocalWhisperer.Models;

namespace LocalWhisperer.Services;

/// <summary>
/// Wires AudioCaptureService → WebSocketService.
/// Audio is streamed to the server while recording; a single final
/// transcription result is returned when recording stops.
/// </summary>
public class TranscriptionOrchestrator
{
    private readonly AudioCaptureService _audio;
    private readonly WebSocketService _ws;
    private readonly AppSettings _settings;

    public bool IsRecording { get; private set; }

    /// <summary>Raised immediately when recording starts (true) or stops (false).</summary>
    public event Action<bool>? RecordingStateChanged;

    /// <summary>Raised when the microphone device is lost mid-session.</summary>
    public event Action? MicrophoneDeviceLost;

    /// <summary>
    /// Raised on the thread that receives WebSocket messages.
    /// UI must marshal to DispatcherQueue.
    /// </summary>
    public event Action<string, bool>? TranscriptionUpdated;

    /// <summary>Raised with RMS level 0.0–1.0 for each audio buffer (UI thread not guaranteed).</summary>
    public event Action<float>? AudioLevelChanged;

    public TranscriptionOrchestrator(
        AudioCaptureService audio,
        WebSocketService ws,
        AppSettings settings)
    {
        _audio = audio;
        _ws = ws;
        _settings = settings;

        _audio.AudioDataAvailable += OnAudioData;
        _audio.AudioLevelChanged  += level => AudioLevelChanged?.Invoke(level);
        _audio.DeviceLost         += OnDeviceLost;
        _ws.TranscriptionReceived += OnTranscription;
    }

    public void StartRecording()
    {
        if (IsRecording) return;
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
        IsRecording = false;
        RecordingStateChanged?.Invoke(false);
        MicrophoneDeviceLost?.Invoke();
    }

    private async void OnAudioData(byte[] pcm)
    {
        if (!IsRecording) return;
        try { await _ws.SendAudioAsync(pcm); }
        catch { /* connection error handled via WebSocketService.ConnectionError */ }
    }

    private void OnTranscription(TranscriptionResult result)
    {
        TranscriptionUpdated?.Invoke(result.Text, result.IsFinal);
    }
}
