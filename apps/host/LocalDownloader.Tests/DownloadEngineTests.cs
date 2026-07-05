using System.Net;
using System.Text;
using System.Text.Json;
using LocalDownloader.Host;

namespace LocalDownloader.Tests;

public sealed class DownloadEngineTests
{
    [Fact]
    public async Task DownloadAsync_writes_part_metadata_and_moves_to_final_file()
    {
        await using var server = await TestHttpServer.StartAsync("hello from server");
        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new DownloadEngine(client, temp.Path);
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "task-1",
            Url = server.Url,
            SuggestedFilename = "../payload.txt"
        };

        var result = await engine.DownloadAsync(request, CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Equal(Path.Combine(temp.Path, "payload.txt"), result.FilePath);
        Assert.Equal("hello from server", await File.ReadAllTextAsync(result.FilePath));
        Assert.False(File.Exists(Path.Combine(temp.Path, "payload.txt.part")));

        var metadataPath = Path.Combine(temp.Path, "payload.txt.task.json");
        Assert.True(File.Exists(metadataPath));
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        Assert.Equal("task-1", metadata.RootElement.GetProperty("id").GetString());
        Assert.Equal("completed", metadata.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DownloadAsync_marks_metadata_failed_when_server_returns_error()
    {
        await using var server = await TestHttpServer.StartAsync("server failed", statusCode: 500);
        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new DownloadEngine(client, temp.Path);
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "task-failed",
            Url = server.Url,
            SuggestedFilename = "bad.txt"
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => engine.DownloadAsync(request, CancellationToken.None));

        var metadataPath = Path.Combine(temp.Path, "bad.txt.task.json");
        Assert.True(File.Exists(metadataPath));
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        Assert.Equal("task-failed", metadata.RootElement.GetProperty("id").GetString());
        Assert.Equal("failed", metadata.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DownloadAsync_sends_browser_user_agent_and_referrer_headers()
    {
        string? userAgent = null;
        string? referer = null;
        await using var server = await TestHttpServer.StartAsync("header payload", onRequest: context =>
        {
            userAgent = context.Request.UserAgent;
            referer = context.Request.UrlReferrer?.ToString();
        });
        using var temp = new TempDirectory();
        using var client = new HttpClient();
        var engine = new DownloadEngine(client, temp.Path);
        var request = new DownloadRequest
        {
            Type = "download.create",
            Id = "task-headers",
            Url = server.Url,
            SuggestedFilename = "headers.txt",
            UserAgent = "LocalDownloaderTest/1.0",
            Referrer = "https://example.com/page"
        };

        await engine.DownloadAsync(request, CancellationToken.None);

        Assert.Equal("LocalDownloaderTest/1.0", userAgent);
        Assert.Equal("https://example.com/page", referer);
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

        public static async Task<TestHttpServer> StartAsync(
            string responseText,
            int statusCode = 200,
            Action<HttpListenerContext>? onRequest = null)
        {
            var port = GetFreePort();
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            var serverTask = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                onRequest?.Invoke(context);
                var payload = Encoding.UTF8.GetBytes(responseText);
                context.Response.StatusCode = statusCode;
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
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private static int GetFreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }
}
