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
        server.AddSlowRange(0, payload.Length / 4 - 1);
        server.SlowRangeDelayPerChunk = TimeSpan.FromMilliseconds(15);
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
        var progress = new Progress<DownloadProgressSnapshot>(snapshot =>
        {
            if (snapshot.Status == DownloadTaskStatus.Downloading && snapshot.BytesDownloaded > 0)
            {
                pauseCts.Cancel();
            }
        });

        var firstResult = await engine.DownloadAsync(request, options, progress, pauseCts.Token);
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

    [Fact]
    public async Task DownloadAsync_splits_a_slow_segment_via_work_stealing_and_produces_byte_exact_file()
    {
        // 40MB across 4 connections = 10MB per initial segment, well above the 4MB split
        // threshold. Make the first segment's range artificially slow so the other three
        // (fast) segments finish first, run out of queued work, and must steal a split of the
        // slow segment's still-large remaining tail rather than sit idle.
        var payload = RandomPayload(40 * 1024 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.AddSlowRange(0, payload.Length / 4 - 1);
        server.SlowRangeDelayPerChunk = TimeSpan.FromMilliseconds(15);

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions { OutputDirectory = temp.Path, Connections = 4 };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "work-stealing-task",
            Url = server.Url,
            SuggestedFilename = "work-stealing.bin"
        };

        var result = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.True(result.SegmentCount > 4, $"expected a split to have occurred (segments > 4), got {result.SegmentCount}");
        Assert.Equal(payload.Length, result.BytesWritten);

        var written = await File.ReadAllBytesAsync(result.FilePath);
        Assert.Equal(payload, written);
    }

    [Fact]
    public async Task DownloadAsync_completes_and_verifies_bytes_when_server_caps_concurrent_connections_with_429()
    {
        var payload = RandomPayload(2 * 1024 * 1024 + 123);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.MaxConcurrentConnections = 4;
        // Hold every accepted request open briefly so the 8 initial segment requests genuinely
        // race and overlap against the 4-connection cap, rather than some finishing (and
        // freeing a slot) before the others have even been dispatched.
        server.MinRequestDuration = TimeSpan.FromMilliseconds(200);

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions { OutputDirectory = temp.Path, Connections = 8 };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "throttled-task",
            Url = server.Url,
            SuggestedFilename = "throttled.bin"
        };

        var result = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.Equal(payload.Length, result.BytesWritten);

        var written = await File.ReadAllBytesAsync(result.FilePath);
        Assert.Equal(payload, written);
        Assert.True(server.RejectedCount > 0, "expected at least one request to have been rejected with 429");
    }

    [Fact]
    public async Task DownloadAsync_cancel_mid_split_then_resume_produces_byte_exact_file()
    {
        var payload = RandomPayload(40 * 1024 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.AddSlowRange(0, payload.Length / 4 - 1);
        server.SlowRangeDelayPerChunk = TimeSpan.FromMilliseconds(15);

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions { OutputDirectory = temp.Path, Connections = 4 };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "split-then-cancel-task",
            Url = server.Url,
            SuggestedFilename = "split-then-cancel.bin"
        };

        using var pauseCts = new CancellationTokenSource();
        // Give the initial probe + preallocation time to finish, and the fast segments time to
        // complete and steal a split of the still-slow segment's tail, before pausing mid-flight.
        pauseCts.CancelAfter(TimeSpan.FromMilliseconds(800));

        var firstResult = await engine.DownloadAsync(request, options, progress: null, pauseCts.Token);
        Assert.Equal(DownloadTaskStatus.Paused, firstResult.Status);

        var partPath = $"{Path.Combine(temp.Path, "split-then-cancel.bin")}.part";
        var metadataPath = $"{Path.Combine(temp.Path, "split-then-cancel.bin")}.task.json";
        Assert.True(File.Exists(partPath));
        Assert.True(File.Exists(metadataPath));

        var finalResult = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

        Assert.Equal(DownloadTaskStatus.Completed, finalResult.Status);
        Assert.Equal(payload.Length, finalResult.BytesWritten);

        var written = await File.ReadAllBytesAsync(finalResult.FilePath);
        Assert.Equal(payload, written);
    }

    [Fact]
    public async Task DownloadAsync_resume_loads_sidecar_from_ResumeFilePath_not_suggested_name()
    {
        var payload = RandomPayload(512 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);

        var realPath = Path.Combine(temp.Path, "from-content-disposition.bin");
        var partPath = $"{realPath}.part";
        var metadataPath = $"{realPath}.task.json";

        await File.WriteAllBytesAsync(partPath, payload);
        var store = new SegmentProgressStore();
        await store.SaveAsync(new SegmentedTaskState
        {
            Id = "resume-path-task",
            Url = server.Url,
            Status = nameof(DownloadTaskStatus.Paused),
            FilePath = realPath,
            PartPath = partPath,
            MetadataPath = metadataPath,
            TotalBytes = payload.Length,
            SupportsRange = true,
            Segments = new List<SegmentProgress>
            {
                new() { Start = 0, End = payload.Length - 1, CompletedBytes = payload.Length / 2 }
            },
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var options = new SegmentedDownloadOptions
        {
            OutputDirectory = temp.Path,
            Connections = 2,
            ResumeFilePath = realPath
        };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "resume-path-task",
            Url = server.Url,
            SuggestedFilename = "other-name.bin"
        };

        var result = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.Equal(realPath, result.FilePath);
        Assert.False(File.Exists(Path.Combine(temp.Path, "other-name.bin")));
        Assert.False(File.Exists($"{Path.Combine(temp.Path, "other-name.bin")}.task.json"));
        var written = await File.ReadAllBytesAsync(result.FilePath);
        Assert.Equal(payload, written);
    }

    [Fact]
    public async Task DownloadAsync_fails_on_403_after_limited_retries_without_looping()
    {
        var payload = RandomPayload(512 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.ForcedStatusCode = 403;
        server.AllowSuccessfulRequests = 1; // let the probe succeed

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions
        {
            OutputDirectory = temp.Path,
            Connections = 2,
            MaxRetriesPerSegment = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10)
        };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "forbidden-task",
            Url = server.Url,
            SuggestedFilename = "forbidden.bin"
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            engine.DownloadAsync(request, options, progress: null, CancellationToken.None));

        // probe + initial attempt + 2 retries; must not spin.
        Assert.InRange(server.RequestCount, 2, 8);
    }

    [Fact]
    public async Task DownloadAsync_stalls_when_all_workers_stay_rate_limited()
    {
        var payload = RandomPayload(512 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.ForcedStatusCode = 429;
        server.AllowSuccessfulRequests = 1;

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions
        {
            OutputDirectory = temp.Path,
            Connections = 2,
            StallFailureWindow = TimeSpan.FromMilliseconds(100)
        };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "stall-task",
            Url = server.Url,
            SuggestedFilename = "stall.bin"
        };

        var ex = await Assert.ThrowsAsync<SegmentedDownloadException>(() =>
            engine.DownloadAsync(request, options, progress: null, CancellationToken.None));
        Assert.Contains("stalled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequeueSegment_releases_in_flight_so_segment_is_not_stolen()
    {
        var segment = new SegmentProgress { Start = 0, End = 99, CompletedBytes = 10 };
        var state = new SegmentedTaskState
        {
            Id = "requeue-unit",
            FilePath = "unused.bin",
            PartPath = "unused.bin.part",
            MetadataPath = "unused.bin.task.json",
            Segments = new List<SegmentProgress> { segment }
        };

        var coordinator = new SegmentedDownloadEngine.WorkStealingCoordinator(state, totalWorkers: 1);
        var taken = coordinator.TakeNextSegment();
        Assert.Same(segment, taken);
        Assert.True(coordinator.IsInFlight(segment));

        coordinator.RequeueSegment(segment);

        Assert.False(coordinator.IsInFlight(segment));
        Assert.Equal(1, coordinator.QueuedCount);
    }

    [Fact]
    public async Task DownloadAsync_fails_when_206_instance_length_differs_from_stale_probe()
    {
        var payload = RandomPayload(3 * 1024 * 1024);
        var staleProbeSize = 2 * 1024 * 1024;
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.ProbeInstanceLength = staleProbeSize;

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions
        {
            OutputDirectory = temp.Path,
            Connections = 4,
            MaxRetriesPerSegment = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10)
        };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "stale-probe-task",
            Url = server.Url,
            SuggestedFilename = "stale-probe.bin"
        };

        var ex = await Assert.ThrowsAsync<SegmentedDownloadException>(() =>
            engine.DownloadAsync(request, options, progress: null, CancellationToken.None));

        Assert.Contains("Size mismatch / stale probe", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(staleProbeSize.ToString(), ex.Message);
        Assert.Contains(payload.Length.ToString(), ex.Message);
        Assert.False(File.Exists(Path.Combine(temp.Path, "stale-probe.bin")));
    }

    [Fact]
    public async Task DownloadAsync_fails_when_206_body_is_shorter_than_content_range()
    {
        var payload = RandomPayload(512 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.TruncateRangeBodyBytes = 1024;

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions
        {
            OutputDirectory = temp.Path,
            Connections = 2,
            MaxRetriesPerSegment = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10)
        };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "short-206-task",
            Url = server.Url,
            SuggestedFilename = "short-206.bin"
        };

        var ex = await Assert.ThrowsAsync<SegmentedDownloadException>(() =>
            engine.DownloadAsync(request, options, progress: null, CancellationToken.None));

        Assert.Contains("shorter than Content-Range", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(temp.Path, "short-206.bin")));
    }

    [Fact]
    public async Task DownloadAsync_single_stream_fails_when_get_content_length_differs_from_probe()
    {
        var payload = RandomPayload(512 * 1024);
        var staleProbeSize = 256 * 1024;
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.ProbeInstanceLength = staleProbeSize;

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions
        {
            OutputDirectory = temp.Path,
            Connections = 1,
            MaxRetriesPerSegment = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10)
        };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "single-stale-probe-task",
            Url = server.Url,
            SuggestedFilename = "single-stale.bin"
        };

        var ex = await Assert.ThrowsAsync<SegmentedDownloadException>(() =>
            engine.DownloadAsync(request, options, progress: null, CancellationToken.None));

        Assert.Contains("Size mismatch / stale probe", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(temp.Path, "single-stale.bin")));
    }

    [Fact]
    public async Task DownloadAsync_succeeds_when_probe_and_206_instance_lengths_match()
    {
        var payload = RandomPayload(768 * 1024);
        await using var server = await RangeTestServer.StartAsync(payload, supportsRange: true);
        server.ProbeInstanceLength = payload.Length;
        server.GetInstanceLength = payload.Length;

        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new SegmentedDownloadEngine(client);
        var options = new SegmentedDownloadOptions { OutputDirectory = temp.Path, Connections = 3 };
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "consistent-size-task",
            Url = server.Url,
            SuggestedFilename = "consistent.bin"
        };

        var result = await engine.DownloadAsync(request, options, progress: null, CancellationToken.None);

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
