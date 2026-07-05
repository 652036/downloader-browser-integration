using System.IO;
using System.Text.Json;

namespace LocalDownloader.App.Settings;

/// <summary>
/// Loads and atomically persists <see cref="AppSettings"/> at
/// %APPDATA%\LocalDownloader\settings.json.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsStore()
        : this(DefaultSettingsPath())
    {
    }

    public SettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public static string DefaultAppDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalDownloader");

    public static string DefaultSettingsPath() => Path.Combine(DefaultAppDataDirectory(), "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_settingsPath}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));

        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }

        File.Move(tempPath, _settingsPath);
    }
}
