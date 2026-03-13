using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using LocalWhisperer.Models;
using LocalWhisperer.Services;
using LocalWhisperer.ViewModels;

namespace LocalWhisperer;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    private MainWindow?     _window;
    private OverlayWindow?  _overlay;
    private TaskbarIcon?    _trayIcon;
    private HotkeyService?  _hotkey;
    private DispatcherQueue? _dispatcherQueue;

    private static readonly Uri UriIdle       = new("ms-appx:///Assets/tray-idle.ico");
    private static readonly Uri UriListening  = new("ms-appx:///Assets/tray-listening.ico");

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
        services.AddSingleton<TextInjectionService>();
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
        _window.Activate();

        // Initialize directly — subscribing to Activated after Activate() misses the first event
        InitializeTrayIcon();
        RegisterHotkey();

        _window.AppWindow.Hide();

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

        var menu = new MenuFlyout();

        var settingsItem = new MenuFlyoutItem { Text = "Innstillinger" };
        settingsItem.Click += (_, _) => ShowWindow();

        var exitItem = new MenuFlyoutItem { Text = "Avslutt" };
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(settingsItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextFlyout    = menu;
        _trayIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        _trayIcon.ForceCreate(false);

        // Subscribe to state changes for icon + tooltip updates
        var ws           = Services.GetRequiredService<WebSocketService>();
        var orchestrator = Services.GetRequiredService<TranscriptionOrchestrator>();

        _overlay = new OverlayWindow();
        _overlay.Activate();   // must activate once so AppWindow is ready; Hide() immediately follows
        _overlay.AppWindow.Hide();

        ws.ConnectionError    += _ => UpdateTrayIcon(recording: false, connected: false);
        ws.ConnectionRestored += () => UpdateTrayIcon(recording: false, connected: true);
        orchestrator.RecordingStateChanged += isRecording =>
        {
            UpdateTrayIcon(recording: isRecording, connected: true);
            if (isRecording)
                _overlay.ShowListening();
            else
                _overlay.ShowProcessing();
        };
        orchestrator.AudioLevelChanged += level => _overlay.UpdateAudioLevel(level);
        orchestrator.TranscriptionUpdated += (text, isFinal) =>
        {
            if (!isFinal) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                _overlay.Hide();
                return;
            }
            var settings = Services.GetRequiredService<AppSettings>();
            if (settings.AutoCopyToClipboard)
            {
                _overlay.CopyToClipboard(text);
                _overlay.Hide();
            }
            else
            {
                _overlay.ShowResult(text);
            }
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

    private void UpdateTrayIcon(bool recording, bool connected)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_trayIcon is null) return;

            _trayIcon.IconSource  = new BitmapImage(recording ? UriListening : UriIdle);
            _trayIcon.ToolTipText = connected
                ? (recording ? "LocalWhisperer — Tar opp..." : "LocalWhisperer — Tilkoblet")
                : "LocalWhisperer — Frakoblet";
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

        _hotkey.Register(settings.HotkeyVirtualKey);

        _hotkey.HotkeyDown += () => _dispatcherQueue?.TryEnqueue(() =>
        {
            if (!ws.IsConnected) return;

            if (settings.HoldToTalk)
            {
                if (!orchestrator.IsRecording) orchestrator.StartRecording();
            }
            else
            {
                if (!orchestrator.IsRecording)
                    orchestrator.StartRecording();
                else
                    _ = orchestrator.StopRecordingAsync();
            }
        });

        _hotkey.HotkeyUp += () =>
        {
            if (!settings.HoldToTalk) return;
            _dispatcherQueue?.TryEnqueue(() => _ = orchestrator.StopRecordingAsync());
        };
    }

    // -------------------------------------------------------------------------
    // Window management
    // -------------------------------------------------------------------------

    private async Task AutoConnectAsync(string url)
    {
        var ws = Services.GetRequiredService<WebSocketService>();
        try { await ws.ConnectAsync(url); }
        catch { /* auto-reconnect will retry; tray icon updated via ConnectionError event */ }
    }

    private void ShowWindow()
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _window ??= new MainWindow();
            _window.AppWindow.Show();
            _window.Activate();
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Intercept close — hide instead of destroying the window
        args.Handled = true;
        _window?.AppWindow.Hide();
    }

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        _hotkey?.Dispose();
        if (_window is not null)
            _window.Closed -= OnWindowClosed;
        _window?.Close();
        Exit();
    }
}
