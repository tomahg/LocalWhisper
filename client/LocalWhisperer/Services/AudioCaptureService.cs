using LocalWhisperer.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LocalWhisperer.Services;

/// <summary>
/// Captures audio (microphone, system loopback, or both) as 16kHz 16-bit mono PCM
/// and raises AudioDataAvailable with raw byte chunks for the transcription server.
///
/// Threading rules:
///  - WaveInEvent (microphone) uses Win32 waveIn — safe on any thread.
///  - WasapiLoopbackCapture uses COM/WASAPI:
///      * Must NOT be constructed, started, stopped, or disposed on the WinUI 3 UI
///        thread (ASTA), as doing so can deadlock the COM message pump.
///      * A dedicated long-lived MTA background thread (_loopbackThread) owns the
///        entire WASAPI lifecycle: create → start → wait for stop → stop → dispose.
///      * StopCapture() never makes any WASAPI calls; it only signals the thread via
///        a CancellationTokenSource. This eliminates all COM/lock deadlock risk.
///      * WASAPI COM calls must NOT be made while holding _loopbackLock.
/// </summary>
public class AudioCaptureService : IDisposable
{
    private const int SampleRate         = 16000;
    private const int BitsPerSample      = 16;
    private const int Channels           = 1;
    private const int BufferMilliseconds = 100;

    private static readonly WaveFormat TargetFormat =
        new WaveFormat(SampleRate, BitsPerSample, Channels);

    // 100 ms of 16kHz 16-bit mono = 3200 bytes
    private const int ChunkSize =
        SampleRate * BitsPerSample / 8 * Channels * BufferMilliseconds / 1000;

    private WaveInEvent?           _waveIn;
    private WasapiLoopbackCapture? _loopbackCapture;
    private BufferedWaveProvider?  _loopbackRawBuffer;
    private IWaveProvider?         _loopbackProcessed;
    private int                    _loopbackInputBytesPerSec; // stored inside _loopbackLock to avoid COM calls on wrong thread
    private readonly List<byte>    _loopbackAccumulator = [];

    // Plain object lock — never held while making WASAPI/COM calls.
    private readonly object _loopbackLock = new();

    // Incremented on every StopCapture() so a background InitLoopback() that races
    // with stop can detect it should abort.
    private int _captureGeneration = 0;

    // Per-session CancellationTokenSource used to signal the loopback thread to stop.
    // Accessed on the calling thread under _loopbackLock.
    private CancellationTokenSource? _loopbackCts;

    private AudioSourceMode _sourceMode;

    public event Action<byte[]>?    AudioDataAvailable;
    /// <summary>Raised when the capture device is lost mid-session.</summary>
    public event Action<Exception>? DeviceLost;
    /// <summary>Raised with RMS level 0.0–1.0 for each audio buffer.</summary>
    public event Action<float>?     AudioLevelChanged;

    public void StartCapture(int deviceIndex = 0,
                             AudioSourceMode sourceMode = AudioSourceMode.Microphone)
    {
        StopCapture();             // increments _captureGeneration, clears old state
        _sourceMode = sourceMode;

        if (sourceMode != AudioSourceMode.SystemAudio)
            InitMic(deviceIndex);  // Win32 waveIn — safe on any thread

        if (sourceMode != AudioSourceMode.Microphone)
        {
            // Snapshot generation so the loopback thread can detect a stale start.
            int gen = _captureGeneration;
            var cts = new CancellationTokenSource();

            lock (_loopbackLock)
                _loopbackCts = cts;

            // A dedicated MTA thread owns the full WASAPI lifecycle.
            // We must NOT use Task.Run here — pool threads can be reclaimed and may
            // have COM apartment state issues on WinUI 3.
            var thread = new Thread(() => LoopbackThreadBody(gen, cts))
            {
                IsBackground = true,
                Name         = "WasapiLoopback",
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
    }

    // ── Microphone (Win32 waveIn) ──────────────────────────────────────────

    private void InitMic(int deviceIndex)
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat         = TargetFormat,
            BufferMilliseconds = BufferMilliseconds,
            DeviceNumber       = deviceIndex,
        };
        _waveIn.DataAvailable    += OnMicDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        byte[] output;
        if (_sourceMode == AudioSourceMode.Both)
        {
            byte[] loopback;
            lock (_loopbackLock)
            {
                DrainToAccumulator();
                loopback = TakeFromAccumulator(e.BytesRecorded);
            }
            output = Mix(e.Buffer, loopback, e.BytesRecorded);
        }
        else
        {
            output = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, output, e.BytesRecorded);
        }

