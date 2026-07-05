using LocalDownloader.Core;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.Services;

/// <summary>
/// Runtime state for a single download task tracked by <see cref="DownloadManagerService"/>.
/// Cookie header is held only here (in memory) and is never written to the task registry.
/// </summary>
public sealed class ManagedDownloadTask
{
    public required string Id { get; init; }

    public required DownloadRequest Request { get; init; }

    public DownloadTaskStatus Status { get; set; } = DownloadTaskStatus.Queued;

    public string? FilePath { get; set; }

    public long BytesDownloaded { get; set; }

    public long? TotalBytes { get; set; }

    public int SegmentCount { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Cancellation source for the currently running download attempt, if any. Canceling
    /// this requests a pause (segments stop, state is preserved) rather than an abort.</summary>
    internal CancellationTokenSource? RunCts { get; set; }

    /// <summary>Set once when the user cancels (not pauses) the task, to distinguish a permanent
    /// cancellation from a pause when the engine surfaces OperationCanceledException.</summary>
    internal bool IsCanceledByUser { get; set; }
}
