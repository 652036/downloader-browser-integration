using LocalDownloader.Core;

namespace LocalDownloader.Tests;

public sealed class DownloadRequestTests
{
    [Theory]
    [InlineData("http://example.com/file.zip")]
    [InlineData("https://example.com/file.zip")]
    public void TryValidate_accepts_http_and_https_urls(string url)
    {
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "abc",
            Url = url,
            SuggestedFilename = "file.zip"
        };

        var valid = DownloadRequestValidator.TryValidate(request, out var error);

        Assert.True(valid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("C:\\Windows\\win.ini")]
    public void TryValidate_rejects_non_http_urls(string url)
    {
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "abc",
            Url = url,
            SuggestedFilename = "file.zip"
        };

        var valid = DownloadRequestValidator.TryValidate(request, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Equal("unsupported_url", error.Code);
    }
}
