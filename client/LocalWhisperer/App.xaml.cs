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

    private MainWindow? _window;
    private TaskbarIcon? _trayIcon;
    private HotkeyService? _hotkey;
    private DispatcherQueue? _dispatcherQueue;
    private bool _trayInitialized;

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
        _window.Activate();
        // App lives in the tray — hide the settings window immediately
        _window.AppWindow.Hide();

        _window.Activated += OnFirstActivation;
        _window.Closed    += OnWindowClosed;
    }

    private void OnFirstActivation(object sender, WindowActivatedEventArgs e)
    {
        if (_trayInitialized) return;
        _trayInitialized = true;

        InitializeTrayIcon();
        RegisterHotkey();
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

        ws.ConnectionError    += _ => UpdateTrayIcon(recording: false, connected: false);
        ws.ConnectionRestored += () => UpdateTrayIcon(recording: false, connected: true);
        orchestrator.TranscriptionUpdated += (_, isFinal) =>
            UpdateTrayIcon(recording: !isFinal, connected: true);
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

        _hotkey.Register(0x78); // F9

        _hotkey.HotkeyDown += () => _dispatcherQueue?.TryEnqueue(() =>
        {
            if (!ws.IsConnected) return;

            if (settings.HoldToTalk)
            {
                if (!orchestrator.IsRecording) orchestrator.StartRecording(injectText: true);
            }
            else
            {
                if (!orchestrator.IsRecording)
                    orchestrator.StartRecording(injectText: true);
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
