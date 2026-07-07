using System.Net;
using System.Net.Http.Headers;

namespace LocalDownloader.Core.Segments;

/// <summary>
/// Multi-connection, resumable download engine. Probes the server for Range support, splits
/// the resource into N segments when possible, downloads segments using a pool of N worker
/// "lanes" that pull from a shared queue (work stealing: an idle worker splits the largest
/// remaining in-flight segment rather than sitting idle), and periodically persists progress to
/// a `.task.json` sidecar so an interrupted download can resume from where it left off.
/// Falls back to a single-stream download when the server does not support Range requests
/// or the total size is unknown.
/// </summary>
public sealed class SegmentedDownloadEngine
{
    private readonly HttpClient _httpClient;
    private readonly SegmentProgressStore _progressStore;

    /// <summary>Minimum remaining bytes a running segment must have before it is eligible to be
    /// split off for a newly idle worker.</summary>
    private const long MinSplitRemainingBytes = 4 * 1024 * 1024;

    /// <summary>How long the whole task can go with zero global progress, while every worker is
    /// in a rate-limit backoff wait, before the task is declared failed.</summary>
    private static readonly TimeSpan StallFailureWindow = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan MinBackoffDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(30);

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
        var workerCount = Math.Max(1, Math.Min(options.Connections, state.Segments.Count));
        var coordinator = new WorkStealingCoordinator(state, progress, throttle, workerCount);

