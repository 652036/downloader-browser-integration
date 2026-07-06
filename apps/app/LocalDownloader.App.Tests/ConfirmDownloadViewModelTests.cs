using LocalDownloader.App.ViewModels;
using LocalDownloader.Core;

namespace LocalDownloader.App.Tests;

public sealed class ConfirmDownloadViewModelTests
{
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
}
