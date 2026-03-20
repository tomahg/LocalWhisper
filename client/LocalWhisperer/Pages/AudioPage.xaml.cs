using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class AudioPage : Page
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly TranscriptionOrchestrator _orchestrator;
    private bool _loading;
    private CancellationTokenSource? _calibrationCts;

    public AudioPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();
        _orchestrator    = App.Services.GetRequiredService<TranscriptionOrchestrator>();

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

        VadEnabledToggle.IsOn = _settings.VadEnabled;
        var clampedThreshold = Math.Clamp(_settings.VadThreshold, 0.10, 0.90);
        VadThresholdSlider.Value = clampedThreshold;
        VadThresholdLabel.Text = clampedThreshold.ToString("F2");
        ApplyVadPanelVisibility(_settings.VadEnabled);

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

    private void VadEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.VadEnabled = VadEnabledToggle.IsOn;
        ApplyVadPanelVisibility(_settings.VadEnabled);
        _settingsService.Save(_settings);
        _ = _orchestrator.SyncVadSettingsAsync();
    }

    private void ApplyVadPanelVisibility(bool vadEnabled)
    {
        var vis = vadEnabled ? Visibility.Visible : Visibility.Collapsed;
        VadDivider.Visibility      = vis;
        VadSettingsPanel.Visibility = vis;
    }

    private void VadThreshold_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading || double.IsNaN(e.NewValue)) return;
        var rounded = Math.Round(e.NewValue, 2);
        VadThresholdLabel.Text = rounded.ToString("F2");
        _settings.VadThreshold = rounded;
        _settingsService.Save(_settings);
        _ = _orchestrator.SyncVadSettingsAsync();
    }

    private async void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        _calibrationCts = new CancellationTokenSource();
        CalibrateButton.IsEnabled = false;
        CalibrateStatusText.Visibility = Visibility.Visible;

        try
        {
            // Start recording immediately while showing countdown
            var calibrationTask = _orchestrator.CalibrateVadAsync(_calibrationCts.Token);

            for (int i = 3; i >= 1; i--)
            {
                CalibrateStatusText.Text = $"Hold deg stille... {i}";
                await Task.Delay(1000, _calibrationCts.Token);
            }

            CalibrateStatusText.Text = "Analyserer...";
            var recommended = await calibrationTask;

            _settings.VadThreshold = recommended;
            _settingsService.Save(_settings);

            _loading = true;
            VadThresholdSlider.Value = Math.Clamp(recommended, 0.10, 0.90);
            VadThresholdLabel.Text = recommended.ToString("F2");
            _loading = false;

            CalibrateStatusText.Text = $"Anbefalt: {recommended:F2}";
            await _orchestrator.SyncVadSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            CalibrateStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CalibrateStatusText.Text = $"Feil: {ex.Message}";
        }
        finally
        {
            CalibrateButton.IsEnabled = true;
            _calibrationCts?.Dispose();
            _calibrationCts = null;
        }
    }

}
