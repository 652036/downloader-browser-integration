using System.IO;
using LocalDownloader.App.Settings;

namespace LocalDownloader.App.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));

        var settings = store.Load();

        Assert.Equal(8, settings.ConnectionsPerTask);
        Assert.Equal(3, settings.MaxConcurrentTasks);
        Assert.Contains(".zip", settings.InterceptExtensions);
        Assert.Contains("video/", settings.InterceptMimePrefixes);
        Assert.False(settings.LaunchAtStartup);
    }

    [Fact]
    public void Save_then_Load_round_trips_settings()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);

        var settings = store.Load();
        settings.ConnectionsPerTask = 16;
        settings.MaxConcurrentTasks = 5;
        settings.DownloadDirectory = @"D:\Downloads";
        settings.LaunchAtStartup = true;
        settings.InterceptExtensions.Add(".customext");
        store.Save(settings);

        var reloaded = store.Load();

        Assert.Equal(16, reloaded.ConnectionsPerTask);
        Assert.Equal(5, reloaded.MaxConcurrentTasks);
        Assert.Equal(@"D:\Downloads", reloaded.DownloadDirectory);
        Assert.True(reloaded.LaunchAtStartup);
        Assert.Contains(".customext", reloaded.InterceptExtensions);
        Assert.False(File.Exists($"{path}.tmp"));
    }

    [Fact]
    public void Load_returns_defaults_when_file_contains_invalid_json()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{not valid json");
        var store = new SettingsStore(path);

        var settings = store.Load();

        Assert.Equal(8, settings.ConnectionsPerTask);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