        // Every worker beyond the number of not-yet-complete segments will simply find the
        // queue empty and immediately attempt a work-steal split, which is the desired behavior
        // for lanes that finish early on a job with few segments left.
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => RunWorkerAsync(uri, request, state, options, coordinator, cancellationToken))
            .ToArray();

        await Task.WhenAll(workers);

        cancellationToken.ThrowIfCancellationRequested();

        if (coordinator.FailureException is not null)
        {
            throw coordinator.FailureException;
        }

        await _progressStore.SaveAsync(state, CancellationToken.None);
    }

    private async Task RunWorkerAsync(
        Uri uri,
        DownloadRequest request,
        SegmentedTaskState state,
        SegmentedDownloadOptions options,
        WorkStealingCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (coordinator.FailureException is not null)
            {
                return;
            }

            var segment = coordinator.TakeNextSegment();
            if (segment is null)
            {
                // Nothing queued and nothing left to split off of: this worker is done.
                return;
            }

            await DownloadSegmentWithRetryAsync(uri, request, state, segment, options, coordinator, cancellationToken);
        }
    }

    /// <summary>
    /// Attempts one segment. Transient (non-rate-limit) failures retry in place a bounded number
    /// of times with the existing short exponential backoff. Rate-limit responses (403/418/429)
    /// or refused connections instead put the segment back on the shared queue and send this
    /// worker into a longer backoff (2s/4s/8s.../30s cap) before it goes looking for new work —
    /// this both frees the segment up for another (non-throttled) worker immediately and lets
    /// the throttled worker's own connection slot sit idle, which is the "auto-downgrade
    /// concurrency" behavior.
    /// </summary>
    private async Task DownloadSegmentWithRetryAsync(
        Uri uri,
        DownloadRequest request,
        SegmentedTaskState state,
        SegmentProgress segment,
        SegmentedDownloadOptions options,
        WorkStealingCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var transientAttempt = 0;

        if (segment.IsComplete)
        {
            coordinator.ReleaseSegment(segment);
            coordinator.NoteProgress();
            return;
        }

        try
        {
            var outcome = await DownloadSegmentAsync(uri, request, state, segment, coordinator, cancellationToken);
            coordinator.NoteProgress();

            if (outcome == SegmentOutcome.RateLimited)
            {
                var backoffStep = coordinator.RegisterRateLimitHit(segment);
                coordinator.RequeueSegment(segment);

                var delay = ComputeBackoffDelay(backoffStep);
                coordinator.EnterBackoff();
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                finally
                {
                    coordinator.ExitBackoff();
                }

                coordinator.CheckForStall();
                return;
            }

            // Completed or SplitAway: this worker's involvement with this segment object is over.
            coordinator.ReleaseSegment(segment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            coordinator.ReleaseSegment(segment);
            throw;
        }
        catch (Exception) when (++transientAttempt <= options.MaxRetriesPerSegment)
        {
            var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, transientAttempt - 1));
            await Task.Delay(delay, cancellationToken);
            coordinator.RequeueSegment(segment);
        }
        catch (Exception ex)
        {
            coordinator.ReleaseSegment(segment);
            coordinator.ReportFailure(ex);
        }
    }

    private static TimeSpan ComputeBackoffDelay(int step)
    {
        var seconds = MinBackoffDelay.TotalSeconds * Math.Pow(2, step - 1);
        seconds = Math.Min(seconds, MaxBackoffDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsRateLimited(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode is { } status && IsRateLimitStatus(status))
            {
                return true;
            }

            // Connection refused / reset surfaces as HttpRequestException without a status code
            // (HttpRequestError distinguishes this from a plain unsuccessful status code).
            if (httpEx.StatusCode is null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRateLimitStatus(HttpStatusCode status)
    {
        return status is HttpStatusCode.Forbidden // 403
            or (HttpStatusCode)418
            or HttpStatusCode.TooManyRequests; // 429
    }

    private enum SegmentOutcome
    {
        Completed,
        SplitAway,
        RateLimited
    }

    private async Task<SegmentOutcome> DownloadSegmentAsync(
        Uri uri,
        DownloadRequest request,
        SegmentedTaskState state,
        SegmentProgress segment,
        WorkStealingCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var rangeStart = segment.Start + segment.CompletedBytes;
        var rangeEnd = segment.End;
        if (rangeStart > rangeEnd)
        {
            return SegmentOutcome.Completed;
        }

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyRequestHeaders(requestMessage, request);
        requestMessage.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex) when (IsRateLimited(ex))
        {
            return SegmentOutcome.RateLimited;
        }

        using var _ = response;

        if (IsRateLimitStatus(response.StatusCode))
        {
            return SegmentOutcome.RateLimited;
        }

        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) when (IsRateLimited(ex))
        {
            return SegmentOutcome.RateLimited;
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(state.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        output.Seek(rangeStart, SeekOrigin.Begin);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            // Never write past the segment's current End: it may have been shrunk by a
            // work-stealing split that happened concurrently on another worker.
            var currentEnd = Volatile.Read(ref segment.EndUnsafe);
            var position = output.Position;
            var maxWritable = currentEnd - position + 1;

            if (maxWritable <= 0)
            {
                return SegmentOutcome.SplitAway;
            }

            var toWrite = (int)Math.Min(bytesRead, maxWritable);
            await output.WriteAsync(buffer.AsMemory(0, toWrite), cancellationToken);
            segment.CompletedBytes += toWrite;
            coordinator.NoteProgress();

            if (coordinator.ShouldPersist(toWrite))
            {
                await coordinator.PersistAsync(cancellationToken);
            }

            if (toWrite < bytesRead)
            {
                // We were writing right up to a newly-shrunk End: stop, the split owner
                // continues from here.
                return SegmentOutcome.SplitAway;
            }
        }

        return SegmentOutcome.Completed;
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

    /// <summary>
    /// Coordinates the shared segment queue, work-stealing splits, progress persistence, and
    /// the "all workers stalled in rate-limit backoff" failure detection for a single task's
    /// worker pool. All mutation of <see cref="SegmentedTaskState.Segments"/> happens under
    /// <see cref="_lock"/>.
    /// </summary>
    private sealed class WorkStealingCoordinator
    {
        private readonly SegmentedTaskState _state;
        private readonly IProgress<DownloadProgressSnapshot>? _progress;
        private readonly PersistThrottle _throttle;
        private readonly SegmentProgressStore _store = new();
        private readonly int _totalWorkers;
        private readonly object _lock = new();
        private readonly Queue<SegmentProgress> _queue = new();
        private readonly HashSet<SegmentProgress> _inFlight = new();
        private readonly Dictionary<SegmentProgress, int> _rateLimitHits = new();
        private int _workersInBackoff;
        private DateTimeOffset _lastGlobalProgressAt = DateTimeOffset.UtcNow;
        private long _lastGlobalCompletedBytes;

        public Exception? FailureException { get; private set; }

        public WorkStealingCoordinator(
            SegmentedTaskState state,
            IProgress<DownloadProgressSnapshot>? progress,
            PersistThrottle throttle,
            int totalWorkers)
        {
            _state = state;
            _progress = progress;
            _throttle = throttle;
            _totalWorkers = Math.Max(1, totalWorkers);
            _lastGlobalCompletedBytes = state.CompletedBytes;

            foreach (var segment in state.Segments.Where(s => !s.IsComplete))
            {
                _queue.Enqueue(segment);
            }
        }

        /// <summary>
        /// Returns the next segment for an idle worker to work on: either one waiting in the
        /// queue, or (if the queue is empty) half of the largest in-flight segment, split at its
        /// midpoint. Returns null when there is truly nothing left to do.
        /// </summary>
        public SegmentProgress? TakeNextSegment()
        {
            lock (_lock)
            {
                while (_queue.Count > 0)
                {
                    var candidate = _queue.Dequeue();
                    if (candidate.IsComplete)
                    {
                        continue;
                    }

                    _inFlight.Add(candidate);
                    return candidate;
                }

                return TrySplitLargestInFlightSegment_NoLock();
            }
        }

        private SegmentProgress? TrySplitLargestInFlightSegment_NoLock()
        {
            SegmentProgress? largest = null;
            long largestRemaining = 0;

            foreach (var segment in _inFlight)
            {
                var remaining = segment.End - (segment.Start + segment.CompletedBytes) + 1;
                if (remaining > largestRemaining)
                {
                    largestRemaining = remaining;
                    largest = segment;
                }
            }

            if (largest is null || largestRemaining <= MinSplitRemainingBytes)
            {
                return null;
            }

            // Split at the midpoint of the *remaining* (not-yet-downloaded) range.
            var remainingStart = largest.Start + largest.CompletedBytes;
            var remainingLength = largest.End - remainingStart + 1;
            var newSegmentStart = remainingStart + remainingLength / 2;

            var newSegment = new SegmentProgress
            {
                Start = newSegmentStart,
                End = largest.End,
                CompletedBytes = 0
            };

            // Shrink the original segment's End so its writer stops before the new segment's
            // territory. Volatile write so the writer thread observes it on its next buffer check.
            Volatile.Write(ref largest.EndUnsafe, newSegmentStart - 1);

            _state.Segments.Add(newSegment);
            _inFlight.Add(newSegment);
            return newSegment;
        }

        /// <summary>Worker is done with this segment (completed, or its tail was split away):
        /// remove it from the in-flight set so it is no longer a split candidate.</summary>
        public void ReleaseSegment(SegmentProgress segment)
        {
            lock (_lock)
            {
                _inFlight.Remove(segment);
                _rateLimitHits.Remove(segment);
            }
        }

        /// <summary>Puts a segment back on the shared queue (still in-flight, since it still has
        /// unfinished bytes) so any idle worker — including this one, on its next loop
        /// iteration — can pick it up again.</summary>
        public void RequeueSegment(SegmentProgress segment)
        {
            lock (_lock)
            {
                _queue.Enqueue(segment);
            }
        }

        /// <summary>Records another rate-limit hit against this segment and returns the new hit
        /// count, used to compute the escalating backoff delay (2s/4s/8s.../30s cap).</summary>
        public int RegisterRateLimitHit(SegmentProgress segment)
        {
            lock (_lock)
            {
                var next = _rateLimitHits.GetValueOrDefault(segment) + 1;
                _rateLimitHits[segment] = next;
                return next;
            }
        }

        public void NoteProgress()
        {
            lock (_lock)
            {
                var completed = _state.CompletedBytes;
                if (completed != _lastGlobalCompletedBytes)
                {
                    _lastGlobalCompletedBytes = completed;
                    _lastGlobalProgressAt = DateTimeOffset.UtcNow;
                }
            }
        }

        public bool ShouldPersist(int bytesJustWritten) => _throttle.ShouldPersist(bytesJustWritten);

        public async Task PersistAsync(CancellationToken cancellationToken)
        {
            await _store.SaveAsync(_state, CancellationToken.None);
            _progress?.Report(new DownloadProgressSnapshot(
                _state.Id, DownloadTaskStatus.Downloading, _state.CompletedBytes, _state.TotalBytes, _state.Segments.Count));
        }

        public void EnterBackoff()
        {
            lock (_lock)
            {
                _workersInBackoff++;
            }
        }

        public void ExitBackoff()
        {
            lock (_lock)
            {
                _workersInBackoff--;
            }
        }

        /// <summary>
        /// Called by a worker right after it wakes from a backoff wait. If every worker has been
        /// in backoff continuously and there has been zero global progress for
        /// <see cref="StallFailureWindow"/>, the whole task is declared failed.
        /// </summary>
        public void CheckForStall()
        {
            lock (_lock)
            {
                var allStalled = _workersInBackoff >= _totalWorkers;
                var elapsed = DateTimeOffset.UtcNow - _lastGlobalProgressAt;

                if (allStalled && elapsed >= StallFailureWindow && FailureException is null)
                {
                    FailureException = new SegmentedDownloadException(
                        $"Download stalled: no progress for {elapsed.TotalSeconds:0}s while all connections were rate-limited.");
                }
            }
        }

        public void ReportFailure(Exception ex)
        {
            lock (_lock)
            {
                FailureException ??= ex;
            }
        }
    }
}
