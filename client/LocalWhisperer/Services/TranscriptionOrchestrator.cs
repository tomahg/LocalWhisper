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

    private int _sessionInjectedLength = 0;  // total chars injected in this session
    private bool _injectText = false;

    public bool IsRecording { get; private set; }

    /// <summary>Raised when the microphone device is lost mid-session.</summary>
    public event Action? MicrophoneDeviceLost;

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
        _sessionInjectedLength = 0;
        IsRecording = true;
        _audio.StartCapture(_settings.MicrophoneDeviceIndex);
    }

    public async Task StopRecordingAsync()
    {
        if (!IsRecording) return;
        IsRecording = false;
        _audio.StopCapture();
        await _ws.SendStopAsync();
    }

    private void OnDeviceLost(Exception _)
    {
        // Device disappeared mid-session — stop cleanly without sending audio_stop
        // (server will reset on next connect anyway)
        IsRecording = false;
        _sessionInjectedLength = 0;
        MicrophoneDeviceLost?.Invoke();
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

        // Replace the cumulative session text injected so far with the new text.
        // Because the server now sends the full running transcript in every
        // partial, we erase what we injected and re-inject the updated full text.
        _textInjection.SendBackspaces(_sessionInjectedLength);
        _textInjection.InjectText(result.Text);
        _sessionInjectedLength = result.IsFinal ? 0 : result.Text.Length;
    }
}
