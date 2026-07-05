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
}
