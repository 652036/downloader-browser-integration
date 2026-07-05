using System.Text.Json.Serialization;

namespace LocalDownloader.Core;

/// <summary>
/// Shared JSON message contract passed across the extension &lt;-&gt; Host &lt;-&gt; App boundary.
/// Host performs no business parsing of this payload; it forwards frames verbatim between
/// stdio and the named pipe. This type exists for the App (and tests) to serialize/deserialize
/// the wire format described in the v2 design document.
/// </summary>
public sealed class IpcMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // download.create / download.returnToBrowser fields
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("suggestedFilename")]
    public string? SuggestedFilename { get; set; }

    [JsonPropertyName("referrer")]
    public string? Referrer { get; set; }

    [JsonPropertyName("cookieHeader")]
    public string? CookieHeader { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("fileSize")]
    public long? FileSize { get; set; }

    [JsonPropertyName("mime")]
    public string? Mime { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    // download.accepted
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    // download.error
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    // settings.value
    [JsonPropertyName("interceptExtensions")]
    public List<string>? InterceptExtensions { get; set; }

    [JsonPropertyName("interceptMimePrefixes")]
    public List<string>? InterceptMimePrefixes { get; set; }
}

/// <summary>
/// Well-known IPC message type discriminators shared by extension, Host and App.
/// </summary>
public static class IpcMessageType
{
    public const string DownloadCreate = "download.create";
    public const string DownloadAccepted = "download.accepted";
    public const string DownloadError = "download.error";
    public const string DownloadReturnToBrowser = "download.returnToBrowser";
    public const string SettingsGet = "settings.get";
    public const string SettingsValue = "settings.value";
}

/// <summary>
/// Well-known error codes carried on <see cref="IpcMessageType.DownloadError"/> messages.
/// </summary>
public static class IpcErrorCode
{
    public const string UnsupportedUrl = "unsupported_url";
    public const string HostProtocolError = "host_protocol_error";
    public const string NetworkError = "network_error";
    public const string DiskError = "disk_error";
    public const string PermissionDenied = "permission_denied";
}
