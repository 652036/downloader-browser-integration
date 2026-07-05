using System.Security.Cryptography;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.Tests.Segments;

public sealed class SegmentedDownloadEngineTests
{
    [Fact]
    public async Task DownloadAsync_splits_into_segments_and_produces_byte_exact_file_when_range_supported()
    {
        var payload = RandomPayload(4 * 1024 * 1024 + 37);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions { OutputDirectory = temp.Path, Connections = 8 };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "seg-task",
            Url = server.Url,
            SuggestedFilename = "file.bin"
        };

        var result = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.True(result.SupportsRange);
        Assert.True(result.SegmentCount > 1);
        Assert.Equal(payload.Length, result.BytesWritten);

        var written = await File.ReadAllBytesAsync(result.FilePath);
        Assert.Equal(payload, written);
        Assert.False(File.Exists($"{result.FilePath}.part"));
        Assert.False(File.Exists($"{result.FilePath}.task.json"));
        Assert.True(server.RequestCount > 1);
    }

    [Fact]
    public async Task DownloadAsync_falls_back_to_single_stream_when_range_not_supported()
    {
        var payload = RandomPayload(2 * 1024 * 1024 + 11);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: false);
        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions { OutputDirectory = temp.Path, Connections = 8 };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "single-task",
            Url = server.Url,
            SuggestedFilename = "single.bin"
        };

        var result = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.False(result.SupportsRange);
        Assert.Equal(1, result.SegmentCount);
        Assert.Equal(payload.Length, result.BytesWritten);

        var written = await File.ReadAllBytesAsync(result.FilePath);
        Assert.Equal(payload, written);
    }

    [Fact]
    public async Task DownloadAsync_pausing_preserves_state_and_resume_completes_full_file()
    {
        var payload = RandomPayload(6 * 1024 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions { OutputDirectory = temp.Path, Connections = 4 };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "pausable-task",
            Url = server.Url,
            SuggestedFilename = "pausable.bin"
        };

        using var pauseCts = new CancellationTokenSource();
        pauseCts.CancelAfter(TimeSpan.FromMilliseconds(20));

        var firstResult = await engine.DownloadAsync(request, options, progress: null, pauseCts.Token);
        Assert.Equal(DownloadTaskStatus.Paused, firstResult.Status);

        var partPath = $"{Path.Combine(temp.Path, "pausable.bin")}.part";
        var metadataPath = $"{Path.Combine(temp.Path, "pausable.bin")}.task.json";
        Assert.True(File.Exists(partPath));
        Assert.True(File.Exists(metadataPath));

        // Resume with a fresh (non-canceled) token.
        var finalResult = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

        Assert.Equal(DownloadTaskStatus.Completed, finalResult.Status);
        Assert.Equal(payload.Length, finalResult.BytesWritten);

        var written = await File.ReadAllBytesAsync(finalResult.FilePath);
        Assert.Equal(payload, written);
    }

    [Fact]
    public async Task DownloadAsync_sends_cookie_referrer_and_user_agent_headers_to_server()
    {
        var payload = RandomPayload(1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions { OutputDirectory = temp.Path, Connections = 4 };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "header-task",
            Url = server.Url,
            SuggestedFilename = "header.bin",
            UserAgent = "LocalDownloaderTest/2.0",
            Referrer = "https://example.com/page",
            CookieHeader = "session=abc123; token=xyz"
        };

        await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

        Assert.Equal("LocalDownloaderTest/2.0", server.LastUserAgent);
        Assert.Equal("https://example.com/page", server.LastReferer);
        Assert.Equal("session=abc123; token=xyz", server.LastCookie);
    }

    [Fact]
    public async Task DownloadAsync_retries_segment_on_transient_failure_then_succeeds()
    {
        // Payload small enough to be a single segment so a mid-stream abort on the only
        // connection forces a retry that must succeed on the next attempt.
        var payload = RandomPayload(300 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.FailAfterBytes = 1024; // abort the very first request part-way through

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions
        {
            OutputDirectory = temp.Path,
            Connections = 1,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10)
        };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "retry-task",
            Url = server.Url,
            SuggestedFilename = "retry.bin"
        };

        // Only fail once: after the first (aborted) request, disable the fault.
        var engineTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            server.FailAfterBytes = 0;
        });

        var result = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);
        await engineTask;

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.Equal(payload.Length, result.BytesWritten);
        var written = await File.ReadAllBytesAsync(result.FilePath);
        Assert.Equal(payload, written);
    }

    private static byte[] RandomPayload(int length)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
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
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; some file handles may still be closing async.
                }
            }
        }
    }
}
