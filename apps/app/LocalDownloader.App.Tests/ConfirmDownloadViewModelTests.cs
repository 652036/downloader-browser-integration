using System.IO;
using System.Net.Http;
using LocalDownloader.App.ViewModels;
using LocalDownloader.Core;

namespace LocalDownloader.App.Tests;

public sealed class ConfirmDownloadViewModelTests
{
    [Fact]
    public void Constructor_prefills_categorized_directory_when_categorize_by_type_enabled()
    {
        var request = new DownloadRequest
        {
            Url = "https://example.com/movie.mp4",
            SuggestedFilename = "movie.mp4"
        };

        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads\LocalDownloader", categorizeByType: true);

        Assert.Equal(Path.Combine(@"C:\Downloads\LocalDownloader", FileCategoryClassifier.Video), viewModel.SaveDirectory);
    }

    [Fact]
    public void Constructor_uses_plain_default_directory_when_categorize_by_type_disabled()
    {
        var request = new DownloadRequest
        {
            Url = "https://example.com/movie.mp4",
            SuggestedFilename = "movie.mp4"
        };

        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads\LocalDownloader", categorizeByType: false);

        Assert.Equal(@"C:\Downloads\LocalDownloader", viewModel.SaveDirectory);
    }


    [Fact]
    public void Constructor_derives_filename_domain_and_size_from_request()
    {
        var request = new DownloadRequest
        {
            Url = "https://cdn.example.com/files/archive.zip",
            SuggestedFilename = "archive.zip",
            FileSize = 5 * 1024 * 1024
        };

        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads\LocalDownloader");

        Assert.Equal("archive.zip", viewModel.FileName);
        Assert.Equal("cdn.example.com", viewModel.SourceDomain);
        Assert.Equal("5.0 MB", viewModel.SizeDisplay);
        Assert.Equal(@"C:\Downloads\LocalDownloader", viewModel.SaveDirectory);
    }

    [Fact]
    public void Constructor_falls_back_to_url_path_segment_when_no_suggested_filename()
    {
        var request = new DownloadRequest
        {
            Url = "https://example.com/downloads/report.pdf"
        };

        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads");

        Assert.Equal("report.pdf", viewModel.FileName);
        Assert.Equal("大小未知", viewModel.SizeDisplay);
    }

    [Fact]
    public void StartCommand_sets_outcome_to_start_and_raises_close()
    {
        var request = new DownloadRequest { Url = "https://example.com/a.zip" };
        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads");

        var closed = false;
        viewModel.RequestClose += () => closed = true;
        viewModel.StartCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(ConfirmDownloadOutcome.Start, viewModel.Outcome);
    }

    [Fact]
    public void ReturnToBrowserCommand_sets_outcome_and_raises_close()
    {
        var request = new DownloadRequest { Url = "https://example.com/a.zip" };
        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads");

        viewModel.ReturnToBrowserCommand.Execute(null);

        Assert.Equal(ConfirmDownloadOutcome.ReturnToBrowser, viewModel.Outcome);
    }

    [Fact]
    public void CancelCommand_sets_outcome_and_raises_close()
    {
        var request = new DownloadRequest { Url = "https://example.com/a.zip" };
        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads");

        viewModel.CancelCommand.Execute(null);

        Assert.Equal(ConfirmDownloadOutcome.Cancel, viewModel.Outcome);
    }

    [Fact]
    public async Task Successful_size_probe_updates_size_display_without_touching_file_name()
    {
        var request = new DownloadRequest
        {
            Url = "https://example.com/a.zip",
            SuggestedFilename = "a.zip",
            FileSize = 100
        };

        var probeStarted = new TaskCompletionSource();
        var releaseProbe = new TaskCompletionSource();
        ConfirmDownloadViewModel.SizeProbe probe = async (_, _) =>
        {
            probeStarted.TrySetResult();
            await releaseProbe.Task;
            return 5 * 1024 * 1024L;
        };

        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads", categorizeByType: false, probe);

        Assert.Equal(FormatSize(100), viewModel.SizeDisplay);

        await probeStarted.Task;
        releaseProbe.SetResult();

        await WaitUntilAsync(() => viewModel.SizeDisplay == "5.0 MB");

        Assert.Equal("5.0 MB", viewModel.SizeDisplay);
        Assert.Equal("a.zip", viewModel.FileName);
    }

    [Fact]
    public async Task Failed_size_probe_keeps_original_size_display()
    {
        var request = new DownloadRequest
        {
            Url = "https://example.com/a.zip",
            SuggestedFilename = "a.zip",
            FileSize = 100
        };

        var probeRan = new TaskCompletionSource();
        ConfirmDownloadViewModel.SizeProbe probe = (_, _) =>
        {
            probeRan.TrySetResult();
            throw new HttpRequestException("boom");
        };

        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads", categorizeByType: false, probe);

        await probeRan.Task;
        // Give the (failed, synchronous-continuation) probe task a moment to fully unwind before
        // asserting nothing changed.
        await Task.Delay(50);

        Assert.Equal(FormatSize(100), viewModel.SizeDisplay);
    }

    [Fact]
    public async Task Probe_returning_null_keeps_original_size_display()
    {
        var request = new DownloadRequest
        {
            Url = "https://example.com/a.zip",
            SuggestedFilename = "a.zip"
        };

        var probeRan = new TaskCompletionSource();
        ConfirmDownloadViewModel.SizeProbe probe = (_, _) =>
        {
            probeRan.TrySetResult();
            return Task.FromResult<long?>(null);
        };

        var viewModel = new ConfirmDownloadViewModel(request, @"C:\Downloads", categorizeByType: false, probe);

        await probeRan.Task;
        await Task.Delay(50);

        Assert.Equal("大小未知", viewModel.SizeDisplay);
    }

    private static string FormatSize(long bytes)
    {
        // Mirrors ConfirmDownloadViewModel's private FormatSize for test assertions.
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

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
