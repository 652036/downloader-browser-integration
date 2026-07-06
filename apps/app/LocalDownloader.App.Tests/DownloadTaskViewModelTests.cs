using LocalDownloader.App.Services;
using LocalDownloader.App.ViewModels;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.Tests;

public class DownloadTaskViewModelTests
{
    private static ManagedDownloadTask CreateTask(DownloadTaskStatus status, string? errorMessage = null)
    {
        return new ManagedDownloadTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Request = new DownloadRequest
            {
                Type = IpcMessageType.DownloadCreate,
                Url = "https://example.com/files/video.mp4"
            },
            Status = status,
            ErrorMessage = errorMessage
        };
    }

    [Theory]
    [InlineData(DownloadTaskStatus.Queued, "排队中")]
    [InlineData(DownloadTaskStatus.Probing, "连接中")]
    [InlineData(DownloadTaskStatus.Downloading, "下载中")]
    [InlineData(DownloadTaskStatus.Paused, "已暂停")]
    [InlineData(DownloadTaskStatus.Completed, "已完成")]
    [InlineData(DownloadTaskStatus.Canceled, "已取消")]
    public void StatusDisplay_maps_status_to_chinese(DownloadTaskStatus status, string expected)
    {
        var viewModel = new DownloadTaskViewModel(CreateTask(status));

        Assert.Equal(expected, viewModel.StatusDisplay);
    }

    [Fact]
    public void StatusDisplay_failed_appends_error_message()
    {
        var viewModel = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Failed, "连接被重置"));

        Assert.Equal("失败：连接被重置", viewModel.StatusDisplay);
        Assert.True(viewModel.IsFailed);
    }

    [Fact]
    public void StatusDisplay_failed_without_message_is_plain()
    {
        var viewModel = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Failed));

        Assert.Equal("失败", viewModel.StatusDisplay);
    }

    [Fact]
    public void ApplyFrom_copies_segment_count()
    {
        var task = CreateTask(DownloadTaskStatus.Downloading);
        task.SegmentCount = 8;

        var viewModel = new DownloadTaskViewModel(task);

        Assert.Equal(8, viewModel.SegmentCount);
        Assert.Equal("8 线程", viewModel.SegmentDisplay);
    }

    [Fact]
    public void SegmentDisplay_is_empty_when_not_downloading()
    {
        var task = CreateTask(DownloadTaskStatus.Paused);
        task.SegmentCount = 8;

        var viewModel = new DownloadTaskViewModel(task);

        Assert.Equal(string.Empty, viewModel.SegmentDisplay);
    }

    [Theory]
    [InlineData(8, "剩余 8 秒")]
    [InlineData(59, "剩余 59 秒")]
    [InlineData(60, "剩余 1 分 0 秒")]
    [InlineData(125, "剩余 2 分 5 秒")]
    [InlineData(3599, "剩余 59 分 59 秒")]
    [InlineData(3600, "剩余 01:00:00")]
    [InlineData(4000, "剩余 01:06:40")]
    public void FormatEta_uses_chinese_units(double seconds, string expected)
    {
        Assert.Equal(expected, DownloadTaskViewModel.FormatEta(seconds));
    }

    [Fact]
    public void FormatEta_returns_empty_for_invalid_input()
    {
        Assert.Equal(string.Empty, DownloadTaskViewModel.FormatEta(-1));
        Assert.Equal(string.Empty, DownloadTaskViewModel.FormatEta(double.NaN));
        Assert.Equal(string.Empty, DownloadTaskViewModel.FormatEta(double.PositiveInfinity));
    }

    [Fact]
    public void EtaDisplay_and_SpeedDisplay_are_empty_when_not_applicable()
    {
        var task = CreateTask(DownloadTaskStatus.Paused);
        task.TotalBytes = 1000;
        task.BytesDownloaded = 100;

        var viewModel = new DownloadTaskViewModel(task);

        Assert.Equal(string.Empty, viewModel.EtaDisplay);
        Assert.Equal(string.Empty, viewModel.SpeedDisplay);
    }

    [Fact]
    public void SizeDisplay_shows_chinese_unknown_when_total_is_missing()
    {
        var viewModel = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Queued));

        Assert.Equal("大小未知", viewModel.SizeDisplay);
    }

    [Theory]
    [InlineData(DownloadTaskStatus.Queued, true, false)]
    [InlineData(DownloadTaskStatus.Probing, true, false)]
    [InlineData(DownloadTaskStatus.Downloading, true, false)]
    [InlineData(DownloadTaskStatus.Paused, false, true)]
    [InlineData(DownloadTaskStatus.Failed, false, true)]
    [InlineData(DownloadTaskStatus.Completed, false, false)]
    [InlineData(DownloadTaskStatus.Canceled, false, false)]
    public void CanPause_and_CanResume_follow_status(DownloadTaskStatus status, bool canPause, bool canResume)
    {
        var viewModel = new DownloadTaskViewModel(CreateTask(status));

        Assert.Equal(canPause, viewModel.CanPause);
        Assert.Equal(canResume, viewModel.CanResume);
    }
}
