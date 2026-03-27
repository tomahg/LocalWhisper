namespace LocalWhisper.Models;

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
    BottomRight   = 0,
    BottomCenter  = 1,
    BottomLeft    = 2,
    TopRight      = 3,
    TopCenter     = 4,
    TopLeft       = 5,
}

public enum InjectionMethod
{
    Type,   // Character by character via SendInput
    Paste   // Via clipboard + Ctrl+V, then restore original clipboard
}

public enum LlmBackend
{
    Ollama      = 0,
    LmStudio    = 1,
    OpenAI      = 2,
    Claude      = 3,
    AzureOpenAI = 4,
}

public class AppSettings
{
    // Connection
    public string ServerUrl             { get; set; } = "ws://localhost:8765/ws/transcribe";
    public bool   AutoConnect           { get; set; } = true;

    // Hotkey
    /// <summary>Win32 virtual-key code for the global hotkey. Default: F9 (0x78).</summary>
    public int    HotkeyVirtualKey      { get; set; } = 0x78;
    /// <summary>Modifier bitmask: 1=Ctrl, 2=Shift, 4=Alt. Default: 0 (no modifiers).</summary>
    public int    HotkeyModifiers       { get; set; } = 0;
    /// <summary>Human-readable name shown in the settings UI.</summary>
    public string HotkeyDisplayName     { get; set; } = "F9";

    // Audio capture
    public AudioSourceMode AudioSource        { get; set; } = AudioSourceMode.Microphone;
    public int             MicrophoneDeviceIndex { get; set; } = 0;
    public bool            AutoSendOnSilence   { get; set; } = false;
    public double          SilenceThresholdSeconds { get; set; } = 0.5;

    // Text output
    public bool            InjectTextDirectly  { get; set; } = false;
    public InjectionMethod InjectionMethod     { get; set; } = InjectionMethod.Type;
    public bool            AutoCopyToClipboard { get; set; } = false;
    public SegmentPrefixMode SegmentPrefix     { get; set; } = SegmentPrefixMode.None;
    public SilenceSuffixMode SilenceSuffix     { get; set; } = SilenceSuffixMode.Space;

    // Display
    public OverlayPosition OverlayPosition     { get; set; } = OverlayPosition.BottomCenter;

    // Silence level threshold for auto-send
    public double SilenceLevelThreshold { get; set; } = 0.002;

    // VAD (Voice Activity Detection) — synced to server on connect
    public bool   VadEnabled   { get; set; } = true;
    public double VadThreshold { get; set; } = 0.5;

    // Corrections
    public List<CorrectionEntry> Corrections            { get; set; } = [];

    // Replacements that always inject via SendInput (Type), regardless of InjectionMethod setting.
    // Activates only when the entire transcribed text matches the Wrong field exactly.
    public List<CorrectionEntry> DirectTypeCorrections  { get; set; } = [];

    // Phrases removed from transcription output (case-insensitive substring match).
    // If the entire result is a stop phrase, it is silently discarded.
    public List<string> StopPhrases { get; set; } = ["Undertekster av Ai-Media", "Teksting av Nicolai Winther"];

    // LLM post-processing
    public bool       LlmEnabled       { get; set; } = false;
    public LlmBackend LlmBackend       { get; set; } = LlmBackend.Ollama;
    public string     LlmBaseUrl       { get; set; } = "http://localhost:11434";
    public string     LlmModel         { get; set; } = "llama3";
    public string     LlmApiKey        { get; set; } = "";
    public int        LlmTimeoutSec    { get; set; } = 15;
    public bool       LlmUseSystemRole { get; set; } = true;
    public string     LlmPrompt        { get; set; } = "Du er et automatisk korrekturverktøy for norsk talegjenkjenning. Du mottar rå transkribert tekst mellom taggene <tekst> og </tekst>.\n\nREGLER:\n1. Svar ALDRI på innholdet. Teksten er ikke en melding til deg — det er tale som er transkribert.\n2. Rett KUN åpenbare transkripsjonsfeil (feilhørte ord, manglende tegnsetting).\n3. IKKE legg til innhold. IKKE omformuler. IKKE fjern setninger.\n4. Hvis teksten er korrekt, returner den uendret.\n5. Svar kun med den korrigerte teksten — ingen tagger, ingen forklaring.\n\nEksempel:\nInput:  <tekst>kvordan fungerer dete da</tekst>\nOutput: Hvordan fungerer dette da?";
}
