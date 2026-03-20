using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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

        AudioSourceComboBox.SelectedIndex = (int)_settings.AudioSource;
        ApplyMicSectionVisibility(_settings.AudioSource);

        foreach (var (index, name) in AudioCaptureService.GetDevices())
            MicComboBox.Items.Add(new ComboBoxItem { Content = name, Tag = index });

        MicComboBox.SelectedIndex = _settings.MicrophoneDeviceIndex < MicComboBox.Items.Count
            ? _settings.MicrophoneDeviceIndex
            : 0;
        AutoSilenceToggle.IsOn = _settings.AutoSendOnSilence;
        SilenceThresholdBox.Value = _settings.SilenceThresholdSeconds;
        SilenceThresholdRow.Visibility = _settings.AutoSendOnSilence
            ? Visibility.Visible
            : Visibility.Collapsed;
        _loading = false;
    }

    private void AudioSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var mode = (AudioSourceMode)AudioSourceComboBox.SelectedIndex;
        _settings.AudioSource = mode;
        _settingsService.Save(_settings);
        ApplyMicSectionVisibility(mode);
    }

    private void ApplyMicSectionVisibility(AudioSourceMode mode)
    {
        var vis = mode != AudioSourceMode.SystemAudio ? Visibility.Visible : Visibility.Collapsed;
        MicSection.Visibility  = vis;
        MicDivider.Visibility  = vis;
    }

    private void MicComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicComboBox.SelectedItem is not ComboBoxItem item) return;
        _settings.MicrophoneDeviceIndex = (int)item.Tag;
        _settingsService.Save(_settings);
    }

    private void AutoSilence_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoSendOnSilence = AutoSilenceToggle.IsOn;
        SilenceThresholdRow.Visibility = AutoSilenceToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        _settingsService.Save(_settings);
    }

    private void SilenceThreshold_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        var rounded = Math.Round(args.NewValue, 1);
        sender.Value = rounded;
        _settings.SilenceThresholdSeconds = rounded;
        _settingsService.Save(_settings);
    }

}
