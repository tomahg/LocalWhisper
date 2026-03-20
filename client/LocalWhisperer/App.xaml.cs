using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using LocalWhisperer.Helpers;
using LocalWhisperer.Models;
using LocalWhisperer.Services;
using LocalWhisperer.ViewModels;
using static LocalWhisperer.Models.TranscriptionSource;

namespace LocalWhisperer;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;
    public OverlayWindow? Overlay => _overlay;

    private MainWindow?     _window;
    private OverlayWindow?  _overlay;
    private TaskbarIcon?    _trayIcon;
    private HotkeyService?  _hotkey;
    private DispatcherQueue? _dispatcherQueue;
    private bool             _isExiting;
    private string           _accumulatedText = "";
    private DateTime         _hotkeyPressedAt;
    private const int        HoldThresholdMs = 300;

    /// <summary>
    /// Injects text into the active window, converting '\n' characters to real Return key presses.
    /// </summary>
    private static void InjectText(string text)
    {
        var parts = text.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                NativeMethods.SendUnicodeString(parts[i]);
            if (i < parts.Length - 1)
                NativeMethods.SendReturn();
        }
    }

    /// <summary>
    /// Pastes text via clipboard + Ctrl+V, then restores the previous clipboard content.
    /// Runs on a background thread to avoid blocking the UI during the restore delay.
    /// </summary>
    private static void InjectTextViaClipboard(string text)
    {
        _ = Task.Run(() =>
        {
            NativeMethods.SetClipboardText(text);
            NativeMethods.SendCtrlV();
        });
    }

    private static void Inject(string text, AppSettings settings)
    {
        if (settings.InjectionMethod == InjectionMethod.Paste)
            InjectTextViaClipboard(text);
        else
            InjectText(text);
    }

    private static string GetSeparator(SilenceSuffixMode mode) => mode switch
    {
        SilenceSuffixMode.Space         => " ",
        SilenceSuffixMode.Newline       => "\n",
        SilenceSuffixMode.DoubleNewline => "\n\n",
        _                               => ""
    };

    private static string GetPrefix(SegmentPrefixMode mode) => mode switch
    {
        SegmentPrefixMode.Space => " ",
        SegmentPrefixMode.Dash  => "- ",
        SegmentPrefixMode.Star  => "* ",
        _                       => ""
    };

    private static Uri AssetUri(string fileName) =>
        new(Path.Combine(AppContext.BaseDirectory, "Assets", fileName));

    private static readonly Uri UriIdle         = AssetUri("tray-idle.ico");
    private static readonly Uri UriListening    = AssetUri("tray-listening.ico");
    private static readonly Uri UriDisconnected = AssetUri("tray-disconnected.ico");

    public App()
    {
        InitializeComponent();

        var sc = new ServiceCollection();
        ConfigureServices(sc);
        Services = sc.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var settingsService = new SettingsService();
        var settings = settingsService.Load();

        services.AddSingleton(settingsService);
        services.AddSingleton(settings);
        services.AddSingleton<AudioCaptureService>();
        services.AddSingleton<WebSocketService>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TranscriptionOrchestrator>();
        services.AddSingleton<ServerApiService>();
        services.AddSingleton<MainViewModel>();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Capture the UI thread's DispatcherQueue — Application has no .DispatcherQueue property
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _window = new MainWindow();
        _window.Closed += OnWindowClosed;

        // Move offscreen before Activate() to avoid a visible flash
        _window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-9999, -9999, 0, 0));
        _window.Activate();

        // Initialize directly — subscribing to Activated after Activate() misses the first event
        InitializeTrayIcon();
        RegisterHotkey();

        _window.AppWindow.Hide();
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(580, 800));

        var settings = Services.GetRequiredService<AppSettings>();
        if (settings.AutoConnect)
            _ = AutoConnectAsync(settings.ServerUrl);
    }

    // -------------------------------------------------------------------------
    // Tray icon
    // -------------------------------------------------------------------------

    private void InitializeTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        _trayIcon.ContextFlyout = null; // Don't use WinUI MenuFlyout — it doesn't fire events

        _trayIcon.LeftClickCommand  = new RelayCommand(ShowWindow);
        _trayIcon.RightClickCommand = new RelayCommand(ShowTrayContextMenu);
        _trayIcon.ForceCreate(false);

        // Subscribe to state changes for icon + tooltip updates
        var ws           = Services.GetRequiredService<WebSocketService>();
        var orchestrator = Services.GetRequiredService<TranscriptionOrchestrator>();

        _overlay = new OverlayWindow();
        _overlay.Activate();   // must activate once so AppWindow is ready; Hide() immediately follows
        _overlay.AppWindow.Hide();

        _overlay.FileSelected += async (filePath) =>
        {
            if (orchestrator.IsRecording)
                await orchestrator.StopRecordingAsync();
            _overlay.ShowProcessing();
            try
            {
                await orchestrator.TranscribeFileAsync(filePath);
            }
            catch (Exception ex)
            {
                _overlay.ShowResult($"Feil: {ex.Message}");
            }
        };

        ws.ConnectionError    += _ => UpdateTrayIcon(recording: false, connected: false);
        ws.Disconnected       += () => UpdateTrayIcon(recording: false, connected: false);
        ws.ConnectionRestored += () => UpdateTrayIcon(recording: false, connected: true);

        // Set correct initial icon — depends on AutoConnect result, so do it after events are wired
        UpdateTrayIcon(recording: false, connected: ws.IsConnected);
        orchestrator.RecordingStateChanged += isRecording =>
        {
            UpdateTrayIcon(recording: isRecording, connected: true);
            if (orchestrator.IsTranscribingFile) return; // file transcription controls overlay
            if (isRecording)
            {
                _accumulatedText = "";
                _overlay.ShowListening();
            }
            else
                _overlay.ShowProcessing();
        };
        orchestrator.AudioLevelChanged += level => _overlay.UpdateAudioLevel(level);
        orchestrator.TranscriptionUpdated += (result, source) =>
        {
            if (!result.IsFinal) return;
            var text = result.Text;
            var settings = Services.GetRequiredService<AppSettings>();

            if (settings.Corrections.Count > 0)
                text = CorrectorService.Apply(text, settings.Corrections);

            if (settings.InjectTextDirectly)
            {
                // --- Inject mode ---
                if (source == AutoSilence)
                {
                    // Inject this segment immediately; overlay stays in "Lytter..." state
                    if (!string.IsNullOrEmpty(text))
                    {
                        var prefix   = GetPrefix(settings.SegmentPrefix);
                        var suffix   = GetSeparator(settings.SilenceSuffix);
                        var toInject = prefix + (char.IsWhiteSpace(text[^1]) ? text : text + suffix);
                        Inject(toInject, settings);
                    }
                    return;
                }
                // Microphone (final stop)
                if (source == Microphone)
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        var prefix   = GetPrefix(settings.SegmentPrefix);
                        var suffix   = GetSeparator(settings.SilenceSuffix);
                        var toInject = prefix + (char.IsWhiteSpace(text[^1]) ? text : text + suffix);
                        Inject(toInject, settings);
                    }
                    _overlay.Hide();
                    return;
                }
                // File
                if (!string.IsNullOrEmpty(text))
                    Inject(text, settings);
                _overlay.Hide();
                return;
            }

            // --- Overlay mode ---
            var sep = GetSeparator(settings.SilenceSuffix);

            if (source == AutoSilence)
            {
                // Intermediate result from silence detection — accumulate and keep listening
                if (string.IsNullOrWhiteSpace(text)) return;
                if (_accumulatedText.Length > 0)
                    _accumulatedText += sep;
                _accumulatedText += GetPrefix(settings.SegmentPrefix) + text;
                if (settings.AutoCopyToClipboard)
                {
                    _dispatcherQueue?.TryEnqueue(() => _overlay.CopyToClipboard(_accumulatedText));
                }
                _overlay.ShowListeningWithText(_accumulatedText);
                return;
            }

            if (source == Microphone)
            {
                // Final stop from user hotkey
                if (_accumulatedText.Length > 0)
                {
                    // Append final segment to accumulated text
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _accumulatedText += sep;
                        _accumulatedText += GetPrefix(settings.SegmentPrefix) + text;
                    }
                    text = _accumulatedText;
                    _accumulatedText = "";
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    text = GetPrefix(settings.SegmentPrefix) + text;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    _overlay.Hide();
                    return;
                }

                if (!char.IsWhiteSpace(text[^1]))
                    text += sep;

                if (settings.AutoCopyToClipboard)
                {
                    _dispatcherQueue?.TryEnqueue(() => _overlay.CopyToClipboard(text));
                    _overlay.ShowResult(text, showCopy: true,
                        audioDurationMs: result.AudioDurationMs, processingTimeMs: result.ProcessingTimeMs);
                }
                else
                {
                    _overlay.ShowResult(text, showCopy: true,
                        audioDurationMs: result.AudioDurationMs, processingTimeMs: result.ProcessingTimeMs);
                }
                return;
            }

            // File transcription
            if (string.IsNullOrWhiteSpace(text))
            {
                _overlay.ShowResult("Ingen tale funnet", showCopy: false,
                    audioDurationMs: result.AudioDurationMs, processingTimeMs: result.ProcessingTimeMs);
                return;
            }
            _overlay.ShowResult(text,
                audioDurationMs: result.AudioDurationMs, processingTimeMs: result.ProcessingTimeMs);
        };
        orchestrator.MicrophoneDeviceLost += () =>
        {
            UpdateTrayIcon(recording: false, connected: ws.IsConnected);
            _overlay.Hide();
            _dispatcherQueue?.TryEnqueue(() =>
                _trayIcon?.ShowNotification("LocalWhisperer",
                    "Mikrofon frakoblet — opptak stoppet.", H.NotifyIcon.Core.NotificationIcon.Warning));
        };
    }

    private void ShowTrayContextMenu()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        int choice = NativeMethods.ShowNativePopupMenu(hwnd,
            ["Innstillinger", "Last inn lydfil...", "-", "Avslutt"]);

        switch (choice)
        {
            case 1: ShowWindow();          break;   // Innstillinger
            case 2: _overlay?.OpenFilePicker(); break;   // Last inn lydfil...
            case 4: ExitApp();             break;   // Avslutt (index 4 because separator is index 3)
        }
    }

    private void UpdateTrayIcon(bool recording, bool connected)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_trayIcon is null) return;

            _trayIcon.IconSource  = new BitmapImage(recording ? UriListening : connected ? UriIdle : UriDisconnected);
            _trayIcon.ToolTipText = connected
                ? (recording ? "LocalWhisperer – Tar opp..." : "Local Whisperer – Tilkoblet")
                : "Local Whisperer – Frakoblet";
        });
    }

    // -------------------------------------------------------------------------
    // Global hotkey — registered at App level (works without a visible window)
    // -------------------------------------------------------------------------

    private void RegisterHotkey()
    {
        _hotkey      = Services.GetRequiredService<HotkeyService>();
        var settings     = Services.GetRequiredService<AppSettings>();
        var orchestrator = Services.GetRequiredService<TranscriptionOrchestrator>();
        var ws           = Services.GetRequiredService<WebSocketService>();

        _hotkey.Register(settings.HotkeyVirtualKey, settings.HotkeyModifiers);

        _hotkey.EscapePressed += () => _overlay?.Hide();

        _hotkey.HotkeyDown += () => _dispatcherQueue?.TryEnqueue(() =>
        {
            if (!ws.IsConnected) return;

            if (!orchestrator.IsRecording)
            {
                _hotkeyPressedAt = DateTime.UtcNow;
                orchestrator.StartRecording();
            }
            else
            {
                _ = orchestrator.StopRecordingAsync();
            }
        });

        _hotkey.HotkeyUp += () =>
        {
            if (!orchestrator.IsRecording) return;
            if ((DateTime.UtcNow - _hotkeyPressedAt).TotalMilliseconds >= HoldThresholdMs)
                _dispatcherQueue?.TryEnqueue(() => _ = orchestrator.StopRecordingAsync());
        };
    }

    // -------------------------------------------------------------------------
    // Window management
    // -------------------------------------------------------------------------

    private async Task AutoConnectAsync(string url)
    {
        var ws = Services.GetRequiredService<WebSocketService>();
        try
        {
            await ws.ConnectAsync(url);
            UpdateTrayIcon(recording: false, connected: true);
        }
        catch { /* tray icon updated via ConnectionError event */ }
    }

    private void ShowWindow()
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _window ??= new MainWindow();

            // The window is initially placed off-screen to avoid a flash at startup.
            // Move it to the center of the work area before showing.
            var pos = _window.AppWindow.Position;
            if (pos.X <= -9000 || pos.Y <= -9000)
            {
                var workArea = Microsoft.UI.Windowing.DisplayArea
                    .GetFromWindowId(_window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary)
                    .WorkArea;
                var size = _window.AppWindow.Size;
                _window.AppWindow.Move(new Windows.Graphics.PointInt32(
                    workArea.X + (workArea.Width  - size.Width)  / 2,
                    workArea.Y + (workArea.Height - size.Height) / 2));
            }

            _window.AppWindow.Show();
            _window.Activate();
            NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_isExiting) return; // Allow real close during exit

        // Intercept close — hide instead of destroying the window
        args.Handled = true;
        _window?.AppWindow.Hide();
    }

    public void ExitApp()
    {
        _isExiting = true;
        try
        {
            _hotkey?.Dispose();
            _window?.Close();
            _overlay?.Close();
        }
        catch
        {
            // Swallow — we're terminating anyway
        }

        Environment.Exit(0);
    }
}
