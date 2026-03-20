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
    private readonly ServerApiService _api;
    private readonly AppSettings _settings;

    private System.Timers.Timer? _silenceTimer;
    private int _pendingSilenceStops;

    public bool IsRecording { get; private set; }
    public bool IsTranscribingFile { get; private set; }

    /// <summary>Raised immediately when recording starts (true) or stops (false).</summary>
    public event Action<bool>? RecordingStateChanged;

    /// <summary>Raised when the microphone device is lost mid-session.</summary>
    public event Action? MicrophoneDeviceLost;

    /// <summary>
    /// Raised on the thread that receives WebSocket messages.
    /// Parameters: result, source.
    /// UI must marshal to DispatcherQueue.
    /// </summary>
    public event Action<TranscriptionResult, TranscriptionSource>? TranscriptionUpdated;

    /// <summary>Raised with RMS level 0.0–1.0 for each audio buffer (UI thread not guaranteed).</summary>
    public event Action<float>? AudioLevelChanged;

    public TranscriptionOrchestrator(
        AudioCaptureService audio,
        WebSocketService ws,
        ServerApiService api,
        AppSettings settings)
    {
        _audio = audio;
        _ws = ws;
        _api = api;
        _settings = settings;

        _audio.AudioDataAvailable += OnAudioData;
        _audio.AudioLevelChanged  += OnAudioLevel;
        _audio.DeviceLost         += OnDeviceLost;
        _ws.TranscriptionReceived += OnTranscription;
        _ws.ConnectionRestored    += async () => await SyncVadSettingsAsync();
    }

    public void StartRecording()
    {
        if (IsRecording) return;
        IsRecording = true;
        _pendingSilenceStops = 0;
        ResetSilenceTimer();
        RecordingStateChanged?.Invoke(true);
        _audio.StartCapture(_settings.MicrophoneDeviceIndex, _settings.AudioSource);
    }

    public async Task StopRecordingAsync()
    {
        if (!IsRecording) return;
        IsRecording = false;
        ResetSilenceTimer();
        RecordingStateChanged?.Invoke(false);
        _audio.StopCapture();
        try { await _ws.SendStopAsync(); }
        catch { /* connection error handled via WebSocketService.ConnectionError */ }
    }

    private void OnDeviceLost(Exception _)
    {
        IsRecording = false;
        ResetSilenceTimer();
        RecordingStateChanged?.Invoke(false);
        MicrophoneDeviceLost?.Invoke();
    }

    private void OnAudioLevel(float level)
    {
        AudioLevelChanged?.Invoke(level);

        if (!IsRecording || !_settings.AutoSendOnSilence) return;

        if (level < (float)_settings.SilenceLevelThreshold)
        {
            // Below threshold — start one-shot timer if not running
            if (_silenceTimer is null)
            {
                _silenceTimer = new System.Timers.Timer(_settings.SilenceThresholdSeconds * 1000);
                _silenceTimer.AutoReset = false;
                _silenceTimer.Elapsed += OnSilenceTimerElapsed;
                _silenceTimer.Start();
            }
        }
        else
        {
            // Above threshold — reset timer
            ResetSilenceTimer();
        }
    }

    private async void OnSilenceTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!IsRecording) return;
        Interlocked.Increment(ref _pendingSilenceStops);
        ResetSilenceTimer();
        try { await _ws.SendStopAsync(); }
        catch { /* connection error handled via WebSocketService.ConnectionError */ }
    }

    private void ResetSilenceTimer()
    {
        if (_silenceTimer is not null)
        {
            _silenceTimer.Stop();
            _silenceTimer.Elapsed -= OnSilenceTimerElapsed;
            _silenceTimer.Dispose();
            _silenceTimer = null;
        }
    }

    private async void OnAudioData(byte[] pcm)
    {
        if (!IsRecording) return;
        try { await _ws.SendAudioAsync(pcm); }
        catch { /* connection error handled via WebSocketService.ConnectionError */ }
    }

    private void OnTranscription(TranscriptionResult result)
    {
        if (IsTranscribingFile) return; // suppress mic results during file transcription

        if (Interlocked.CompareExchange(ref _pendingSilenceStops, 0, 0) > 0)
        {
            Interlocked.Decrement(ref _pendingSilenceStops);
            TranscriptionUpdated?.Invoke(result, TranscriptionSource.AutoSilence);
        }
        else
        {
            TranscriptionUpdated?.Invoke(result, TranscriptionSource.Microphone);
        }
    }

    /// <summary>
    /// Pushes the current VAD settings to the server. Called automatically on connect/reconnect
    /// and can be called manually after settings change.
    /// </summary>
    public async Task SyncVadSettingsAsync()
    {
        if (!_ws.IsConnected) return;
        try
        {
            await _api.SetVadConfigAsync(_settings.ServerUrl, _settings.VadEnabled, _settings.VadThreshold);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VAD sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Records ambient noise for 3 seconds and asks the server to recommend a VAD threshold.
    /// Must not be called while a recording session is active.
    /// </summary>
    public async Task<double> CalibrateVadAsync(CancellationToken ct = default)
    {
        if (IsRecording)
            throw new InvalidOperationException("Kan ikke kalibrere under opptak.");

        var buffer = new List<byte>();
        void OnCalibrationAudio(byte[] pcm) { lock (buffer) buffer.AddRange(pcm); }

        _audio.AudioDataAvailable += OnCalibrationAudio;
        _audio.StartCapture(_settings.MicrophoneDeviceIndex, _settings.AudioSource);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        finally
        {
            _audio.StopCapture();
            _audio.AudioDataAvailable -= OnCalibrationAudio;
        }

        byte[] pcm;
        lock (buffer) pcm = [.. buffer];

        return await _api.CalibrateVadAsync(_settings.ServerUrl, pcm, ct);
    }

    /// <summary>
    /// Records ambient noise for 3 seconds and returns the recommended silence level threshold.
    /// Computed client-side from RMS, using the same 36× amplification as the audio level bar.
    /// </summary>
    public async Task<double> CalibrateSilenceLevelAsync(CancellationToken ct = default)
    {
        if (IsRecording)
            throw new InvalidOperationException("Kan ikke kalibrere under opptak.");

        var buffer = new List<byte>();
        void OnAudio(byte[] pcm) { lock (buffer) buffer.AddRange(pcm); }

        _audio.AudioDataAvailable += OnAudio;
        _audio.StartCapture(_settings.MicrophoneDeviceIndex, _settings.AudioSource);
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        finally
        {
            _audio.StopCapture();
            _audio.AudioDataAvailable -= OnAudio;
        }

        byte[] pcm;
        lock (buffer) pcm = [.. buffer];

        if (pcm.Length < 2) return 0.08;

        // Compute RMS — same formula as AudioCaptureService.FireLevelEvent
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
        double sumSq = 0;
        foreach (var s in samples) sumSq += (double)s * s;
        double rawRms = Math.Sqrt(sumSq / samples.Length) / 32768.0;
        double ambientLevel = rawRms * 36.0;

        // Add 30% headroom so speech easily clears the gate, then round to 2 decimals
        return Math.Clamp(Math.Round(ambientLevel * 1.3 + 0.02, 2), 0.00, 0.30);
    }

    public async Task TranscribeFileAsync(string filePath)
    {
        if (IsTranscribingFile) return;
        IsTranscribingFile = true;
        try
        {
            var result = await _api.TranscribeFileAsync(_settings.ServerUrl, filePath);
            IsTranscribingFile = false;
            TranscriptionUpdated?.Invoke(result, TranscriptionSource.File);
        }
        catch
        {
            IsTranscribingFile = false;
            throw;
        }
    }
}
