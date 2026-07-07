namespace LocalDownloader.Core;

/// <summary>
/// Classifies a file by extension into one of the download-directory categories used by
/// "按类型分类保存" (categorize-by-type saving): 压缩包 (archives), 程序 (programs/installers),
/// 视频 (video), 音乐 (audio), 文档 (documents), or 其他 (other, the catch-all for anything
/// unrecognized).
/// </summary>
public static class FileCategoryClassifier
{
    public const string Archives = "压缩包";
    public const string Programs = "程序";
    public const string Video = "视频";
    public const string Music = "音乐";
    public const string Documents = "文档";
    public const string Other = "其他";

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab"
    };

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".apk", ".dmg", ".pkg", ".deb", ".rpm"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m4v"
    };

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".epub", ".mobi"
    };

    /// <summary>Returns the Chinese category name for a file name (extension is all that
    /// matters; the rest of the name, if any, is ignored). Unknown or missing extensions
    /// classify as <see cref="Other"/>.</summary>
    public static string Classify(string? fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return Other;
        }

        if (ArchiveExtensions.Contains(extension))
        {
            return Archives;
        }

        if (ProgramExtensions.Contains(extension))
        {
            return Programs;
        }

        if (VideoExtensions.Contains(extension))
        {
            return Video;
        }

        if (MusicExtensions.Contains(extension))
        {
            return Music;
        }

        if (DocumentExtensions.Contains(extension))
        {
            return Documents;
        }

        return Other;
    }
}
