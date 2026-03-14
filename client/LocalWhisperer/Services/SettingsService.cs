using System.Text.Json;
using System.Text.Json.Nodes;
using LocalWhisperer.Models;

namespace LocalWhisperer.Services;

/// <summary>
/// Persists AppSettings to JSON in the MSIX local app data folder.
/// On load, any properties present in appsettings.local.json (next to the .exe)
/// are applied as defaults — useful for local dev overrides without touching git.
/// </summary>
public class SettingsService
{
    private const string FileName      = "settings.json";
    private const string LocalOverride = "appsettings.local.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Load()
    {
        // Start from persisted user settings, or defaults
        AppSettings settings;
        try
        {
            var path = GetPath();
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { settings = new(); }

        // Apply local override file if present (not committed to git)
        try
        {
            var overridePath = Path.Combine(AppContext.BaseDirectory, LocalOverride);
            if (File.Exists(overridePath))
            {
                var node = JsonNode.Parse(File.ReadAllText(overridePath));
                if (node?["ServerUrl"]?.GetValue<string>() is { Length: > 0 } url)
                    settings.ServerUrl = url;
            }
        }
        catch { }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        try { File.WriteAllText(GetPath(), JsonSerializer.Serialize(settings, JsonOptions)); }
        catch { }
    }

    private static string GetPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LocalWhisperer");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, FileName);
    }
}
