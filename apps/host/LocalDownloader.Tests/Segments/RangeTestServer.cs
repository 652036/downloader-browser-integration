using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalDownloader.Tests.Segments;

/// <summary>
/// Minimal ASP.NET Core test server that serves a fixed in-memory payload, optionally
/// honoring HTTP Range requests, and records the last request's headers for assertions.
/// </summary>
public sealed class RangeTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public byte[] Payload { get; }

    public string Url { get; }

    public bool SupportsRange { get; set; }

    /// <summary>When &gt; 0, the server aborts the connection after streaming this many bytes for
    /// the *next* request only (then resets), to simulate a mid-transfer failure.</summary>
    public long FailAfterBytes { get; set; }

    public string? LastUserAgent { get; private set; }
    public string? LastReferer { get; private set; }
    public string? LastCookie { get; private set; }
    public int RequestCount { get; private set; }

    private RangeTestServer(WebApplication app, byte[] payload, string url)
    {
        _app = app;
        Payload = payload;
        Url = url;
    }

    public static async Task<RangeTestServer> StartAsync(byte[] payload, bool supportsRange = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        RangeTestServer? server = null;

        app.MapGet("/file.bin", async context =>
        {
            server!.RequestCount++;
            server.LastUserAgent = context.Request.Headers.UserAgent.ToString();
            server.LastReferer = context.Request.Headers.Referer.ToString();
            server.LastCookie = context.Request.Headers.Cookie.ToString();

            var body = server.Payload;
            var rangeHeader = context.Request.Headers.Range.ToString();

            if (server.SupportsRange && !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var (start, end) = ParseRange(rangeHeader, body.Length);
                var length = end - start + 1;

                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers.ContentRange = $"bytes {start}-{end}/{body.Length}";
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = length;

                await WriteBodyAsync(context, body, start, length, server.FailAfterBytes);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = body.Length;
            await WriteBodyAsync(context, body, 0, body.Length, server.FailAfterBytes);
        });

        await app.StartAsync();

        var address = app.Urls.First();
        server = new RangeTestServer(app, payload, $"{address}/file.bin")
        {
            SupportsRange = supportsRange
        };

        return server;
    }

    private static async Task WriteBodyAsync(HttpContext context, byte[] body, long start, long length, long failAfterBytes)
    {
        var toWrite = failAfterBytes > 0 && failAfterBytes < length ? failAfterBytes : length;
        await context.Response.Body.WriteAsync(body.AsMemory((int)start, (int)toWrite));
        await context.Response.Body.FlushAsync();

        if (failAfterBytes > 0 && failAfterBytes < length)
        {
            // Abort mid-stream to simulate a dropped connection.
            context.Abort();
        }
    }

    private static (long start, long end) ParseRange(string rangeHeader, long totalLength)
    {
        var spec = rangeHeader["bytes=".Length..];
        var parts = spec.Split('-');
        var start = long.Parse(parts[0]);
        var end = parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : totalLength - 1;
        return (start, Math.Min(end, totalLength - 1));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
