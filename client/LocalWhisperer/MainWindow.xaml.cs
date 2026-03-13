using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer;

public sealed partial class MainWindow : Window
{
    private readonly WebSocketService _ws;
    private readonly AudioCaptureService _audio;
    private readonly TranscriptionOrchestrator _orchestrator;

    public MainWindow()
    {
        InitializeComponent();

        // Set minimum window size (no MinWidth/MinHeight on WinUI 3 Window)
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this)));
        appWindow.Resize(new Windows.Graphics.SizeInt32(520, 420));

        _ws = App.Services.GetRequiredService<WebSocketService>();
        _audio = App.Services.GetRequiredService<AudioCaptureService>();
        _orchestrator = App.Services.GetRequiredService<TranscriptionOrchestrator>();

        ServerUrlBox.Text = App.Services.GetRequiredService<AppSettings>().ServerUrl;

        _orchestrator.TranscriptionUpdated += OnTranscriptionUpdated;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;

        App.Services.GetRequiredService<AppSettings>().ServerUrl = url;
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

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        RecordButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _orchestrator.StartRecording();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        RecordButton.IsEnabled = true;
        await _orchestrator.StopRecordingAsync();
    }

    private void OnTranscriptionUpdated(string text, bool isFinal)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var prefix = isFinal ? "[final]  " : "[partial]";
            TranscriptionLog.Text += $"{prefix} {text}\n";
        });
    }
}
