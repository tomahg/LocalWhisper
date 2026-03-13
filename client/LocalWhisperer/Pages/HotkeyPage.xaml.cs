using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class HotkeyPage : Page
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;

    public HotkeyPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        HotkeyLabel.Text          = _settings.Hotkey;
        HoldToTalkToggle.IsOn     = _settings.HoldToTalk;
    }

    private void HoldToTalkToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _settings.HoldToTalk = HoldToTalkToggle.IsOn;
        _settingsService.Save(_settings);
    }
}
