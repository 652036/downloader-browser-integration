using System.IO;
using System.Net.Http;
using System.Text.Json;
using LocalDownloader.App.Services;
using LocalDownloader.App.Settings;
using LocalDownloader.App.Tasks;
using LocalDownloader.Core;

namespace LocalDownloader.App.Tests;

public sealed class AppMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_download_create_returns_accepted_and_raises_DownloadRequested()
    {
        using var temp = new TempDirectory();
        var settingsStore = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var taskRegistryStore = new TaskRegistryStore(Path.Combine(temp.Path, "tasks.json"));
        using var httpClient = new HttpClient();
        var downloadManager = new DownloadManagerService(httpClient, settingsStore, taskRegistryStore);
        var handler = new AppMessageHandler(downloadManager, settingsStore);

        DownloadRequest? captured = null;
        handler.DownloadRequested += request => captured = request;

        const string message = """
            {
              "type": "download.create",
              "id": "browser-1",
              "url": "https://example.com/file.zip",
              "suggestedFilename": "file.zip",
              "cookieHeader": "session=abc",
              "userAgent": "TestAgent/1.0"
            }
            """;

        var responseJson = await handler.HandleAsync(message, CancellationToken.None);

        using var response = JsonDocument.Parse(responseJson!);
        Assert.Equal("download.accepted", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("browser-1", response.RootElement.GetProperty("id").GetString());
        Assert.Equal("queued", response.RootElement.GetProperty("status").GetString());

        Assert.NotNull(captured);
        Assert.Equal("https://example.com/file.zip", captured!.Url);
        Assert.Equal("session=abc", captured.CookieHeader);
    }

    [Fact]
    public async Task HandleAsync_download_create_rejects_non_http_url()
    {
        using var temp = new TempDirectory();
        var settingsStore = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var taskRegistryStore = new TaskRegistryStore(Path.Combine(temp.Path, "tasks.json"));
        using var httpClient = new HttpClient();
        var downloadManager = new DownloadManagerService(httpClient, settingsStore, taskRegistryStore);
        var handler = new AppMessageHandler(downloadManager, settingsStore);

        const string message = """
            {
              "type": "download.create",
              "id": "browser-2",
              "url": "file:///C:/secret.txt"
            }
            """;

        var responseJson = await handler.HandleAsync(message, CancellationToken.None);

        using var response = JsonDocument.Parse(responseJson!);
        Assert.Equal("download.error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("unsupported_url", response.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task HandleAsync_settings_get_returns_intercept_lists()
    {
        using var temp = new TempDirectory();
        var settingsStore = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var taskRegistryStore = new TaskRegistryStore(Path.Combine(temp.Path, "tasks.json"));
        using var httpClient = new HttpClient();
        var downloadManager = new DownloadManagerService(httpClient, settingsStore, taskRegistryStore);
        var handler = new AppMessageHandler(downloadManager, settingsStore);

        const string message = """{ "type": "settings.get", "id": "s-1" }""";

        var responseJson = await handler.HandleAsync(message, CancellationToken.None);

        using var response = JsonDocument.Parse(responseJson!);
        Assert.Equal("settings.value", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("s-1", response.RootElement.GetProperty("id").GetString());
        var extensions = response.RootElement.GetProperty("interceptExtensions").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(".zip", extensions);
    }

    [Fact]
    public async Task HandleAsync_returns_protocol_error_for_invalid_json()
    {
        using var temp = new TempDirectory();
        var settingsStore = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var taskRegistryStore = new TaskRegistryStore(Path.Combine(temp.Path, "tasks.json"));
        using var httpClient = new HttpClient();
        var downloadManager = new DownloadManagerService(httpClient, settingsStore, taskRegistryStore);
        var handler = new AppMessageHandler(downloadManager, settingsStore);

        var responseJson = await handler.HandleAsync("{not json", CancellationToken.None);

        using var response = JsonDocument.Parse(responseJson!);
        Assert.Equal("download.error", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("host_protocol_error", response.RootElement.GetProperty("code").GetString());
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
}
