using System.Net;
using System.Net.Http.Headers;

namespace LocalDownloader.Core.Segments;

public sealed record ProbeResult(
    bool SupportsRange,
    long? TotalBytes,
    string? ContentDispositionFilename,
    string? ContentType);

/// <summary>
/// Probes a URL with a zero-length Range request to discover whether the server supports
/// byte-range requests and, if so, the total resource size.
/// </summary>
public static class DownloadProbe
{
    public static async Task<ProbeResult> ProbeAsync(
        HttpClient httpClient,
        Uri uri,
        Action<HttpRequestMessage> configureRequest,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        configureRequest(request);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var filename = ExtractContentDispositionFilename(response.Content.Headers.ContentDisposition);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { } contentRange)
        {
            var total = contentRange.HasLength ? contentRange.Length : null;
            return new ProbeResult(SupportsRange: true, TotalBytes: total, filename, contentType);
        }

        response.EnsureSuccessStatusCode();

        // Server ignored the Range header and returned the full body (200 OK).
        var contentLength = response.Content.Headers.ContentLength;
        return new ProbeResult(SupportsRange: false, TotalBytes: contentLength, filename, contentType);
    }

    private static string? ExtractContentDispositionFilename(ContentDispositionHeaderValue? header)
    {
        if (header is null)
        {
            return null;
        }

        var name = header.FileNameStar;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = header.FileName;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return name.Trim('"');
    }
}
