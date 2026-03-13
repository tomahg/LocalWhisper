using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly WebSocketService _ws;
    private readonly TranscriptionOrchestrator _orchestrator;
    private readonly AppSettings _settings;

    [ObservableProperty]
    private string _statusText = "Frakoblet";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _transcriptionLog = "";

    public MainViewModel(WebSocketService ws, TranscriptionOrchestrator orchestrator, AppSettings settings)
    {
        _ws = ws;
        _orchestrator = orchestrator;
        _settings = settings;

        _orchestrator.TranscriptionUpdated += OnTranscriptionUpdated;
        _ws.ConnectionError += OnConnectionError;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            await _ws.ConnectAsync(_settings.ServerUrl);
            IsConnected = true;
            StatusText = "Tilkoblet";
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = $"Feil: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StartRecording()
    {
        IsRecording = true;
        _orchestrator.StartRecording(injectText: false);
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        IsRecording = false;
        await _orchestrator.StopRecordingAsync();
    }

    private void OnTranscriptionUpdated(string text, bool isFinal)
    {
        var prefix = isFinal ? "[final]  " : "[partial]";
        TranscriptionLog += $"{prefix} {text}\n";
    }

    private void OnConnectionError(Exception ex)
    {
        IsConnected = false;
        StatusText = $"Tilkoblingsfeil: {ex.Message}";
    }
}
