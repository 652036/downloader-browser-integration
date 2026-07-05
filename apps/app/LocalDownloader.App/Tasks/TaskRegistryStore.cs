using System.IO;
using System.Text.Json;

namespace LocalDownloader.App.Tasks;

/// <summary>
/// Loads and atomically persists the full task list at %APPDATA%\LocalDownloader\tasks.json.
/// </summary>
public sealed class TaskRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _tasksPath;
    private readonly object _lock = new();

    public TaskRegistryStore()
        : this(DefaultTasksPath())
    {
    }

    public TaskRegistryStore(string tasksPath)
    {
        _tasksPath = tasksPath;
    }

    public static string DefaultTasksPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LocalDownloader",
            "tasks.json");

    public List<PersistedTaskRecord> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_tasksPath))
            {
                return new List<PersistedTaskRecord>();
            }

            try
            {
                var json = File.ReadAllText(_tasksPath);
                var records = JsonSerializer.Deserialize<List<PersistedTaskRecord>>(json, JsonOptions);
                return records ?? new List<PersistedTaskRecord>();
            }
            catch (JsonException)
            {
                return new List<PersistedTaskRecord>();
            }
        }
    }

    public void Save(IReadOnlyList<PersistedTaskRecord> records)
    {
        lock (_lock)
        {
            var directory = Path.GetDirectoryName(_tasksPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = $"{_tasksPath}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(records, JsonOptions));

            if (File.Exists(_tasksPath))
            {
                File.Delete(_tasksPath);
            }

            File.Move(tempPath, _tasksPath);
        }
    }
}
