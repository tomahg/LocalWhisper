using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class ConnectionPage : Page
{
    private readonly WebSocketService _ws;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;

    public ConnectionPage()
    {
        InitializeComponent();
        _ws             = App.Services.GetRequiredService<WebSocketService>();
        _settings       = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        ServerUrlBox.Text             = _settings.ServerUrl;
        AutoConnectCheckBox.IsChecked = _settings.AutoConnect;
        UpdateStatus();

        _ws.ConnectionError    += _ => DispatcherQueue.TryEnqueue(UpdateStatus);
        _ws.ConnectionRestored +=  () => DispatcherQueue.TryEnqueue(UpdateStatus);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;

        _settings.ServerUrl = url;
        _settingsService.Save(_settings);
        ErrorBar.IsOpen = false;
        ConnectButton.IsEnabled = false;

        try
        {
            await _ws.ConnectAsync(url);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            UpdateStatus();
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void AutoConnect_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AutoConnect = AutoConnectCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _ws.AutoReconnect = false;
        await _ws.DisconnectAsync();
        _ws.AutoReconnect = true;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var connected = _ws.IsConnected;
        StatusText.Text         = connected ? "Tilkoblet" : "Frakoblet";
        ConnectButton.IsEnabled    = !connected;
        DisconnectButton.IsEnabled =  connected;
    }
}
