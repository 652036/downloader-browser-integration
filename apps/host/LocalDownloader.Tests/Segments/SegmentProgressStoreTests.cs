using LocalDownloader.Core.Segments;

namespace LocalDownloader.Tests.Segments;

public sealed class SegmentProgressStoreTests
{
    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_segment_state()
    {
        using var temp = new TempDirectory();
        var metadataPath = Path.Combine(temp.Path, "file.zip.task.json");
        var store = new SegmentProgressStore();

        var state = new SegmentedTaskState
        {
            Id = "task-1",
            Url = "https://example.com/file.zip",
            Status = "downloading",
            FilePath = Path.Combine(temp.Path, "file.zip"),
            PartPath = Path.Combine(temp.Path, "file.zip.part"),
            MetadataPath = metadataPath,
            TotalBytes = 1000,
            SupportsRange = true,
            Segments = new List<SegmentProgress>
            {
                new() { Start = 0, End = 499, CompletedBytes = 500 },
                new() { Start = 500, End = 999, CompletedBytes = 100 }
            },
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync(metadataPath, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(state.Id, loaded!.Id);
        Assert.Equal(state.TotalBytes, loaded.TotalBytes);
        Assert.True(loaded.SupportsRange);
        Assert.Equal(2, loaded.Segments.Count);
        Assert.Equal(500, loaded.Segments[0].CompletedBytes);
        Assert.True(loaded.Segments[0].IsComplete);
        Assert.Equal(100, loaded.Segments[1].CompletedBytes);
        Assert.False(loaded.Segments[1].IsComplete);
        Assert.Equal(600, loaded.CompletedBytes);
    }

    [Fact]
    public async Task LoadAsync_returns_null_when_file_missing()
    {
        using var temp = new TempDirectory();
        var store = new SegmentProgressStore();

        var loaded = await store.LoadAsync(Path.Combine(temp.Path, "missing.task.json"), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task SaveAsync_overwrites_existing_file_atomically()
    {
        using var temp = new TempDirectory();
        var metadataPath = Path.Combine(temp.Path, "file.zip.task.json");
        var store = new SegmentProgressStore();

        var state = new SegmentedTaskState
        {
            Id = "task-1",
            Url = "https://example.com/file.zip",
            FilePath = Path.Combine(temp.Path, "file.zip"),
            PartPath = Path.Combine(temp.Path, "file.zip.part"),
            MetadataPath = metadataPath,
            Segments = new List<SegmentProgress> { new() { Start = 0, End = 99, CompletedBytes = 50 } }
        };

        await store.SaveAsync(state, CancellationToken.None);
        state.Segments[0].CompletedBytes = 100;
        await store.SaveAsync(state, CancellationToken.None);

        var loaded = await store.LoadAsync(metadataPath, CancellationToken.None);
        Assert.Equal(100, loaded!.Segments[0].CompletedBytes);
        Assert.False(File.Exists($"{metadataPath}.tmp"));
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
