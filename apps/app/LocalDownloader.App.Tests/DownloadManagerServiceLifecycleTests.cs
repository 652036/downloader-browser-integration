using System.IO;
using System.Net.Http;
using LocalDownloader.App.Services;
using LocalDownloader.App.Settings;
using LocalDownloader.App.Tasks;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.Tests;

public sealed class DownloadManagerServiceLifecycleTests
{
    [Fact]
    public void PauseAll_marks_queued_tasks_paused_so_they_cannot_start()
    {
        using var temp = new TempDirectory();
        var settingsStore = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var settings = settingsStore.Load();
        settings.MaxConcurrentTasks = 0;
        settings.DownloadDirectory = temp.Path;
        settingsStore.Save(settings);

        var taskRegistryStore = new TaskRegistryStore(Path.Combine(temp.Path, "tasks.json"));
        var manager = new DownloadManagerService(new HttpClient(), settingsStore, taskRegistryStore);

        var task = manager.CreateTask(new DownloadRequest
        {
            Type = IpcMessageType.DownloadCreate,
            Url = "https://example.com/files/a.zip",
            SuggestedFilename = "a.zip"
        });

        Assert.Equal(DownloadTaskStatus.Queued, task.Status);

        manager.PauseAll();

        Assert.Equal(DownloadTaskStatus.Paused, task.Status);

        settings.MaxConcurrentTasks = 1;
        settingsStore.Save(settings);

        var other = manager.CreateTask(new DownloadRequest
        {
            Type = IpcMessageType.DownloadCreate,
            Url = "https://example.com/files/b.zip",
            SuggestedFilename = "b.zip"
        });

        Assert.Equal(DownloadTaskStatus.Paused, task.Status);
        Assert.NotEqual(DownloadTaskStatus.Paused, other.Status);
    }

    [Fact]
    public void RemoveTask_deletes_sidecars_even_when_not_deleting_completed_file()
    {
        using var temp = new TempDirectory();
        var filePath = Path.Combine(temp.Path, "keep.bin");
        var partPath = $"{filePath}.part";
        var metadataPath = $"{filePath}.task.json";
        File.WriteAllText(filePath, "completed");
        File.WriteAllText(partPath, "partial");
        File.WriteAllText(metadataPath, "{}");

        var settingsStore = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var taskRegistryStore = new TaskRegistryStore(Path.Combine(temp.Path, "tasks.json"));
        taskRegistryStore.Save(new[]
        {
            new PersistedTaskRecord
            {
                Id = "remove-1",
                Url = "https://example.com/keep.bin",
                Status = "paused",
                FilePath = filePath,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        });

        var manager = new DownloadManagerService(new HttpClient(), settingsStore, taskRegistryStore);
        manager.LoadPersistedTasks();

        manager.RemoveTask("remove-1", deleteFile: false);

        Assert.True(File.Exists(filePath));
        Assert.False(File.Exists(partPath));
        Assert.False(File.Exists(metadataPath));
        Assert.False(manager.TryGetTask("remove-1", out _));
    }

    [Fact]
    public void RemoveTask_deleteFile_true_also_removes_completed_file()
    {
        using var temp = new TempDirectory();
        var filePath = Path.Combine(temp.Path, "gone.bin");
        File.WriteAllText(filePath, "completed");
        File.WriteAllText($"{filePath}.part", "partial");
        File.WriteAllText($"{filePath}.task.json", "{}");

        var settingsStore = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var taskRegistryStore = new TaskRegistryStore(Path.Combine(temp.Path, "tasks.json"));
        taskRegistryStore.Save(new[]
        {
            new PersistedTaskRecord
            {
                Id = "remove-2",
                Url = "https://example.com/gone.bin",
                Status = "paused",
                FilePath = filePath,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        });

        var manager = new DownloadManagerService(new HttpClient(), settingsStore, taskRegistryStore);
        manager.LoadPersistedTasks();

        manager.RemoveTask("remove-2", deleteFile: true);

        Assert.False(File.Exists(filePath));
        Assert.False(File.Exists($"{filePath}.part"));
        Assert.False(File.Exists($"{filePath}.task.json"));
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
