using LocalDownloader.App.Services;

namespace LocalDownloader.App.Tests;

public sealed class ClipboardUrlDetectorTests
{
    private static readonly string[] DefaultExtensions = { ".zip", ".exe", ".mp4", ".pdf" };

    [Fact]
    public void FindDownloadUrl_returns_null_for_empty_or_whitespace_text()
    {
        Assert.Null(ClipboardUrlDetector.FindDownloadUrl(null, DefaultExtensions));
        Assert.Null(ClipboardUrlDetector.FindDownloadUrl("", DefaultExtensions));
        Assert.Null(ClipboardUrlDetector.FindDownloadUrl("   ", DefaultExtensions));
    }

    [Fact]
    public void FindDownloadUrl_returns_null_when_no_intercept_extensions_configured()
    {
        var url = ClipboardUrlDetector.FindDownloadUrl("https://example.com/file.zip", Array.Empty<string>());
        Assert.Null(url);
    }

    [Fact]
    public void FindDownloadUrl_extracts_a_bare_matching_url()
    {
        var url = ClipboardUrlDetector.FindDownloadUrl("https://example.com/downloads/file.zip", DefaultExtensions);
        Assert.Equal("https://example.com/downloads/file.zip", url);
    }

    [Fact]
    public void FindDownloadUrl_extracts_url_embedded_in_prose()
    {
        var text = "下载地址在这里：https://cdn.example.com/setup.exe 请尽快下载，谢谢！";
        var url = ClipboardUrlDetector.FindDownloadUrl(text, DefaultExtensions);
        Assert.Equal("https://cdn.example.com/setup.exe", url);
    }

    [Theory]
    [InlineData("see https://example.com/file.zip.", "https://example.com/file.zip")]
    [InlineData("(https://example.com/file.zip)", "https://example.com/file.zip")]
    [InlineData("https://example.com/file.zip,", "https://example.com/file.zip")]
    [InlineData("\"https://example.com/file.zip\"", "https://example.com/file.zip")]
    public void FindDownloadUrl_trims_surrounding_punctuation(string text, string expected)
    {
        var url = ClipboardUrlDetector.FindDownloadUrl(text, DefaultExtensions);
        Assert.Equal(expected, url);
    }

    [Fact]
    public void FindDownloadUrl_ignores_urls_whose_extension_is_not_configured()
    {
        var url = ClipboardUrlDetector.FindDownloadUrl("https://example.com/page.html", DefaultExtensions);
        Assert.Null(url);
    }

    [Fact]
    public void FindDownloadUrl_ignores_non_http_schemes()
    {
        var url = ClipboardUrlDetector.FindDownloadUrl("ftp://example.com/file.zip", DefaultExtensions);
        Assert.Null(url);
    }

    [Fact]
    public void FindDownloadUrl_is_case_insensitive_on_extension()
    {
        var url = ClipboardUrlDetector.FindDownloadUrl("https://example.com/FILE.ZIP", DefaultExtensions);
        Assert.Equal("https://example.com/FILE.ZIP", url);
    }

    [Fact]
    public void FindDownloadUrl_returns_first_matching_url_when_multiple_present()
    {
        var text = "https://example.com/readme.html and https://example.com/file.zip and https://example.com/other.pdf";
        var url = ClipboardUrlDetector.FindDownloadUrl(text, DefaultExtensions);
        Assert.Equal("https://example.com/file.zip", url);
    }

    [Fact]
    public void FindDownloadUrl_returns_null_for_plain_text_without_any_url()
    {
        var url = ClipboardUrlDetector.FindDownloadUrl("hello world, this is just text", DefaultExtensions);
        Assert.Null(url);
    }
}
