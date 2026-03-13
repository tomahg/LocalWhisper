using System.Text.Json;
using LocalWhisperer.Models;
using Windows.Storage;

namespace LocalWhisperer.Services;

/// <summary>
/// Persists AppSettings to JSON in the MSIX local app data folder.
/// </summary>
public class SettingsService
{
    private const string FileName = "settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            var path = GetPath();
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try { File.WriteAllText(GetPath(), JsonSerializer.Serialize(settings, JsonOptions)); }
        catch { }
    }

    private static string GetPath() =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
}
