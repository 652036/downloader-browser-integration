using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalDownloader.App.Services;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.ViewModels;

/// <summary>
/// Presentation wrapper around a <see cref="ManagedDownloadTask"/> for the main window's
/// card list. Speed and ETA are derived from the delta between successive progress updates,
/// which the caller applies via <see cref="ApplyFrom"/> at the throttled UI refresh cadence.
/// </summary>
public sealed partial class DownloadTaskViewModel : ObservableObject
{
    private DateTimeOffset _lastSampleAt = DateTimeOffset.UtcNow;
    private long _lastSampleBytes;

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeGlyph))]
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
    [NotifyPropertyChangedFor(nameof(SegmentDisplay))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentDisplay))]
    private int _segmentCount;

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
        SegmentCount = task.SegmentCount;

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

    public string SizeDisplay => TotalBytes is > 0 ? FormatBytes(TotalBytes.Value) : "大小未知";

    public double ProgressPercent => TotalBytes is > 0 ? Math.Min(100.0, BytesDownloaded * 100.0 / TotalBytes.Value) : 0;

    public string ProgressDisplay => TotalBytes is > 0
        ? $"{ProgressPercent:0}% · {FormatBytes(BytesDownloaded)} / {FormatBytes(TotalBytes.Value)}"
        : FormatBytes(BytesDownloaded);

    public string SpeedDisplay => Status == DownloadTaskStatus.Downloading && BytesPerSecond > 0
        ? $"{FormatBytes((long)BytesPerSecond)}/s"
        : string.Empty;

    public string EtaDisplay
    {
        get
        {
            if (Status != DownloadTaskStatus.Downloading || BytesPerSecond <= 0 || TotalBytes is not > 0)
            {
                return string.Empty;
            }

            var remaining = TotalBytes.Value - BytesDownloaded;
            if (remaining <= 0)
            {
                return FormatEta(0);
            }

            return FormatEta(remaining / BytesPerSecond);
        }
    }

    /// <summary>剩余时间中文格式：小于 60 秒“剩余 X 秒”，小于 1 小时“剩余 X 分 Y 秒”，更长“剩余 hh:mm:ss”。</summary>
    public static string FormatEta(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return string.Empty;
        }

        var total = (long)Math.Round(seconds);
        if (total < 60)
        {
            return $"剩余 {total} 秒";
        }

        if (total < 3600)
        {
            return $"剩余 {total / 60} 分 {total % 60} 秒";
        }

        return $"剩余 {TimeSpan.FromSeconds(total):hh\\:mm\\:ss}";
    }

    public string SegmentDisplay => Status == DownloadTaskStatus.Downloading && SegmentCount > 0
        ? $"{SegmentCount} 线程"
        : string.Empty;

    public string StatusDisplay => Status switch
    {
        DownloadTaskStatus.Queued => "排队中",
        DownloadTaskStatus.Probing => "连接中",
        DownloadTaskStatus.Downloading => "下载中",
        DownloadTaskStatus.Paused => "已暂停",
        DownloadTaskStatus.Completed => "已完成",
        DownloadTaskStatus.Failed => $"失败{(ErrorMessage is null ? "" : $"：{ErrorMessage}")}",
        DownloadTaskStatus.Canceled => "已取消",
        _ => Status.ToString()
    };

    public bool IsFailed => Status == DownloadTaskStatus.Failed;

    /// <summary>暂停按钮可见：任务正在推进（排队/连接/下载）。</summary>
    public bool CanPause => Status is DownloadTaskStatus.Queued
        or DownloadTaskStatus.Probing
        or DownloadTaskStatus.Downloading;

    /// <summary>继续按钮可见：任务停在可恢复状态（与 DownloadManagerService.ResumeTask 一致）。</summary>
    public bool CanResume => Status is DownloadTaskStatus.Paused or DownloadTaskStatus.Failed;

    public bool CanCancel => Status is not DownloadTaskStatus.Completed and not DownloadTaskStatus.Canceled;

    /// <summary>按扩展名给卡片选一个 Segoe MDL2 Assets 类型图标。</summary>
    public string TypeGlyph
    {
        get
        {
            var extension = Path.GetExtension(FileName).ToLowerInvariant();
            return extension switch
            {
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" => "",
                ".mp3" or ".flac" or ".wav" or ".aac" or ".ogg" or ".m4a" => "",
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "",
                ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" => "",
                ".exe" or ".msi" => "",
                _ => ""
            };
        }
    }

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
