using LocalDownloader.Core;
using LocalDownloader.Host;

namespace LocalDownloader.Tests.Relay;

public sealed class RelayServiceTests
{
    [Fact]
    public async Task RunAsync_forwards_browser_message_to_app_and_app_response_back_to_browser()
    {
        // Two duplex pairs, with the relay in the middle:
        //   test (browser role) <-> relayBrowserEnd | relay | relayAppEnd <-> test (App role)
        var (testBrowserEnd, relayBrowserEnd) = DuplexMemoryStream.CreatePair();
        var (relayAppEnd, testAppEnd) = DuplexMemoryStream.CreatePair();
        var logger = new HostLogger(CreateTempLogDir());
        var relay = new RelayService(logger);

        using var cts = new CancellationTokenSource();
        var relayTask = relay.RunAsync(relayBrowserEnd, relayBrowserEnd, relayAppEnd, cts.Token);

        // Browser -> Host -> App
        await NativeMessaging.WriteMessageAsync(testBrowserEnd, "{\"type\":\"download.create\",\"id\":\"1\"}", CancellationToken.None);
        var forwarded = await NativeMessaging.ReadMessageAsync(testAppEnd, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{\"type\":\"download.create\",\"id\":\"1\"}", forwarded);

        // App -> Host -> Browser
        await NativeMessaging.WriteMessageAsync(testAppEnd, "{\"type\":\"download.accepted\",\"id\":\"1\"}", CancellationToken.None);
        var response = await NativeMessaging.ReadMessageAsync(testBrowserEnd, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{\"type\":\"download.accepted\",\"id\":\"1\"}", response);

        cts.Cancel();
        try
        {
            await relayTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public void BuildAppUnavailableError_includes_request_id_and_host_protocol_error_code()
    {
        var json = RelayService.BuildAppUnavailableError("browser-42");

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(IpcMessageType.DownloadError, document.RootElement.GetProperty("type").GetString());
        Assert.Equal("browser-42", document.RootElement.GetProperty("id").GetString());
        Assert.Equal(IpcErrorCode.HostProtocolError, document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void BuildAppUnavailableError_allows_null_request_id()
    {
        var json = RelayService.BuildAppUnavailableError(null);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("id").ValueKind);
    }

    private static string CreateTempLogDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "LocalDownloaderHostTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
