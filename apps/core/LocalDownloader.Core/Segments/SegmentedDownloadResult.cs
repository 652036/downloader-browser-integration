namespace LocalDownloader.Core.Segments;

public sealed record SegmentedDownloadResult(
    string Id,
    DownloadTaskStatus Status,
    string FilePath,
    long BytesWritten,
    bool SupportsRange,
    int SegmentCount);

/// <summary>
/// Thrown when a segmented download cannot complete after exhausting all retries.
/// </summary>
public sealed class SegmentedDownloadException : Exception
{
    public SegmentedDownloadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown internally to signal a cooperative pause (segment connections canceled, but state
/// preserved on disk for a later resume). Callers see this surface as a normal return with
/// <see cref="DownloadTaskStatus.Paused"/> status rather than as a thrown exception.
/// </summary>
internal sealed class DownloadPausedSignal : Exception
{
}
