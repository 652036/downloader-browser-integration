using System.IO;
using LocalDownloader.App.Settings;
using LocalDownloader.App.ViewModels;

namespace LocalDownloader.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Save_persists_clamped_connections_and_normalized_extensions()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var viewModel = new SettingsViewModel(store)
        {
            ConnectionsPerTask = 999,
            MaxConcurrentTasks = 0,
            InterceptExtensionsText = "zip\n.exe\n\nMP4\n.zip",
            DownloadDirectory = temp.Path
        };

        var closed = false;
        viewModel.RequestClose += () => closed = true;
        viewModel.SaveCommand.Execute(null);

        Assert.True(closed);

        var saved = store.Load();
        Assert.Equal(32, saved.ConnectionsPerTask);
        Assert.Equal(1, saved.MaxConcurrentTasks);
        Assert.Contains(".zip", saved.InterceptExtensions);
        Assert.Contains(".exe", saved.InterceptExtensions);
        // case-insensitive de-dup: "MP4" and no duplicate ".zip" entries
        Assert.Single(saved.InterceptExtensions, e => e.Equals(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.Single(saved.InterceptExtensions, e => e.Equals(".zip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cancel_raises_RequestClose_without_saving()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        var viewModel = new SettingsViewModel(store)
        {
            ConnectionsPerTask = 20
        };

        var closed = false;
        viewModel.RequestClose += () => closed = true;
        viewModel.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.False(File.Exists(path));
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
