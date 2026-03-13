using NAudio.Wave;

namespace LocalWhisperer.Services;

/// <summary>
/// Captures microphone audio as 16kHz 16-bit mono PCM and raises AudioDataAvailable
/// with raw byte chunks suitable for sending directly to the transcription server.
/// </summary>
public class AudioCaptureService : IDisposable
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BufferMilliseconds = 100;

    private WaveInEvent? _waveIn;

    public event Action<byte[]>? AudioDataAvailable;

    public void StartCapture(int deviceIndex = 0)
    {
        StopCapture(); // Dispose any previous instance before creating a new one

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = BufferMilliseconds,
            DeviceNumber = deviceIndex,
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
    }

    public void StopCapture()
    {
        if (_waveIn is null) return;
        _waveIn.StopRecording();
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
        _waveIn = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        var buffer = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, buffer, e.BytesRecorded);
        AudioDataAvailable?.Invoke(buffer);
    }

    public static IEnumerable<(int Index, string Name)> GetDevices()
    {
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            yield return (i, caps.ProductName);
        }
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _waveIn = null;
    }
}
