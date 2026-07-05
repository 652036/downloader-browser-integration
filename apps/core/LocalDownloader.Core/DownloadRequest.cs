using System.Text.Json.Serialization;

namespace LocalDownloader.Core;

public sealed class DownloadRequest
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

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
}

public sealed record DownloadValidationError(string Code, string Message);

public static class DownloadRequestValidator
{
    public static bool TryValidate(DownloadRequest? request, out DownloadValidationError? error)
    {
        if (request is null)
        {
            error = new DownloadValidationError("host_protocol_error", "Request body is missing.");
            return false;
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = new DownloadValidationError("unsupported_url", "Only http and https URLs are supported.");
            return false;
        }

        error = null;
        return true;
    }
}
