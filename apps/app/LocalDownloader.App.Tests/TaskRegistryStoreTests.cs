using System.IO;
using LocalDownloader.App.Tasks;

namespace LocalDownloader.App.Tests;

public sealed class TaskRegistryStoreTests
{
    [Fact]
    public void Load_returns_empty_list_when_file_missing()
    {
        using var temp = new TempDirectory();
        var store = new TaskRegistryStore(Path.Combine(temp.Path, "tasks.json"));

        var records = store.Load();

        Assert.Empty(records);
    }

    [Fact]
    public void Save_then_Load_round_trips_task_records_without_cookie_field()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "tasks.json");
        var store = new TaskRegistryStore(path);

        var records = new List<PersistedTaskRecord>
        {
            new()
            {
                Id = "task-1",
                Url = "https://example.com/file.zip",
                SuggestedFilename = "file.zip",
                Status = "paused",
                FilePath = @"C:\Downloads\file.zip",
                TotalBytes = 1000,
                BytesDownloaded = 500,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        store.Save(records);

        var reloaded = store.Load();
        Assert.Single(reloaded);
        Assert.Equal("task-1", reloaded[0].Id);
        Assert.Equal("paused", reloaded[0].Status);
        Assert.Equal(500, reloaded[0].BytesDownloaded);

        var rawJson = File.ReadAllText(path);
        Assert.DoesNotContain("cookieHeader", rawJson, StringComparison.OrdinalIgnoreCase);
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
