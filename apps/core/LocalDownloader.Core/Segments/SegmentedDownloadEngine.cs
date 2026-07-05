using System.Net;
using System.Net.Http.Headers;

namespace LocalDownloader.Core.Segments;

/// <summary>
/// Multi-connection, resumable download engine. Probes the server for Range support, splits
/// the resource into N segments when possible, downloads each segment on its own HTTP
/// connection into a preallocated `.part` file, and periodically persists progress to a
/// `.task.json` sidecar so an interrupted download can resume from where it left off.
/// Falls back to a single-stream download when the server does not support Range requests
/// or the total size is unknown.
/// </summary>
public sealed class SegmentedDownloadEngine
{
    private readonly HttpClient _httpClient;
    private readonly SegmentProgressStore _progressStore;

    public SegmentedDownloadEngine(HttpClient httpClient)
        : this(httpClient, new SegmentProgressStore())
    {
    }

    public SegmentedDownloadEngine(HttpClient httpClient, SegmentProgressStore progressStore)
    {
        _httpClient = httpClient;
        _progressStore = progressStore;
    }

    /// <summary>
    /// Runs (or resumes) a segmented download to completion, or until <paramref name="cancellationToken"/>
    /// requests a pause. A canceled <paramref name="cancellationToken"/> is treated as a pause request:
    /// segment connections stop, but the `.part`/`.task.json` state is preserved for later resume.
    /// </summary>
    public async Task<SegmentedDownloadResult> DownloadAsync(
        DownloadRequest request,
        SegmentedDownloadOptions options,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        if (!DownloadRequestValidator.TryValidate(request, out var validationError))
        {
            throw new DownloadRequestException(validationError!.Code, validationError.Message);
        }

        Directory.CreateDirectory(options.OutputDirectory);

        var uri = new Uri(request.Url!);
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!;

        var fallbackName = FileNameSanitizer.Sanitize(Path.GetFileName(uri.LocalPath), "download.bin");
        var initialName = FileNameSanitizer.Sanitize(request.SuggestedFilename, fallbackName);
        var finalPath = GetAvailablePath(Path.Combine(options.OutputDirectory, initialName));
        var partPath = $"{finalPath}.part";
        var metadataPath = $"{finalPath}.task.json";

        // Resume support: if a prior sidecar exists for this exact target path, reuse it.
        var existingState = await _progressStore.LoadAsync(metadataPath, cancellationToken);

        SegmentedTaskState state;
        if (existingState is not null && File.Exists(partPath))
        {
            state = existingState;
        }
        else
        {
            progress?.Report(new DownloadProgressSnapshot(id, DownloadTaskStatus.Probing, 0, request.FileSize, 0));

            var probe = await DownloadProbe.ProbeAsync(
                _httpClient,
                uri,
                req => ApplyRequestHeaders(req, request),
                cancellationToken);

            var probedName = FileNameSanitizer.Sanitize(probe.ContentDispositionFilename, initialName);
            if (!string.Equals(probedName, initialName, StringComparison.Ordinal))
            {
                finalPath = GetAvailablePath(Path.Combine(options.OutputDirectory, probedName));
                partPath = $"{finalPath}.part";
                metadataPath = $"{finalPath}.task.json";
            }

            var totalBytes = probe.TotalBytes ?? request.FileSize;
            var supportsRange = probe.SupportsRange && totalBytes is > 0;

            var segments = supportsRange
                ? SegmentPlanner.Plan(totalBytes!.Value, options.Connections)
                : new[] { new SegmentRange(0, Math.Max((totalBytes ?? 0) - 1, 0)) };

            state = new SegmentedTaskState
            {
                Id = id,
                Url = request.Url!,
                Status = nameof(DownloadTaskStatus.Downloading),
                FilePath = finalPath,
                PartPath = partPath,
                MetadataPath = metadataPath,
                TotalBytes = totalBytes,
                SupportsRange = supportsRange,
                Segments = segments.Select(s => new SegmentProgress { Start = s.Start, End = s.End, CompletedBytes = 0 }).ToList(),
                UpdatedAt = DateTimeOffset.UtcNow
            };

            PreallocateFile(partPath, supportsRange ? totalBytes : null);
            await _progressStore.SaveAsync(state, cancellationToken);
        }

        try
        {
            if (state.SupportsRange && state.Segments.Count > 1)
            {
                await DownloadSegmentedAsync(request, state, options, progress, cancellationToken);
            }
            else
            {
                await DownloadSingleStreamAsync(request, state, options, progress, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Status = nameof(DownloadTaskStatus.Paused);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await _progressStore.SaveAsync(state, CancellationToken.None);

            progress?.Report(new DownloadProgressSnapshot(id, DownloadTaskStatus.Paused, state.CompletedBytes, state.TotalBytes, state.Segments.Count));
            return new SegmentedDownloadResult(id, DownloadTaskStatus.Paused, finalPath, state.CompletedBytes, state.SupportsRange, state.Segments.Count);
        }
        catch (Exception)
        {
            state.Status = nameof(DownloadTaskStatus.Failed);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await _progressStore.SaveAsync(state, CancellationToken.None);
            progress?.Report(new DownloadProgressSnapshot(id, DownloadTaskStatus.Failed, state.CompletedBytes, state.TotalBytes, state.Segments.Count));
            throw;
        }

        // All segments complete: verify and finalize.
        var expectedTotal = state.TotalBytes ?? state.CompletedBytes;
        if (state.CompletedBytes != expectedTotal)
        {
            state.Status = nameof(DownloadTaskStatus.Failed);
            await _progressStore.SaveAsync(state, CancellationToken.None);
            throw new SegmentedDownloadException(
                $"Byte count mismatch after download: expected {expectedTotal}, wrote {state.CompletedBytes}.");
        }

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        File.Move(partPath, finalPath);
        _progressStore.Delete(metadataPath);

        progress?.Report(new DownloadProgressSnapshot(id, DownloadTaskStatus.Completed, state.CompletedBytes, state.TotalBytes, state.Segments.Count));
        return new SegmentedDownloadResult(id, DownloadTaskStatus.Completed, finalPath, state.CompletedBytes, state.SupportsRange, state.Segments.Count);
    }

    private async Task DownloadSegmentedAsync(
        DownloadRequest request,
        SegmentedTaskState state,
        SegmentedDownloadOptions options,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(request.Url!);
        var throttle = new PersistThrottle(options.PersistInterval, options.PersistByteInterval);

        var tasks = state.Segments
            .Where(segment => !segment.IsComplete)
            .Select(segment => DownloadSegmentWithRetryAsync(uri, request, state, segment, options, progress, throttle, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
        await _progressStore.SaveAsync(state, CancellationToken.None);
    }

    private async Task DownloadSegmentWithRetryAsync(
        Uri uri,
        DownloadRequest request,
        SegmentedTaskState state,
        SegmentProgress segment,
        SegmentedDownloadOptions options,
        IProgress<DownloadProgressSnapshot>? progress,
        PersistThrottle throttle,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadSegmentAsync(uri, request, state, segment, options, progress, throttle, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < options.MaxRetriesPerSegment)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task DownloadSegmentAsync(
        Uri uri,
        DownloadRequest request,
        SegmentedTaskState state,
        SegmentProgress segment,
        SegmentedDownloadOptions options,
        IProgress<DownloadProgressSnapshot>? progress,
        PersistThrottle throttle,
        CancellationToken cancellationToken)
    {
        var rangeStart = segment.Start + segment.CompletedBytes;
        if (rangeStart > segment.End)
        {
            return;
        }

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyRequestHeaders(requestMessage, request);
        requestMessage.Headers.Range = new RangeHeaderValue(rangeStart, segment.End);

        using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(state.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        output.Seek(rangeStart, SeekOrigin.Begin);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            segment.CompletedBytes += bytesRead;

            if (throttle.ShouldPersist(bytesRead))
            {
                await _progressStore.SaveAsync(state, CancellationToken.None);
                progress?.Report(new DownloadProgressSnapshot(state.Id, DownloadTaskStatus.Downloading, state.CompletedBytes, state.TotalBytes, state.Segments.Count));
            }
        }
    }

    private async Task DownloadSingleStreamAsync(
        DownloadRequest request,
        SegmentedTaskState state,
        SegmentedDownloadOptions options,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(request.Url!);
        var segment = state.Segments[0];
        var throttle = new PersistThrottle(options.PersistInterval, options.PersistByteInterval);

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
                ApplyRequestHeaders(requestMessage, request);

                if (segment.CompletedBytes > 0)
                {
                    // Single-stream tasks cannot resume without Range support; caller already
                    // decided to restart from zero (see design: "从头重下"), so completed bytes
                    // are reset before this method is invoked for a resumed non-range task.
                    requestMessage.Headers.Range = new RangeHeaderValue(segment.CompletedBytes, null);
                }

                using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                if (state.TotalBytes is null)
                {
                    state.TotalBytes = response.Content.Headers.ContentLength is { } len && segment.CompletedBytes == 0
                        ? len
                        : state.TotalBytes;
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(state.PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                output.Seek(segment.CompletedBytes, SeekOrigin.Begin);

                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    segment.CompletedBytes += bytesRead;
                    segment.End = segment.Start + segment.CompletedBytes - 1;

                    if (throttle.ShouldPersist(bytesRead))
                    {
                        await _progressStore.SaveAsync(state, CancellationToken.None);
                        progress?.Report(new DownloadProgressSnapshot(state.Id, DownloadTaskStatus.Downloading, state.CompletedBytes, state.TotalBytes, state.Segments.Count));
                    }
                }

                state.TotalBytes ??= segment.CompletedBytes;
                await _progressStore.SaveAsync(state, CancellationToken.None);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < options.MaxRetriesPerSegment)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static void ApplyRequestHeaders(HttpRequestMessage requestMessage, DownloadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.UserAgent))
        {
            requestMessage.Headers.UserAgent.TryParseAdd(request.UserAgent);
        }

        if (Uri.TryCreate(request.Referrer, UriKind.Absolute, out var referrer) &&
            (referrer.Scheme == Uri.UriSchemeHttp || referrer.Scheme == Uri.UriSchemeHttps))
        {
            requestMessage.Headers.Referrer = referrer;
        }

        if (!string.IsNullOrWhiteSpace(request.CookieHeader))
        {
            requestMessage.Headers.TryAddWithoutValidation("Cookie", request.CookieHeader);
        }
    }

    private static void PreallocateFile(string partPath, long? totalBytes)
    {
        var directory = Path.GetDirectoryName(partPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        if (totalBytes is > 0)
        {
            stream.SetLength(totalBytes.Value);
        }
    }

    private static string GetAvailablePath(string path)
    {
        if (!File.Exists(path) && !File.Exists($"{path}.part") && !File.Exists($"{path}.task.json"))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < int.MaxValue; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !File.Exists($"{candidate}.part") && !File.Exists($"{candidate}.task.json"))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to find an available output file name.");
    }

    private sealed class PersistThrottle
    {
        private readonly TimeSpan _interval;
        private readonly long _byteInterval;
        private readonly object _lock = new();
        private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;
        private long _bytesSinceLastPersist;

        public PersistThrottle(TimeSpan interval, long byteInterval)
        {
            _interval = interval;
            _byteInterval = byteInterval;
        }

        public bool ShouldPersist(int bytesJustWritten)
        {
            lock (_lock)
            {
                _bytesSinceLastPersist += bytesJustWritten;
                var now = DateTimeOffset.UtcNow;
                if (now - _lastPersist < _interval && _bytesSinceLastPersist < _byteInterval)
                {
                    return false;
                }

                _lastPersist = now;
                _bytesSinceLastPersist = 0;
                return true;
            }
        }
    }
}
