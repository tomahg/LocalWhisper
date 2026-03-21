using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LocalWhisper.Models;
using LocalWhisper.Services;

namespace LocalWhisper.Pages;

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
        var pos = _settings.OverlayPosition;
        VPosTop.IsChecked    = pos is OverlayPosition.TopLeft    or OverlayPosition.TopCenter    or OverlayPosition.TopRight;
        VPosBottom.IsChecked = pos is OverlayPosition.BottomLeft or OverlayPosition.BottomCenter or OverlayPosition.BottomRight;
        HPosLeft.IsChecked   = pos is OverlayPosition.TopLeft    or OverlayPosition.BottomLeft;
        HPosCenter.IsChecked = pos is OverlayPosition.TopCenter  or OverlayPosition.BottomCenter;
        HPosRight.IsChecked  = pos is OverlayPosition.TopRight   or OverlayPosition.BottomRight;
        _loading = false;
    }

    private void Position_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.OverlayPosition = CombinedPosition();
        _settingsService.Save(_settings);
        if (Application.Current is App app)
            app.Overlay?.RepositionIfVisible();
    }

    private OverlayPosition CombinedPosition()
    {
        bool top = VPosTop.IsChecked == true;
        if (HPosLeft.IsChecked   == true) return top ? OverlayPosition.TopLeft    : OverlayPosition.BottomLeft;
        if (HPosCenter.IsChecked == true) return top ? OverlayPosition.TopCenter  : OverlayPosition.BottomCenter;
        return                                   top ? OverlayPosition.TopRight   : OverlayPosition.BottomRight;
    }
}
