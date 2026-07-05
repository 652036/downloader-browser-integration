using System.Text.Json;
using LocalDownloader.Core;

namespace LocalDownloader.Host;

/// <summary>
/// Thin bidirectional proxy between the browser's Native Messaging stdio stream and the App's
/// named pipe. The Host performs no business parsing of message payloads -- frames are
/// forwarded byte-for-byte as JSON strings using the shared NativeMessaging framing on both
/// sides. The only message the Host itself ever originates is a synthesized download.error,
/// emitted when the App pipe cannot be reached at all.
/// </summary>
public sealed class RelayService
{
    private readonly HostLogger _logger;

    public RelayService(HostLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Pumps frames from <paramref name="browserInput"/> to <paramref name="appStream"/> and
    /// from <paramref name="appStream"/> back to <paramref name="browserOutput"/> concurrently,
    /// until either side closes or <paramref name="cancellationToken"/> is triggered.
    /// </summary>
    public async Task RunAsync(
        Stream browserInput,
        Stream browserOutput,
        Stream appStream,
        CancellationToken cancellationToken)
    {
        var browserToApp = PumpAsync(browserInput, appStream, "browser->app", cancellationToken);
        var appToBrowser = PumpAsync(appStream, browserOutput, "app->browser", cancellationToken);

        await Task.WhenAny(browserToApp, appToBrowser);
    }

    /// <summary>
    /// Builds the synthesized download.error frame sent to the browser when the App pipe could
    /// not be reached at all (App failed to launch / pipe connect timed out).
    /// </summary>
    public static string BuildAppUnavailableError(string? requestId)
    {
        return JsonSerializer.Serialize(new
        {
            type = IpcMessageType.DownloadError,
            id = requestId,
            code = IpcErrorCode.HostProtocolError,
            message = "Local Downloader App is not available."
        });
    }

    private async Task PumpAsync(Stream source, Stream destination, string direction, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? message;
                try
                {
                    message = await NativeMessaging.ReadMessageAsync(source, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
                {
                    _logger.Log($"[{direction}] read error: {ex.Message}");
                    return;
                }

                if (message is null)
                {
                    return;
                }

                try
                {
                    await NativeMessaging.WriteMessageAsync(destination, message, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _logger.Log($"[{direction}] write error: {ex.Message}");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }
}
