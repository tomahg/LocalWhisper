using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using LocalWhisper.Models;
using LocalWhisper.Services;

namespace LocalWhisper.Pages;

public sealed partial class HotkeyPage : Page
{
    private readonly AppSettings     _settings;
    private readonly SettingsService _settingsService;
    private readonly HotkeyService   _hotkey;
    private bool _capturing;
    private int  _capturePeakModifiers;  // all modifier bits held at the same time (peak)
    private int  _captureCurrentHeld;    // modifier bits still physically held right now

    public HotkeyPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();
        _hotkey          = App.Services.GetRequiredService<HotkeyService>();

        UpdateHotkeyBadges(_settings.HotkeyDisplayName);

        IsTabStop = true; // needed to receive KeyDown / KeyUp
    }

    private void ChangeButton_Click(object sender, RoutedEventArgs e)
    {
        _hotkey.Suspend();
        _capturing            = true;
        _capturePeakModifiers = 0;
        _captureCurrentHeld   = 0;
        CapturingBar.IsOpen   = true;
        ChangeButton.IsEnabled = false;
        Focus(FocusState.Programmatic);
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;

        if (e.Key == VirtualKey.Escape)
        {
            StopCapturing();
            return;
        }

        int modBit = ModifierBit(e.Key);
        if (modBit != 0)
        {
            // A modifier was pressed — accumulate and show live preview
            _captureCurrentHeld  |= modBit;
            _capturePeakModifiers |= modBit;
            UpdateHotkeyBadges(BuildDisplayName(_capturePeakModifiers, null));
            return;
        }

        // Non-modifier key pressed — finalize immediately with held modifiers
        SaveAndApply((int)e.Key, _captureCurrentHeld, VirtualKeyToName(e.Key));
        e.Handled = true;
    }

    private void Page_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;

        int modBit = ModifierBit(e.Key);
        if (modBit == 0) return;

        _captureCurrentHeld &= ~modBit;

        if (_captureCurrentHeld == 0 && _capturePeakModifiers != 0)
        {
            // All modifiers released — save peak combination as modifier-only hotkey
            SaveAndApply(0, _capturePeakModifiers, null);
        }
    }

    private void SaveAndApply(int vk, int modifiers, string? mainKeyName)
    {
        var displayName = BuildDisplayName(modifiers, mainKeyName);
        _settings.HotkeyVirtualKey  = vk;
        _settings.HotkeyModifiers   = modifiers;
        _settings.HotkeyDisplayName = displayName;
        _settingsService.Save(_settings);
        _hotkey.Update(vk, modifiers);
        UpdateHotkeyBadges(displayName);
        StopCapturing();
    }

    private void StopCapturing()
    {
        _hotkey.Resume();
        _capturing            = false;
        _capturePeakModifiers = 0;
        _captureCurrentHeld   = 0;
        CapturingBar.IsOpen   = false;
        ChangeButton.IsEnabled = true;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static int ModifierBit(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => 1,
        VirtualKey.Shift   or VirtualKey.LeftShift   or VirtualKey.RightShift   => 2,
        VirtualKey.Menu    or VirtualKey.LeftMenu    or VirtualKey.RightMenu    => 4,
        VirtualKey.LeftWindows or VirtualKey.RightWindows                        => 8,
        _ => 0
    };

    private static string BuildDisplayName(int modifiers, string? mainKey)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 1) != 0) parts.Add("Ctrl");
        if ((modifiers & 2) != 0) parts.Add("Shift");
        if ((modifiers & 4) != 0) parts.Add("Alt");
        if ((modifiers & 8) != 0) parts.Add("Win");
        if (mainKey != null) parts.Add(mainKey);
        return string.Join("+", parts);
    }

    private void UpdateHotkeyBadges(string displayName)
    {
        HotkeyBadges.Children.Clear();
        var accentBrush    = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var separatorBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var keys = displayName.Split('+');
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0)
                HotkeyBadges.Children.Add(new TextBlock
                {
                    Text              = "+",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground        = separatorBrush,
                    FontSize          = 13,
                });

            HotkeyBadges.Children.Add(new Border
            {
                Background   = accentBrush,
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(5),
                Padding      = new Microsoft.UI.Xaml.Thickness(10, 5, 10, 5),
                Child        = new TextBlock
                {
                    Text       = keys[i],
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize   = 14,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                },
            });
        }
    }

    private static string VirtualKeyToName(VirtualKey key) => key switch
    {
        >= VirtualKey.F1 and <= VirtualKey.F24 =>
            $"F{(int)key - (int)VirtualKey.F1 + 1}",
        >= VirtualKey.Number0 and <= VirtualKey.Number9 =>
            ((int)key - (int)VirtualKey.Number0).ToString(),
        >= VirtualKey.A and <= VirtualKey.Z =>
            key.ToString(),
        VirtualKey.Space    => "Space",
        VirtualKey.Tab      => "Tab",
        VirtualKey.Insert   => "Insert",
        VirtualKey.Delete   => "Delete",
        VirtualKey.Home     => "Home",
        VirtualKey.End      => "End",
        VirtualKey.PageUp   => "PageUp",
        VirtualKey.PageDown => "PageDown",
        VirtualKey.Left     => "←",
        VirtualKey.Right    => "→",
        VirtualKey.Up       => "↑",
        VirtualKey.Down     => "↓",
        _                   => $"Key({(int)key})",
    };
}
