using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using LocalDownloader.App.Services;
using LocalDownloader.App.Settings;
using LocalDownloader.App.Tasks;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalDownloader.App.Tests;

/// <summary>
/// Covers the confirmation-popup "save to" bug fix: a task created with a per-task output
/// directory (as set by ConfirmDownloadViewModel.SaveDirectory when the user browses to a
/// different folder, or by the "按类型分类保存" default) must download into that directory even
/// though the global AppSettings.DownloadDirectory points elsewhere.
/// </summary>
public sealed class DownloadManagerServiceDirectoryTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly WebApplication _server;
    private readonly string _serverUrl;
    private readonly byte[] _payload;

    public DownloadManagerServiceDirectoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ldapp-dir-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _payload = new byte[4096];
        RandomNumberGenerator.Fill(_payload);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.MapGet("/file.bin", async context =>
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = _payload.Length;
            await context.Response.Body.WriteAsync(_payload);
        });
        _server.StartAsync().GetAwaiter().GetResult();
        _serverUrl = $"{_server.Urls.First()}/file.bin";
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Task_with_per_task_output_directory_downloads_there_instead_of_global_directory()
    {
        var globalDirectory = Path.Combine(_tempDir, "global");
        var perTaskDirectory = Path.Combine(_tempDir, "per-task-category");
        Directory.CreateDirectory(globalDirectory);

        var settingsStore = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        var settings = settingsStore.Load();
        settings.DownloadDirectory = globalDirectory;
        settingsStore.Save(settings);

        var taskRegistryStore = new TaskRegistryStore(Path.Combine(_tempDir, "tasks.json"));
        var manager = new DownloadManagerService(new HttpClient(), settingsStore, taskRegistryStore);

        ManagedDownloadTask? completed = null;
        var completionSource = new TaskCompletionSource();
        manager.TaskChanged += t =>
        {
            // DownloadManagerService raises TaskChanged once from the engine's own "Completed"
            // progress snapshot (fired before RunTaskAsync's continuation has copied
            // result.FilePath onto the task) and again from RunTaskAsync's finally block once
            // FilePath is set; wait for the latter so FilePath is guaranteed populated.
            if (t.Status is DownloadTaskStatus.Failed || (t.Status is DownloadTaskStatus.Completed && t.FilePath is not null))
            {
                completed = t;
                completionSource.TrySetResult();
            }
        };

        var request = new DownloadRequest
        {
            Type = IpcMessageType.DownloadCreate,
            Url = _serverUrl,
            SuggestedFilename = "movie.mp4"
        };

        manager.CreateTask(request, perTaskDirectory);

        await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(completed);
        Assert.Equal(DownloadTaskStatus.Completed, completed!.Status);
        Assert.NotNull(completed.FilePath);
        Assert.Equal(perTaskDirectory, Path.GetDirectoryName(completed.FilePath));
        Assert.False(Directory.Exists(globalDirectory) && Directory.EnumerateFileSystemEntries(globalDirectory).Any());

        var written = await File.ReadAllBytesAsync(completed.FilePath!);
        Assert.Equal(_payload, written);
    }

    [Fact]
    public async Task Task_without_per_task_output_directory_falls_back_to_global_directory()
    {
        var globalDirectory = Path.Combine(_tempDir, "global-only");

        var settingsStore = new SettingsStore(Path.Combine(_tempDir, "settings2.json"));
        var settings = settingsStore.Load();
        settings.DownloadDirectory = globalDirectory;
        settingsStore.Save(settings);

        var taskRegistryStore = new TaskRegistryStore(Path.Combine(_tempDir, "tasks2.json"));
        var manager = new DownloadManagerService(new HttpClient(), settingsStore, taskRegistryStore);

        var completionSource = new TaskCompletionSource();
        ManagedDownloadTask? completed = null;
        manager.TaskChanged += t =>
        {
            // DownloadManagerService raises TaskChanged once from the engine's own "Completed"
            // progress snapshot (fired before RunTaskAsync's continuation has copied
            // result.FilePath onto the task) and again from RunTaskAsync's finally block once
            // FilePath is set; wait for the latter so FilePath is guaranteed populated.
            if (t.Status is DownloadTaskStatus.Failed || (t.Status is DownloadTaskStatus.Completed && t.FilePath is not null))
            {
                completed = t;
                completionSource.TrySetResult();
            }
        };

        var request = new DownloadRequest
        {
            Type = IpcMessageType.DownloadCreate,
            Url = _serverUrl,
            SuggestedFilename = "no-dir.bin"
        };

        manager.CreateTask(request);

        await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(completed);
        Assert.Equal(DownloadTaskStatus.Completed, completed!.Status);
        Assert.Equal(globalDirectory, Path.GetDirectoryName(completed.FilePath));
    }
}
