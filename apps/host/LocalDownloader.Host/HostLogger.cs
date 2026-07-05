namespace LocalDownloader.Host;

/// <summary>
/// Minimal append-only file logger. stdout is reserved exclusively for Native Messaging frames
/// (the browser reads it directly), so all diagnostics go to
/// %LOCALAPPDATA%\LocalDownloader\logs\host-yyyyMMdd.log instead of the console.
/// </summary>
public sealed class HostLogger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();

    public HostLogger()
        : this(DefaultLogDirectory())
    {
    }

    public HostLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, $"host-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public static string DefaultLogDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalDownloader", "logs");

    public void Log(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line);
            }
            catch (IOException)
            {
                // Logging is best-effort; never let a logging failure break the relay.
            }
        }
    }
}
