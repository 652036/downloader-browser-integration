namespace LocalDownloader.Core.Segments;

/// <summary>
/// Point-in-time progress snapshot reported to callers (e.g. a WPF ViewModel) while a
/// segmented download is running.
/// </summary>
public sealed record DownloadProgressSnapshot(
    string Id,
    DownloadTaskStatus Status,
    long BytesDownloaded,
    long? TotalBytes,
    int SegmentCount);
