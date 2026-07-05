using System.IO;
using System.Text.Json.Serialization;

namespace LocalDownloader.App.Settings;

public sealed class AppSettings
{
    [JsonPropertyName("downloadDirectory")]
    public string DownloadDirectory { get; set; } = DefaultDownloadDirectory();

    [JsonPropertyName("connectionsPerTask")]
    public int ConnectionsPerTask { get; set; } = 8;

    [JsonPropertyName("maxConcurrentTasks")]
    public int MaxConcurrentTasks { get; set; } = 3;

    [JsonPropertyName("interceptExtensions")]
    public List<string> InterceptExtensions { get; set; } = DefaultInterceptExtensions();

    [JsonPropertyName("interceptMimePrefixes")]
    public List<string> InterceptMimePrefixes { get; set; } = DefaultInterceptMimePrefixes();

    [JsonPropertyName("launchAtStartup")]
    public bool LaunchAtStartup { get; set; }

    public static string DefaultDownloadDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads", "LocalDownloader");
    }

    public static List<string> DefaultInterceptExtensions() => new()
    {
        // Archives
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab",
        // Installers
        ".exe", ".msi", ".msix", ".apk", ".dmg", ".pkg", ".deb", ".rpm",
        // Video
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m4v",
        // Audio
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma",
        // Documents
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".epub", ".mobi",
        // Disc images and misc
        ".iso", ".img", ".bin", ".torrent", ".ttf", ".otf", ".psd"
    };

    public static List<string> DefaultInterceptMimePrefixes() => new()
    {
        "application/octet-stream",
        "application/x-msdownload",
        "application/x-msi",
        "application/zip",
        "application/x-7z-compressed",
        "application/x-rar-compressed",
        "application/gzip",
        "application/x-tar",
        "application/x-iso9660-image",
        "video/",
        "audio/",
        "application/pdf"
    };
}
