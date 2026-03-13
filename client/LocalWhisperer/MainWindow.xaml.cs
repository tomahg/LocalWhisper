using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer;

public sealed partial class MainWindow : Window
{
    private const int VK_F9 = 0x78;

    private readonly WebSocketService _ws;
    private readonly AudioCaptureService _audio;
    private readonly TranscriptionOrchestrator _orchestrator;
    private readonly HotkeyService _hotkey;
    private readonly AppSettings _settings;

    private bool _lastLineIsPartial;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();

        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this)));
        appWindow.Resize(new Windows.Graphics.SizeInt32(520, 460));

        _ws           = App.Services.GetRequiredService<WebSocketService>();
        _audio        = App.Services.GetRequiredService<AudioCaptureService>();
        _orchestrator = App.Services.GetRequiredService<TranscriptionOrchestrator>();
        _hotkey       = App.Services.GetRequiredService<HotkeyService>();
        _settings     = App.Services.GetRequiredService<AppSettings>();

        ServerUrlBox.Text = _settings.ServerUrl;

        _orchestrator.TranscriptionUpdated += OnTranscriptionUpdated;
        _ws.ConnectionError += OnConnectionError;

        // Register hotkey after the window is activated so the hook thread has
        // a message loop (required for WH_KEYBOARD_LL).
        Activated += OnFirstActivation;
    }

    // -------------------------------------------------------------------------
    // Hotkey
    // -------------------------------------------------------------------------

    private void OnFirstActivation(object sender, WindowActivatedEventArgs e)
    {
        if (_hotkeyRegistered) return;
        _hotkeyRegistered = true;

        _hotkey.Register(VK_F9);
        _hotkey.HotkeyDown += OnHotkeyDown;
        _hotkey.HotkeyUp   += OnHotkeyUp;
    }

    private void OnHotkeyDown()
    {
        // Hook callback fires on the UI thread — post work back to the message
        // loop so the hook proc returns immediately (required: <1000ms).
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_ws.IsConnected) return;

            if (_settings.HoldToTalk)
            {
                if (!_orchestrator.IsRecording)
                    StartRecording(injectText: true);
            }
            else
            {
                // Toggle
                if (!_orchestrator.IsRecording)
                    StartRecording(injectText: true);
                else
                    _ = StopRecordingAsync();
            }
        });
    }

    private void OnHotkeyUp()
    {
        if (!_settings.HoldToTalk) return;
        DispatcherQueue.TryEnqueue(() => _ = StopRecordingAsync());
    }

    // -------------------------------------------------------------------------
    // Recording state helpers (shared by hotkey and test buttons)
    // -------------------------------------------------------------------------

    private void StartRecording(bool injectText = false)
    {
        _orchestrator.StartRecording(injectText);
        StatusText.Text = "Tar opp...";
        RecordButton.IsEnabled = false;
        StopButton.IsEnabled = true;
    }

    private async Task StopRecordingAsync()
    {
        await _orchestrator.StopRecordingAsync();
        StatusText.Text = "Tilkoblet";
        RecordButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    // -------------------------------------------------------------------------
    // UI event handlers
    // -------------------------------------------------------------------------

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;

        _settings.ServerUrl = url;
        ErrorBar.IsOpen = false;

        try
        {
            await _ws.ConnectAsync(url);
            StatusText.Text = "Tilkoblet";
            RecordButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Frakoblet";
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e) =>
        StartRecording(injectText: false);

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await StopRecordingAsync();

    private void OnConnectionError(Exception _)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = "Frakoblet";
            RecordButton.IsEnabled = false;
            StopButton.IsEnabled = false;
        });
    }

    // -------------------------------------------------------------------------
    // Transcription log
    // -------------------------------------------------------------------------

    private void OnTranscriptionUpdated(string text, bool isFinal)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_lastLineIsPartial)
            {
                var lastNewline = TranscriptionLog.Text.LastIndexOf('\n',
                    TranscriptionLog.Text.Length - 2);
                TranscriptionLog.Text = lastNewline >= 0
                    ? TranscriptionLog.Text[..(lastNewline + 1)]
                    : string.Empty;
            }

            var prefix = isFinal ? "[final]  " : "[partial]";
            TranscriptionLog.Text += $"{prefix} {text}\n";
            _lastLineIsPartial = !isFinal;
        });
    }
}
