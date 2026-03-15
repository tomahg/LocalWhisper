using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class AudioPage : Page
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private bool _loading;

    public AudioPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        _loading = true;
        foreach (var (index, name) in AudioCaptureService.GetDevices())
            MicComboBox.Items.Add(new ComboBoxItem { Content = name, Tag = index });

        MicComboBox.SelectedIndex = _settings.MicrophoneDeviceIndex < MicComboBox.Items.Count
            ? _settings.MicrophoneDeviceIndex
            : 0;
        AutoCopyToggle.IsOn = _settings.AutoCopyToClipboard;
        _loading = false;
    }

    private void MicComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicComboBox.SelectedItem is not ComboBoxItem item) return;
        _settings.MicrophoneDeviceIndex = (int)item.Tag;
        _settingsService.Save(_settings);
    }

    private void AutoCopy_Changed(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoCopyToClipboard = AutoCopyToggle.IsOn;
        _settingsService.Save(_settings);
    }
}
