using LocalDownloader.Core;
using LocalDownloader.Host;

var logger = new HostLogger();
logger.Log("LocalDownloader.Host starting.");

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

// Connect (and launch the App if needed) BEFORE reading the first native-messaging frame so
// the Host's 5s App-launch window runs while the extension is still waiting, rather than
// after the first frame has already started the extension's response timer.
var connector = new AppPipeConnector(logger);
var appPipe = await connector.ConnectAsync(CancellationToken.None);

// The Host is a thin proxy: it never parses download.create business fields. Its only job is
// to get bytes from the browser's stdio Native Messaging channel to the App's named pipe (and
// back), starting the App if the pipe is not already listening.
string? firstMessage;
try
{
    firstMessage = await NativeMessaging.ReadMessageAsync(input, CancellationToken.None);
}
catch (Exception ex) when (ex is IOException or InvalidDataException)
{
    logger.Log($"Native messaging read error before first message: {ex.Message}");
    appPipe?.Dispose();
    return;
}

if (firstMessage is null)
{
    logger.Log("Browser closed the connection before sending any message.");
    appPipe?.Dispose();
    return;
}

if (appPipe is null)
{
    // fail-open: tell the browser we could not reach the App at all so it can fall back to a
    // normal browser download instead of losing the request.
    string? requestId = null;
    try
    {
        using var document = System.Text.Json.JsonDocument.Parse(firstMessage);
        if (document.RootElement.TryGetProperty("id", out var idProperty))
        {
            requestId = idProperty.GetString();
        }
    }
    catch (System.Text.Json.JsonException)
    {
        // Ignore; respond without an id.
    }

    var errorJson = RelayService.BuildAppUnavailableError(requestId);
    await NativeMessaging.WriteMessageAsync(output, errorJson, CancellationToken.None);
    logger.Log("App unavailable; returned download.error to browser.");
    return;
}

using (appPipe)
{
    // Forward the already-read first message, then relay everything else bidirectionally.
    await NativeMessaging.WriteMessageAsync(appPipe, firstMessage, CancellationToken.None);

    var relay = new RelayService(logger);
    await relay.RunAsync(input, output, appPipe, CancellationToken.None);
}

logger.Log("LocalDownloader.Host exiting.");
