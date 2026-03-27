using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LocalWhisper.Models;
using LocalWhisper.Services;

namespace LocalWhisper.Pages;

public sealed partial class LlmPage : Page
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private bool _loading;

    public LlmPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        _loading = true;

        EnabledToggle.IsOn            = _settings.LlmEnabled;
        BackendComboBox.SelectedIndex = (int)_settings.LlmBackend;
        BaseUrlBox.Text               = _settings.LlmBaseUrl;
        ApiKeyBox.Password            = _settings.LlmApiKey;
        ModelBox.Text                 = _settings.LlmModel;
        SystemRoleToggle.IsOn         = _settings.LlmUseSystemRole;
        TimeoutBox.Value              = _settings.LlmTimeoutSec;
        PromptBox.Text                = _settings.LlmPrompt;

        UpdateSectionVisibility();

        _loading = false;
    }

    private void UpdateSectionVisibility()
    {
        BackendSection.Visibility = _settings.LlmEnabled ? Visibility.Visible : Visibility.Collapsed;
        PromptsSection.Visibility = _settings.LlmEnabled ? Visibility.Visible : Visibility.Collapsed;

        var showUrl = _settings.LlmBackend is not LlmBackend.OpenAI
                                           and not LlmBackend.Claude;
        BaseUrlRow.Visibility = showUrl ? Visibility.Visible : Visibility.Collapsed;

        BaseUrlDescText.Text = _settings.LlmBackend == LlmBackend.AzureOpenAI
            ? "Full URL inkl. deployment og api-version"
            : "Base URL til tjenesten";
    }

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LlmEnabled = EnabledToggle.IsOn;
        UpdateSectionVisibility();
        _settingsService.Save(_settings);
    }

    private void Backend_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.LlmBackend = (LlmBackend)BackendComboBox.SelectedIndex;
        UpdateSectionVisibility();
        _settingsService.Save(_settings);
    }

    private void BaseUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LlmBaseUrl = BaseUrlBox.Text.TrimEnd('/');
        _settingsService.Save(_settings);
    }

    private void ApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LlmApiKey = ApiKeyBox.Password;
        _settingsService.Save(_settings);
    }

    private void SystemRole_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LlmUseSystemRole = SystemRoleToggle.IsOn;
        _settingsService.Save(_settings);
    }

    private void Model_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LlmModel = ModelBox.Text.Trim();
        _settingsService.Save(_settings);
    }

    private void Timeout_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading) return;
        if (double.IsNaN(e.NewValue)) return;
        _settings.LlmTimeoutSec = (int)e.NewValue;
        _settingsService.Save(_settings);
    }

    private void Prompt_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LlmPrompt = PromptBox.Text;
        _settingsService.Save(_settings);
    }
}
