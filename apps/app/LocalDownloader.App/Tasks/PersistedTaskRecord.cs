using System.Text.Json.Serialization;

namespace LocalDownloader.App.Tasks;

/// <summary>
/// A single entry in the durable task registry (%APPDATA%\LocalDownloader\tasks.json).
/// Deliberately excludes cookieHeader: cookies live only in memory for the lifetime of the
/// running task object. If a task is resumed after an App restart and the site's cookie has
/// since expired, the resumed request simply fails and the user must re-initiate the download
/// from the browser (see design doc security boundary section).
/// </summary>
public sealed class PersistedTaskRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("suggestedFilename")]
    public string? SuggestedFilename { get; set; }

    [JsonPropertyName("referrer")]
    public string? Referrer { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "queued";

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("totalBytes")]
    public long? TotalBytes { get; set; }

    [JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}
