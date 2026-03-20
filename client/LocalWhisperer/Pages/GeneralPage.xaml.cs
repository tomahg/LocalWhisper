using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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
        InjectToggle.IsOn = _settings.InjectTextDirectly;
        AutoCopyToggle.IsOn = _settings.AutoCopyToClipboard;
        AutoCopyRow.Visibility = _settings.InjectTextDirectly ? Visibility.Collapsed : Visibility.Visible;
        SegmentPrefixComboBox.SelectedIndex = (int)_settings.SegmentPrefix;
        SilenceSuffixComboBox.SelectedIndex = (int)_settings.SilenceSuffix;
        if (_settings.InjectionMethod == InjectionMethod.Paste)
            MethodPaste.IsChecked = true;
        else
            MethodType.IsChecked = true;
        _loading = false;
    }

    private void Inject_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.InjectTextDirectly = InjectToggle.IsOn;
        AutoCopyRow.Visibility = InjectToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        _settingsService.Save(_settings);
    }

    private void AutoCopy_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoCopyToClipboard = AutoCopyToggle.IsOn;
        _settingsService.Save(_settings);
    }

    private void SegmentPrefix_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.SegmentPrefix = (SegmentPrefixMode)SegmentPrefixComboBox.SelectedIndex;
        _settingsService.Save(_settings);
    }

    private void SilenceSuffix_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.SilenceSuffix = (SilenceSuffixMode)SilenceSuffixComboBox.SelectedIndex;
        _settingsService.Save(_settings);
    }

    private void Method_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.InjectionMethod = (RadioButton)sender == MethodPaste
            ? InjectionMethod.Paste
            : InjectionMethod.Type;
        _settingsService.Save(_settings);
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.Exit();
    }
}
