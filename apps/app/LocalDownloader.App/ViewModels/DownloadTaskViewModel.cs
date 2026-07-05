using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalDownloader.App.Services;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.ViewModels;

/// <summary>
/// Presentation wrapper around a <see cref="ManagedDownloadTask"/> for the main window's
/// DataGrid. Speed and ETA are derived from the delta between successive progress updates,
/// which the caller applies via <see cref="ApplyFrom"/> at the throttled UI refresh cadence.
/// </summary>
public sealed partial class DownloadTaskViewModel : ObservableObject
{
    private DateTimeOffset _lastSampleAt = DateTimeOffset.UtcNow;
    private long _lastSampleBytes;

    public string Id { get; }

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressDisplay))]
    [NotifyPropertyChangedFor(nameof(EtaDisplay))]
    private long? _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressDisplay))]
    [NotifyPropertyChangedFor(nameof(EtaDisplay))]
    private long _bytesDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(SpeedDisplay))]
    [NotifyPropertyChangedFor(nameof(EtaDisplay))]
    private DownloadTaskStatus _status;

    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedDisplay))]
    [NotifyPropertyChangedFor(nameof(EtaDisplay))]
    private double _bytesPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private string? _errorMessage;

    public DownloadTaskViewModel(ManagedDownloadTask task)
    {
        Id = task.Id;
        ApplyFrom(task);
    }

    public void ApplyFrom(ManagedDownloadTask task)
    {
        FileName = task.FilePath is not null
            ? Path.GetFileName(task.FilePath)
            : task.Request.SuggestedFilename ?? UrlFileName(task.Request.Url) ?? "download.bin";
        TotalBytes = task.TotalBytes;
        FilePath = task.FilePath;
        Status = task.Status;
        ErrorMessage = task.ErrorMessage;

        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _lastSampleAt).TotalSeconds;
        if (elapsed > 0 && task.Status == DownloadTaskStatus.Downloading)
        {
            var delta = task.BytesDownloaded - _lastSampleBytes;
            BytesPerSecond = delta > 0 ? delta / elapsed : BytesPerSecond;
        }
        else if (task.Status != DownloadTaskStatus.Downloading)
        {
            BytesPerSecond = 0;
        }

        _lastSampleAt = now;
        _lastSampleBytes = task.BytesDownloaded;
        BytesDownloaded = task.BytesDownloaded;
    }

    public string SizeDisplay => TotalBytes is > 0 ? FormatBytes(TotalBytes.Value) : "Unknown";

    public double ProgressPercent => TotalBytes is > 0 ? Math.Min(100.0, BytesDownloaded * 100.0 / TotalBytes.Value) : 0;

    public string ProgressDisplay => TotalBytes is > 0
        ? $"{ProgressPercent:0.0}% ({FormatBytes(BytesDownloaded)} / {FormatBytes(TotalBytes.Value)})"
        : FormatBytes(BytesDownloaded);

    public string SpeedDisplay => Status == DownloadTaskStatus.Downloading && BytesPerSecond > 0
        ? $"{FormatBytes((long)BytesPerSecond)}/s"
        : "-";

    public string EtaDisplay
    {
        get
        {
            if (Status != DownloadTaskStatus.Downloading || BytesPerSecond <= 0 || TotalBytes is not > 0)
            {
                return "-";
            }

            var remaining = TotalBytes.Value - BytesDownloaded;
            if (remaining <= 0)
            {
                return "0s";
            }

            var seconds = remaining / BytesPerSecond;
            return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
        }
    }

    public string StatusDisplay => Status switch
    {
        DownloadTaskStatus.Queued => "Queued",
        DownloadTaskStatus.Probing => "Probing",
        DownloadTaskStatus.Downloading => "Downloading",
        DownloadTaskStatus.Paused => "Paused",
        DownloadTaskStatus.Completed => "Completed",
        DownloadTaskStatus.Failed => $"Failed{(ErrorMessage is null ? "" : $": {ErrorMessage}")}",
        DownloadTaskStatus.Canceled => "Canceled",
        _ => Status.ToString()
    };

    private static string? UrlFileName(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.0} {units[unitIndex]}";
    }
}
