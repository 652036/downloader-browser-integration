using LocalDownloader.Host;

namespace LocalDownloader.Tests;

public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData("../secret.txt", "secret.txt")]
    [InlineData("..\\secret.txt", "secret.txt")]
    [InlineData("folder/sub/report.pdf", "report.pdf")]
    public void Sanitize_strips_path_traversal(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input, "fallback.bin"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sanitize_uses_fallback_for_blank_names(string? input)
    {
        Assert.Equal("download.bin", FileNameSanitizer.Sanitize(input, "download.bin"));
    }

    [Theory]
    [InlineData("CON", "download.bin")]
    [InlineData("bad<name>.txt", "bad_name_.txt")]
    public void Sanitize_replaces_invalid_windows_names(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input, "download.bin"));
    }
}
