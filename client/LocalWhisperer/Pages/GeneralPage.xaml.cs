using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class GeneralPage : Page
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private bool _loading;

    public GeneralPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        _loading = true;
        SilenceSuffixComboBox.SelectedIndex = (int)_settings.SilenceSuffix;
        _loading = false;
    }

    private void SilenceSuffix_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.SilenceSuffix = (SilenceSuffixMode)SilenceSuffixComboBox.SelectedIndex;
        _settingsService.Save(_settings);
    }
}
