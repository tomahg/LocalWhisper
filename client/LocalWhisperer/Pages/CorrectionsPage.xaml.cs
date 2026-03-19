using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using LocalWhisperer.Models;
using LocalWhisperer.Services;

namespace LocalWhisperer.Pages;

public sealed partial class CorrectionsPage : Page
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private CorrectionEntry? _editingEntry;

    public CorrectionsPage()
    {
        InitializeComponent();
        _settings        = App.Services.GetRequiredService<AppSettings>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        RebuildList();
    }

    // -------------------------------------------------------------------------
    // Add
    // -------------------------------------------------------------------------

    private void AddCorrection_Click(object sender, RoutedEventArgs e) => TryAdd();

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) TryAdd();
    }

    private void TryAdd()
    {
        var wrong   = WrongBox.Text;
        var correct = CorrectBox.Text;
        if (string.IsNullOrEmpty(wrong)) return;

        _settings.Corrections.Add(new CorrectionEntry { Wrong = wrong, Correct = correct });
        _settingsService.Save(_settings);

        WrongBox.Text   = "";
        CorrectBox.Text = "";
        WrongBox.Focus(FocusState.Programmatic);

        RebuildList();
    }

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    private void RebuildList()
    {
        CorrectionsList.Children.Clear();
        ListDivider.Visibility = _settings.Corrections.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var sorted = _settings.Corrections
            .OrderBy(c => c.Wrong, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var entry = sorted[i];

            if (i > 0)
                CorrectionsList.Children.Add(new Border
                {
                    Height     = 1,
                    Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
                });

            CorrectionsList.Children.Add(entry == _editingEntry
                ? BuildEditRow(entry)
                : BuildDisplayRow(entry));
        }
    }

    private UIElement BuildDisplayRow(CorrectionEntry entry)
    {
        var grid = new Grid { Padding = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var wrongLabel = new TextBlock
        {
            Text              = entry.Wrong,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping      = TextWrapping.NoWrap,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(wrongLabel, 0);

        var arrow = new TextBlock
        {
            Text              = "→",
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 8, 0),
            Foreground        = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(arrow, 1);

        var correctLabel = new TextBlock
        {
            Text              = string.IsNullOrEmpty(entry.Correct) ? "(slett)" : entry.Correct,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping      = TextWrapping.NoWrap,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = string.IsNullOrEmpty(entry.Correct)
                ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
        Grid.SetColumn(correctLabel, 2);

        var editBtn = new Button
        {
            Content           = new FontIcon { Glyph = "\uE70F", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
            Padding           = new Thickness(6),
        };
        Grid.SetColumn(editBtn, 3);
        editBtn.Click += (_, _) => { _editingEntry = entry; RebuildList(); };

        var deleteBtn = new Button
        {
            Content           = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
            Padding           = new Thickness(6),
        };
        Grid.SetColumn(deleteBtn, 4);
        deleteBtn.Click += (_, _) =>
        {
            if (_editingEntry == entry) _editingEntry = null;
            _settings.Corrections.Remove(entry);
            _settingsService.Save(_settings);
            RebuildList();
        };

        grid.Children.Add(wrongLabel);
        grid.Children.Add(arrow);
        grid.Children.Add(correctLabel);
        grid.Children.Add(editBtn);
        grid.Children.Add(deleteBtn);

        return grid;
    }

    private UIElement BuildEditRow(CorrectionEntry entry)
    {
        var grid = new Grid { Padding = new Thickness(16, 8, 16, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var wrongBox = new TextBox { Text = entry.Wrong };
        Grid.SetColumn(wrongBox, 0);

        var arrow = new TextBlock
        {
            Text              = "→",
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 8, 0),
            Foreground        = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(arrow, 1);

        var correctBox = new TextBox { Text = entry.Correct };
        Grid.SetColumn(correctBox, 2);

        var saveBtn = new Button
        {
            Content           = new FontIcon { Glyph = "\uE74E", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
            Padding           = new Thickness(6),
        };
        Grid.SetColumn(saveBtn, 3);

        var cancelBtn = new Button
        {
            Content           = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
            Padding           = new Thickness(6),
        };
        Grid.SetColumn(cancelBtn, 4);

        void Save()
        {
            if (!string.IsNullOrEmpty(wrongBox.Text))
            {
                entry.Wrong   = wrongBox.Text;
                entry.Correct = correctBox.Text;
                _settingsService.Save(_settings);
            }
            _editingEntry = null;
            RebuildList();
        }

        void Cancel() { _editingEntry = null; RebuildList(); }

        saveBtn.Click   += (_, _) => Save();
        cancelBtn.Click += (_, _) => Cancel();

        void OnKeyDown(object s, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)  Save();
            if (e.Key == Windows.System.VirtualKey.Escape) Cancel();
        }
        wrongBox.KeyDown   += OnKeyDown;
        correctBox.KeyDown += OnKeyDown;

        grid.Children.Add(wrongBox);
        grid.Children.Add(arrow);
        grid.Children.Add(correctBox);
        grid.Children.Add(saveBtn);
        grid.Children.Add(cancelBtn);

        return grid;
    }
}
