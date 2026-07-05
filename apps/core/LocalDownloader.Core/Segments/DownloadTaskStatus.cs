namespace LocalDownloader.Core.Segments;

/// <summary>
/// Task state machine: queued -&gt; probing -&gt; downloading &lt;-&gt; paused -&gt; completed / failed / canceled.
/// A failed task may be retried, which moves it back to queued.
/// </summary>
public enum DownloadTaskStatus
{
    Queued,
    Probing,
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled
}
