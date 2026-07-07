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
    private int _activeConnections;
    private readonly object _slowRangesLock = new();
    private readonly List<(long start, long end)> _slowRanges = new();

    public byte[] Payload { get; }

    public string Url { get; }

    public bool SupportsRange { get; set; }

    /// <summary>When &gt; 0, the server aborts the connection after streaming this many bytes for
    /// the *next* request only (then resets), to simulate a mid-transfer failure.</summary>
    public long FailAfterBytes { get; set; }

    /// <summary>When &gt; 0, caps the number of simultaneously open request handlers; requests
    /// beyond the cap receive HTTP 429 immediately instead of being served.</summary>
    public int MaxConcurrentConnections { get; set; }

    /// <summary>When set, every accepted request holds its connection slot open for at least
    /// this long before completing, widening the window during which concurrent requests
    /// actually overlap (useful for deterministically exercising <see cref="MaxConcurrentConnections"/>
    /// against a fast local server and small payloads).</summary>
    public TimeSpan MinRequestDuration { get; set; }

    /// <summary>Per-byte artificial delay applied while streaming a response whose requested
    /// range overlaps any range added via <see cref="AddSlowRange"/>, simulating a
    /// slow/throttled portion of the file.</summary>
    public TimeSpan SlowRangeDelayPerChunk { get; set; } = TimeSpan.FromMilliseconds(15);

    public string? LastUserAgent { get; private set; }
    public string? LastReferer { get; private set; }
    public string? LastCookie { get; private set; }
    public int RequestCount { get; private set; }
    public int RejectedCount { get; private set; }

    private RangeTestServer(WebApplication app, byte[] payload, string url)
    {
        _app = app;
        Payload = payload;
        Url = url;
    }

    /// <summary>Marks a byte range [start, end] (inclusive) as "slow": any request whose Range
    /// overlaps it is throttled with a small per-chunk delay, to encourage the engine's
    /// work-stealing split to kick in on the (much faster) remaining segments.</summary>
    public void AddSlowRange(long start, long end)
    {
        lock (_slowRangesLock)
        {
            _slowRanges.Add((start, end));
        }
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
            if (server!.MaxConcurrentConnections > 0 &&
                Interlocked.Increment(ref server._activeConnections) > server.MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref server._activeConnections);
                server.RejectedCount++;
                context.Response.StatusCode = 429;
                context.Response.Headers.RetryAfter = "1";
                await context.Response.CompleteAsync();
                return;
            }

            try
            {
                server.RequestCount++;
                server.LastUserAgent = context.Request.Headers.UserAgent.ToString();
                server.LastReferer = context.Request.Headers.Referer.ToString();
                server.LastCookie = context.Request.Headers.Cookie.ToString();

                if (server.MinRequestDuration > TimeSpan.Zero)
                {
                    await Task.Delay(server.MinRequestDuration);
                }

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

                    await WriteBodyAsync(context, server, body, start, end, length, server.FailAfterBytes);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = body.Length;
                await WriteBodyAsync(context, server, body, 0, body.Length - 1, body.Length, server.FailAfterBytes);
            }
            finally
            {
                if (server.MaxConcurrentConnections > 0)
                {
                    Interlocked.Decrement(ref server._activeConnections);
                }
            }
        });

        await app.StartAsync();

        var address = app.Urls.First();
        server = new RangeTestServer(app, payload, $"{address}/file.bin")
        {
            SupportsRange = supportsRange
        };

        return server;
    }

    private static async Task WriteBodyAsync(
        HttpContext context, RangeTestServer server, byte[] body, long rangeStart, long rangeEnd, long length, long failAfterBytes)
    {
        var toWrite = failAfterBytes > 0 && failAfterBytes < length ? failAfterBytes : length;
        var isSlow = server.OverlapsSlowRange(rangeStart, rangeEnd);

        if (!isSlow)
        {
            await context.Response.Body.WriteAsync(body.AsMemory((int)rangeStart, (int)toWrite));
            await context.Response.Body.FlushAsync();
        }
        else
        {
            // Stream in small chunks with a delay so a worker stuck on a "slow" range takes
            // noticeably longer than one on a fast range, giving the other workers time to
            // finish their queue and steal a split of this segment's untouched tail.
            const int chunkSize = 32 * 1024;
            long written = 0;
            while (written < toWrite)
            {
                var thisChunk = (int)Math.Min(chunkSize, toWrite - written);
                await context.Response.Body.WriteAsync(body.AsMemory((int)(rangeStart + written), thisChunk));
                await context.Response.Body.FlushAsync();
                written += thisChunk;
                await Task.Delay(server.SlowRangeDelayPerChunk);
            }
        }

        if (failAfterBytes > 0 && failAfterBytes < length)
        {
            // Abort mid-stream to simulate a dropped connection.
            context.Abort();
        }
    }

    private bool OverlapsSlowRange(long start, long end)
    {
        lock (_slowRangesLock)
        {
            foreach (var (slowStart, slowEnd) in _slowRanges)
            {
                if (start <= slowEnd && end >= slowStart)
                {
                    return true;
                }
            }
        }

        return false;
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
