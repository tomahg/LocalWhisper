using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class HotkeyPage : Page
{
    private readonly AppSettings     _settings;
    private readonly SettingsService _settingsService;
    private readonly HotkeyService   _hotkey;
    private bool _capturing;

    public HotkeyPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();
        _hotkey          = App.Services.GetRequiredService<HotkeyService>();

        HotkeyLabel.Text = _settings.HotkeyDisplayName;

        IsTabStop = true; // needed to receive KeyDown
    }

    private void ChangeButton_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        CapturingBar.IsOpen = true;
        ChangeButton.IsEnabled = false;
        Focus(FocusState.Programmatic);
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;

        // Esc cancels
        if (e.Key == VirtualKey.Escape)
        {
            StopCapturing();
            return;
        }

        // Ignore lone modifier keys
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
                  or VirtualKey.LeftControl or VirtualKey.RightControl
                  or VirtualKey.LeftShift or VirtualKey.RightShift
                  or VirtualKey.LeftMenu or VirtualKey.RightMenu
                  or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return;

        var vk          = (int)e.Key;
        var displayName = VirtualKeyToName(e.Key);

        _settings.HotkeyVirtualKey  = vk;
        _settings.HotkeyDisplayName = displayName;
        _settingsService.Save(_settings);
        _hotkey.Update(vk);

        HotkeyLabel.Text = displayName;
        StopCapturing();
        e.Handled = true;
    }

    private void StopCapturing()
    {
        _capturing = false;
        CapturingBar.IsOpen    = false;
        ChangeButton.IsEnabled = true;
    }

private static string VirtualKeyToName(VirtualKey key) => key switch
    {
        >= VirtualKey.F1 and <= VirtualKey.F24 =>
            $"F{(int)key - (int)VirtualKey.F1 + 1}",
        >= VirtualKey.Number0 and <= VirtualKey.Number9 =>
            ((int)key - (int)VirtualKey.Number0).ToString(),
        >= VirtualKey.A and <= VirtualKey.Z =>
            key.ToString(),
        VirtualKey.Space      => "Space",
        VirtualKey.Tab        => "Tab",
        VirtualKey.Insert     => "Insert",
        VirtualKey.Delete     => "Delete",
        VirtualKey.Home       => "Home",
        VirtualKey.End        => "End",
        VirtualKey.PageUp     => "PageUp",
        VirtualKey.PageDown   => "PageDown",
        VirtualKey.Left       => "←",
        VirtualKey.Right      => "→",
        VirtualKey.Up         => "↑",
        VirtualKey.Down       => "↓",
        _                     => $"Key({(int)key})",
    };
}