        AudioDataAvailable?.Invoke(output);
        FireLevelEvent(output);
    }

    // ── Loopback (WASAPI) — dedicated MTA thread owns entire lifecycle ─────

    /// <summary>
    /// Runs on a dedicated MTA background thread for the entire duration of a
    /// loopback capture session. All WASAPI COM operations happen here.
    /// Stop is signalled via <paramref name="cts"/>; no WASAPI calls are made
    /// by any other thread.
    /// </summary>
    private void LoopbackThreadBody(int expectedGen, CancellationTokenSource cts)
    {
        WasapiLoopbackCapture? capture = null;
        try
        {
            // All COM/WASAPI object creation here — on this dedicated MTA thread.
            capture = new WasapiLoopbackCapture();
            var rawBuffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration          = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true,
            };

            // WASAPI loopback is always IEEE float stereo at the device sample rate (e.g. 48 kHz).
            // Pipeline: float stereo → mono → resample to 16 kHz → 16-bit PCM
            ISampleProvider pipeline = new WaveToSampleProvider(rawBuffer);
            if (pipeline.WaveFormat.Channels == 2)
                pipeline = new StereoToMonoSampleProvider(pipeline);
            if (pipeline.WaveFormat.SampleRate != SampleRate)
                pipeline = new WdlResamplingSampleProvider(pipeline, SampleRate);
            var processed = new SampleToWaveProvider16(pipeline);

            // Check generation and publish fields — WASAPI calls kept OUTSIDE the lock.
            bool proceed;
            lock (_loopbackLock)
            {
                proceed = _captureGeneration == expectedGen;
                if (proceed)
                {
                    _loopbackCapture          = capture;
                    _loopbackRawBuffer        = rawBuffer;
                    _loopbackProcessed        = processed;
                    _loopbackInputBytesPerSec = capture.WaveFormat.AverageBytesPerSecond;
                }
            }

            if (!proceed)
            {
                // StopCapture (or a new StartCapture) ran while we were initialising.
                capture.Dispose();
                return;
            }

            capture.DataAvailable    += OnLoopbackRawData;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording(); // starts NAudio's internal capture thread

            // Block this thread until stop is signalled by StopCapture().
            cts.Token.WaitHandle.WaitOne();

            // Null out shared fields (StopCapture may have already done this).
            lock (_loopbackLock)
            {
                if (ReferenceEquals(_loopbackCapture, capture))
                {
                    _loopbackCapture          = null;
                    _loopbackRawBuffer        = null;
                    _loopbackProcessed        = null;
                    _loopbackInputBytesPerSec = 0;
                }
            }

            // All WASAPI teardown on this same thread — safe, no lock held.
            try { capture.DataAvailable    -= OnLoopbackRawData;  } catch { }
            try { capture.RecordingStopped -= OnRecordingStopped; } catch { }
            try { capture.StopRecording(); }                        catch { }
            try { capture.Dispose(); }                              catch { }
        }
        catch (Exception ex)
        {
            lock (_loopbackLock)
            {
                if (ReferenceEquals(_loopbackCapture, capture))
                {
                    _loopbackCapture          = null;
                    _loopbackRawBuffer        = null;
                    _loopbackProcessed        = null;
                    _loopbackInputBytesPerSec = 0;
                }
            }
            try { capture?.Dispose(); } catch { }
            DeviceLost?.Invoke(ex);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void OnLoopbackRawData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        // Snapshot — StopCapture may null _loopbackRawBuffer between our check and use.
        var rawBuffer = _loopbackRawBuffer;
        if (rawBuffer is null) return;

        rawBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        if (_sourceMode == AudioSourceMode.SystemAudio)
        {
            List<byte[]> chunks;
            lock (_loopbackLock)
            {
                DrainToAccumulator();
                chunks = CollectChunks();
            }
            foreach (var chunk in chunks)
            {
                AudioDataAvailable?.Invoke(chunk);
                FireLevelEvent(chunk);
            }
        }
        else // Both — drain only; mixing happens in OnMicDataAvailable
        {
            lock (_loopbackLock)
                DrainToAccumulator();
        }
    }

    // ── Accumulator helpers (must be called inside _loopbackLock) ──────────

    private void DrainToAccumulator()
    {
        if (_loopbackProcessed is null || _loopbackRawBuffer is null || _loopbackInputBytesPerSec == 0) return;

        // BufferedWaveProvider.Read() always returns data (pads with silence when empty)
        // and never returns 0, so a plain "while (read > 0)" loop is infinite.
        // Bound the drain to bytes proportional to what is actually buffered.
        int buffered = _loopbackRawBuffer.BufferedBytes;
        if (buffered == 0) return;

        int maxOutput = (int)((long)buffered * TargetFormat.AverageBytesPerSecond / _loopbackInputBytesPerSec);
        maxOutput = Math.Max(2, (maxOutput / 2) * 2); // ≥ 1 sample, 16-bit aligned

        var buf = new byte[Math.Min(maxOutput, ChunkSize)];
        int totalRead = 0, read;
        while (totalRead < maxOutput && (read = _loopbackProcessed.Read(buf, 0, buf.Length)) > 0)
        {
            _loopbackAccumulator.AddRange(new ReadOnlySpan<byte>(buf, 0, read));
            totalRead += read;
        }
    }

    private List<byte[]> CollectChunks()
    {
        var result = new List<byte[]>();
        while (_loopbackAccumulator.Count >= ChunkSize)
        {
            var chunk = _loopbackAccumulator.GetRange(0, ChunkSize).ToArray();
            _loopbackAccumulator.RemoveRange(0, ChunkSize);
            result.Add(chunk);
        }
        return result;
    }

    private byte[] TakeFromAccumulator(int needed)
    {
        int available = Math.Min(_loopbackAccumulator.Count, needed);
        var result    = new byte[needed]; // zero-padded = silence
        if (available > 0)
        {
            _loopbackAccumulator.GetRange(0, available).CopyTo(result, 0);
            _loopbackAccumulator.RemoveRange(0, available);
        }
        return result;
    }

    // ── Audio processing helpers ──────────────────────────────────────────

    private static byte[] Mix(byte[] mic, byte[] loopback, int length)
    {
        var result = new byte[length];
        for (int i = 0; i < length - 1; i += 2)
        {
            short sm    = (short)(mic[i]      | (mic[i + 1]      << 8));
            short sl    = (short)(loopback[i] | (loopback[i + 1] << 8));
            int   mixed = Math.Clamp(sm + sl, short.MinValue, short.MaxValue);
            result[i]     = (byte)( mixed       & 0xFF);
            result[i + 1] = (byte)((mixed >> 8) & 0xFF);
        }
        return result;
    }

    private void FireLevelEvent(byte[] buffer)
    {
        double sum     = 0;
        int    samples = buffer.Length / 2;
        for (int i = 0; i < buffer.Length - 1; i += 2)
        {
            short s = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += (double)s * s;
        }
        var rms = (float)(Math.Sqrt(sum / samples) / 32768.0);
        AudioLevelChanged?.Invoke(Math.Min(1f, rms * 36f));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            DeviceLost?.Invoke(e.Exception);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public void StopCapture()
    {
        // Invalidate any in-flight background LoopbackThreadBody().
        Interlocked.Increment(ref _captureGeneration);

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable    -= OnMicDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }

        // Null out shared loopback state and signal the loopback thread.
        // The loopback thread handles all WASAPI teardown — we never call
        // StopRecording() or Dispose() on WasapiLoopbackCapture from here.
        CancellationTokenSource? ctsToCancel;
        lock (_loopbackLock)
        {
            ctsToCancel               = _loopbackCts;
            _loopbackCts              = null;
            _loopbackCapture          = null;
            _loopbackProcessed        = null;
            _loopbackRawBuffer        = null;
            _loopbackInputBytesPerSec = 0;
            _loopbackAccumulator.Clear();
            _loopbackAccumulator.TrimExcess(); // release backing array (can be GBs if bug was triggered)
        }
        ctsToCancel?.Cancel();
    }

    public static IEnumerable<(int Index, string Name)> GetDevices()
    {
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            yield return (i, caps.ProductName);
        }
    }

    public void Dispose() => StopCapture();
}
