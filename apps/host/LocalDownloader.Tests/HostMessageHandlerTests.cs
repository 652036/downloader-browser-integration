using System.Net;
using System.Text;
using System.Text.Json;
using LocalDownloader.Core;
using LocalDownloader.Host;

namespace LocalDownloader.Tests;

public sealed class HostMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_download_accepted_for_valid_create_message()
    {
        var downloadEngine = new StubDownloadEngine();
        var handler = new HostMessageHandler(downloadEngine);
        const string message = """
            {
              "type": "download.create",
              "id": "browser-1",
              "url": "https://example.com/payload.txt",
              "suggestedFilename": "payload.txt"
            }
            """;

        var responseJson = await handler.HandleAsync(message, CancellationToken.None);

        using var response = JsonDocument.Parse(responseJson);
        Assert.Equal("download.accepted", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("browser-1", response.RootElement.GetProperty("id").GetString());
        Assert.Equal("queued", response.RootElement.GetProperty("status").GetString());
        Assert.Single(downloadEngine.Requests);
    }

    [Fact]
    public async Task HandleAsync_accepts_valid_create_message_before_network_download_finishes()
    {
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadEngine = new StubDownloadEngine(_ => downloadStarted.Task);
        var handler = new HostMessageHandler(downloadEngine);
        const string message = """
            {
              "type": "download.create",
              "id": "browser-queued",
              "url": "https://example.com/unreachable.zip",
              "suggestedFilename": "unreachable.zip"
            }
            """;

        var responseJson = await handler.HandleAsync(message, CancellationToken.None);

        using var response = JsonDocument.Parse(responseJson);
        Assert.Equal("download.accepted", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("browser-queued", response.RootElement.GetProperty("id").GetString());
        Assert.Equal("queued", response.RootElement.GetProperty("status").GetString());
        Assert.Single(downloadEngine.Requests);
    }

    [Fact]
    public async Task HandleAsync_returns_download_error_for_unsupported_url()
    {
        var downloadEngine = new StubDownloadEngine();
        var handler = new HostMessageHandler(downloadEngine);
        const string message = """
            {
              "type": "download.create",
              "id": "browser-1",
              "url": "file:///C:/Windows/win.ini",
              "suggestedFilename": "win.ini"
            }
            """;

        var responseJson = await handler.HandleAsync(message, CancellationToken.None);

        using var response = JsonDocument.Parse(responseJson);
        Assert.Equal("download.error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("browser-1", response.RootElement.GetProperty("id").GetString());
        Assert.Equal("unsupported_url", response.RootElement.GetProperty("code").GetString());
        Assert.Empty(downloadEngine.Requests);
    }

    [Fact]
    public async Task HandleAsync_returns_protocol_error_for_invalid_json()
    {
        var downloadEngine = new StubDownloadEngine();
        var handler = new HostMessageHandler(downloadEngine);

        var responseJson = await handler.HandleAsync("{not json", CancellationToken.None);

        using var response = JsonDocument.Parse(responseJson);
        Assert.Equal("download.error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("host_protocol_error", response.RootElement.GetProperty("code").GetString());
        Assert.Empty(downloadEngine.Requests);
    }

    private sealed class StubDownloadEngine : IDownloadEngine
    {
        private readonly Func<DownloadRequest, Task>? _download;

        public StubDownloadEngine(Func<DownloadRequest, Task>? download = null)
        {
            _download = download;
        }

        public List<DownloadRequest> Requests { get; } = new();

        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_download is not null)
            {
                await _download(request);
            }

            return new DownloadResult(request.Id ?? "generated", "completed", "download.bin", 1);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TestHttpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _serverTask;

        private TestHttpServer(HttpListener listener, Task serverTask, string url)
        {
            _listener = listener;
            _serverTask = serverTask;
            Url = url;
        }

        public string Url { get; }

        public static async Task<TestHttpServer> StartAsync(string responseText)
        {
            var port = GetFreePort();
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            var serverTask = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                var payload = Encoding.UTF8.GetBytes(responseText);
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload);
                context.Response.Close();
            });

            await Task.Yield();
            return new TestHttpServer(listener, serverTask, $"{prefix}payload.txt");
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
        }

        private static int GetFreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }
}
