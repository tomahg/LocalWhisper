namespace LocalWhisperer.Models;

public enum AudioSourceMode
{
    Microphone,
    SystemAudio,
    Both
}

public enum SilenceSuffixMode
{
    None          = 0,
    Space         = 1,
    Newline       = 2,
    DoubleNewline = 3
}

public enum SegmentPrefixMode
{
    None  = 0,
    Space = 1,  // " "
    Dash  = 2,  // "- "
    Star  = 3   // "* "
}

public enum OverlayPosition
{
    Right,
    Center,
    Left
}

public enum InjectionMethod
{
    Type,   // Character by character via SendInput
    Paste   // Via clipboard + Ctrl+V, then restore original clipboard
}

public class AppSettings
{
    // Connection
    public string ServerUrl             { get; set; } = "ws://localhost:8765/ws/transcribe";
    public bool   AutoConnect           { get; set; } = true;

    // Hotkey
    /// <summary>Win32 virtual-key code for the global hotkey. Default: F9 (0x78).</summary>
    public int    HotkeyVirtualKey      { get; set; } = 0x78;
    /// <summary>Human-readable name shown in the settings UI.</summary>
    public string HotkeyDisplayName     { get; set; } = "F9";

    // Audio capture
    public AudioSourceMode AudioSource        { get; set; } = AudioSourceMode.Microphone;
    public int             MicrophoneDeviceIndex { get; set; } = 0;
    public bool            AutoSendOnSilence   { get; set; } = false;
    public double          SilenceThresholdSeconds { get; set; } = 2.0;

    // Text output
    public bool            InjectTextDirectly  { get; set; } = false;
    public InjectionMethod InjectionMethod     { get; set; } = InjectionMethod.Type;
    public bool            AutoCopyToClipboard { get; set; } = false;
    public SegmentPrefixMode SegmentPrefix     { get; set; } = SegmentPrefixMode.None;
    public SilenceSuffixMode SilenceSuffix     { get; set; } = SilenceSuffixMode.Space;

    // Display
    public OverlayPosition OverlayPosition     { get; set; } = OverlayPosition.Right;

    // Silence level threshold for auto-send (compared against 36× amplified RMS level)
    public double SilenceLevelThreshold { get; set; } = 0.08;

    // VAD (Voice Activity Detection) — synced to server on connect
    public bool   VadEnabled   { get; set; } = true;
    public double VadThreshold { get; set; } = 0.5;

    // Corrections
    public List<CorrectionEntry> Corrections   { get; set; } = [];
}
