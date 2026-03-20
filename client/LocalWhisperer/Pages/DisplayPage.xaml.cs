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
        var rb = _settings.OverlayPosition switch
        {
            OverlayPosition.TopLeft     => PositionTopLeft,
            OverlayPosition.TopCenter   => PositionTopCenter,
            OverlayPosition.TopRight    => PositionTopRight,
            OverlayPosition.BottomLeft  => PositionBottomLeft,
            OverlayPosition.BottomCenter => PositionBottomCenter,
            _                           => PositionBottomRight,
        };
        rb.IsChecked = true;
        _loading = false;
    }

    private void Position_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.OverlayPosition = sender switch
        {
            RadioButton r when r == PositionTopLeft     => OverlayPosition.TopLeft,
            RadioButton r when r == PositionTopCenter   => OverlayPosition.TopCenter,
            RadioButton r when r == PositionTopRight    => OverlayPosition.TopRight,
            RadioButton r when r == PositionBottomLeft  => OverlayPosition.BottomLeft,
            RadioButton r when r == PositionBottomCenter => OverlayPosition.BottomCenter,
            _                                           => OverlayPosition.BottomRight,
        };
        _settingsService.Save(_settings);
        if (Application.Current is App app)
            app.Overlay?.RepositionIfVisible();
    }
}
