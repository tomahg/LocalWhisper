using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using LocalWhisper.Models;
using LocalWhisper.Services;

namespace LocalWhisper.Pages;

public sealed partial class StopPhrasesPage : Page
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private string? _editingPhrase;

    public StopPhrasesPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        RebuildList();
    }

    // -------------------------------------------------------------------------
    // Add
    // -------------------------------------------------------------------------

    private void Add_Click(object sender, RoutedEventArgs e) => TryAdd();

    private void PhraseBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) TryAdd();
    }

    private void TryAdd()
    {
        var phrase = PhraseBox.Text.Trim();
        if (string.IsNullOrEmpty(phrase)) return;
        if (_settings.StopPhrases.Contains(phrase, StringComparer.CurrentCultureIgnoreCase)) return;

        _settings.StopPhrases.Add(phrase);
        _settingsService.Save(_settings);

        PhraseBox.Text = "";
        PhraseBox.Focus(FocusState.Programmatic);

        RebuildList();
    }

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    private void RebuildList()
    {
        PhraseList.Children.Clear();
        ListDivider.Visibility = _settings.StopPhrases.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var sorted = _settings.StopPhrases
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var phrase = sorted[i];

            if (i > 0)
                PhraseList.Children.Add(new Border
                {
                    Height     = 1,
                    Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                });

            PhraseList.Children.Add(phrase == _editingPhrase
                ? BuildEditRow(phrase)
                : BuildDisplayRow(phrase));
        }
    }

    private UIElement BuildDisplayRow(string phrase)
    {
        var grid = new Grid { Padding = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text              = phrase,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping      = TextWrapping.NoWrap,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 0);

        var editBtn = new Button
        {
            Content           = new FontIcon { Glyph = "\uE70F", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
            Padding           = new Thickness(6),
        };
        Grid.SetColumn(editBtn, 1);
        editBtn.Click += (_, _) => { _editingPhrase = phrase; RebuildList(); };

        var deleteBtn = new Button
        {
            Content           = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
            Padding           = new Thickness(6),
        };
        Grid.SetColumn(deleteBtn, 2);
        deleteBtn.Click += (_, _) =>
        {
            if (_editingPhrase == phrase) _editingPhrase = null;
            _settings.StopPhrases.Remove(phrase);
            _settingsService.Save(_settings);
            RebuildList();
        };

        grid.Children.Add(label);
        grid.Children.Add(editBtn);
        grid.Children.Add(deleteBtn);

        return grid;
    }

    private UIElement BuildEditRow(string phrase)
    {
        var grid = new Grid { Padding = new Thickness(16, 8, 16, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox { Text = phrase };
        Grid.SetColumn(box, 0);

        var saveBtn = new Button
        {
            Content           = new FontIcon { Glyph = "\uE74E", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
            Padding           = new Thickness(6),
        };
        Grid.SetColumn(saveBtn, 1);

        var cancelBtn = new Button
        {
            Content           = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
            Padding           = new Thickness(6),
        };
        Grid.SetColumn(cancelBtn, 2);

        void Save()
        {
            var newPhrase = box.Text.Trim();
            if (!string.IsNullOrEmpty(newPhrase))
            {
                var idx = _settings.StopPhrases.IndexOf(phrase);
                if (idx >= 0) _settings.StopPhrases[idx] = newPhrase;
                _settingsService.Save(_settings);
            }
            _editingPhrase = null;
            RebuildList();
        }

        void Cancel() { _editingPhrase = null; RebuildList(); }

        saveBtn.Click   += (_, _) => Save();
        cancelBtn.Click += (_, _) => Cancel();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)  Save();
            if (e.Key == Windows.System.VirtualKey.Escape) Cancel();
        };

        grid.Children.Add(box);
        grid.Children.Add(saveBtn);
        grid.Children.Add(cancelBtn);

        return grid;
    }
}
