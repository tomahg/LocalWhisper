namespace LocalWhisperer.Models;

public enum AudioSourceMode
{
    Microphone,
    SystemAudio,
    Both
}

public enum SilenceSuffixMode
{
    Space,
    Newline,
    DoubleNewline
}

public class AppSettings
{
    public string ServerUrl           { get; set; } = "ws://localhost:8765/ws/transcribe";
    public int    MicrophoneDeviceIndex { get; set; } = 0;
    public AudioSourceMode AudioSource { get; set; } = AudioSourceMode.Microphone;
    public bool   HoldToTalk          { get; set; } = false;
    public bool   AutoConnect         { get; set; } = true;
    public bool   AutoCopyToClipboard { get; set; } = false;
    public bool   AutoSendOnSilence      { get; set; } = false;
    public double SilenceThresholdSeconds { get; set; } = 2.0;
    public SilenceSuffixMode SilenceSuffix { get; set; } = SilenceSuffixMode.Space;

    public bool   InjectTextDirectly { get; set; } = false;

    /// <summary>Win32 virtual-key code for the global hotkey. Default: F9 (0x78).</summary>
    public int    HotkeyVirtualKey    { get; set; } = 0x78;
    /// <summary>Human-readable name shown in the settings UI.</summary>
    public string HotkeyDisplayName   { get; set; } = "F9";
}
