using LocalDownloader.Host;

var downloadRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Downloads",
    "LocalDownloader");

using var httpClient = new HttpClient();
var handler = new HostMessageHandler(new DownloadEngine(httpClient, downloadRoot));
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

while (true)
{
    string? requestJson;

    try
    {
        requestJson = await NativeMessaging.ReadMessageAsync(input, CancellationToken.None);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException)
    {
        await Console.Error.WriteLineAsync($"Native messaging read error: {ex.Message}");
        break;
    }

    if (requestJson is null)
    {
        break;
    }

    var responseJson = await handler.HandleAsync(requestJson, CancellationToken.None);
    await NativeMessaging.WriteMessageAsync(output, responseJson, CancellationToken.None);
}

await handler.WhenIdleAsync();
