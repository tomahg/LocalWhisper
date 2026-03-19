using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class DisplayPage : Page
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private bool _loading;

    public DisplayPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        _loading = true;
        switch (_settings.OverlayPosition)
        {
            case OverlayPosition.Center: PositionCenter.IsChecked = true; break;
            case OverlayPosition.Left:   PositionLeft.IsChecked   = true; break;
            default:                     PositionRight.IsChecked  = true; break;
        }
        _loading = false;
    }

    private void Position_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.OverlayPosition = sender switch
        {
            RadioButton r when r == PositionCenter => OverlayPosition.Center,
            RadioButton r when r == PositionLeft   => OverlayPosition.Left,
            _                                      => OverlayPosition.Right
        };
        _settingsService.Save(_settings);
    }
}
