namespace LocalWhisperer.Models;

public class AppSettings
{
    public string ServerUrl { get; set; } = "ws://localhost:8765/ws/transcribe";
    public int MicrophoneDeviceIndex { get; set; } = 0;
    public string Hotkey { get; set; } = "F9";
    public bool HoldToTalk { get; set; } = false;
}
