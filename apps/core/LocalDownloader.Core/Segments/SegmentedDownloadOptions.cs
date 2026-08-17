namespace LocalDownloader.Core.Segments;

public sealed class SegmentedDownloadOptions
{
    /// <summary>Number of concurrent connections requested per task (1-32, default 8).</summary>
    public int Connections { get; init; } = SegmentPlanner.DefaultConnections;

    /// <summary>Directory that receives the final downloaded file.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Number of automatic retries per segment on transient failure.</summary>
    public int MaxRetriesPerSegment { get; init; } = 3;

    /// <summary>Base delay for exponential backoff between segment retries (doubles each attempt).</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Minimum interval between sidecar `.task.json` persistence writes.</summary>
    public TimeSpan PersistInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Minimum newly-written bytes (per task) between sidecar persistence writes.</summary>
    public long PersistByteInterval { get; init; } = 1024 * 1024;

    /// <summary>
    /// When set, resume from this existing file's sidecar (`.task.json` / `.part`) instead of
    /// recomputing a path from <c>SuggestedFilename</c>. Used so Content-Disposition renames
    /// and a persisted <c>task.FilePath</c> are honored on resume.
    /// </summary>
    public string? ResumeFilePath { get; init; }

    /// <summary>How long the whole task can go with zero global progress, while every worker is
    /// in a rate-limit backoff wait, before the task is declared failed.</summary>
    public TimeSpan StallFailureWindow { get; init; } = TimeSpan.FromSeconds(60);
}
