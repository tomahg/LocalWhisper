using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class ModelPage : Page
{
    private readonly ServerApiService _api;
    private readonly AppSettings _settings;

    public ModelPage()
    {
        InitializeComponent();
        _api      = App.Services.GetRequiredService<ServerApiService>();
        _settings = App.Services.GetRequiredService<AppSettings>();

        Loaded += async (_, _) => await LoadModelsAsync();
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var models = await _api.GetModelsAsync(_settings.ServerUrl);
            ModelComboBox.Items.Clear();
            foreach (var m in models)
                ModelComboBox.Items.Add(m);

            ModelComboBox.DisplayMemberPath = "Name";

            // Select the currently loaded model
            for (int i = 0; i < ModelComboBox.Items.Count; i++)
            {
                if (models[i].Loaded)
                {
                    ModelComboBox.SelectedIndex = i;
                    break;
                }
            }

            SwitchButton.IsEnabled = ModelComboBox.Items.Count > 0;
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message  = $"Kunne ikke hente modeller: {ex.Message}";
            StatusBar.IsOpen   = true;
        }
    }

    private async void SwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelComboBox.SelectedItem is not ModelInfo model) return;

        SwitchButton.IsEnabled = false;
        StatusBar.IsOpen = false;

        try
        {
            await _api.SwitchModelAsync(_settings.ServerUrl, model.Id);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message  = $"Byttet til {model.Name}";
            StatusBar.IsOpen   = true;
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message  = $"Feil: {ex.Message}";
            StatusBar.IsOpen   = true;
        }
        finally
        {
            SwitchButton.IsEnabled = true;
        }
    }
}
