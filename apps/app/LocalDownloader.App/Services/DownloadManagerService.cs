using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using LocalDownloader.App.Settings;
using LocalDownloader.App.Tasks;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.Services;

/// <summary>
/// Owns the in-memory task list, enforces the configured concurrent-task limit (FIFO queue for
/// overflow), drives the <see cref="SegmentedDownloadEngine"/> for each task, and keeps the
/// durable task registry (tasks.json) in sync. Raises <see cref="TaskChanged"/> so the WPF UI
/// can update its DataGrid; callers are responsible for marshalling that event to the
/// Dispatcher thread.
/// </summary>
public sealed class DownloadManagerService
{
    private readonly SegmentedDownloadEngine _engine;
    private readonly SettingsStore _settingsStore;
    private readonly TaskRegistryStore _taskRegistryStore;
    private readonly ConcurrentDictionary<string, ManagedDownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _inFlightRuns = new();
    private readonly object _queueLock = new();
    private readonly Queue<string> _pendingQueue = new();
    private int _runningCount;

    public DownloadManagerService(HttpClient httpClient, SettingsStore settingsStore, TaskRegistryStore taskRegistryStore)
    {
        _engine = new SegmentedDownloadEngine(httpClient);
        _settingsStore = settingsStore;
        _taskRegistryStore = taskRegistryStore;
    }

    public event Action<ManagedDownloadTask>? TaskChanged;

    public IReadOnlyCollection<ManagedDownloadTask> Tasks => _tasks.Values.ToArray();

    /// <summary>Loads persisted tasks.json entries as paused tasks (design: "重启后未完成任务以 paused 载入").</summary>
    public void LoadPersistedTasks()
    {
        foreach (var record in _taskRegistryStore.Load())
        {
            if (record.Status is "completed")
            {
                continue;
            }

            var task = new ManagedDownloadTask
            {
                Id = record.Id,
                Request = new DownloadRequest
                {
                    Type = IpcMessageType.DownloadCreate,
                    Id = record.Id,
                    Url = record.Url,
                    SuggestedFilename = record.SuggestedFilename,
                    Referrer = record.Referrer,
                    UserAgent = record.UserAgent
                },
                Status = DownloadTaskStatus.Paused,
                FilePath = record.FilePath,
                OutputDirectory = record.OutputDirectory,
                BytesDownloaded = record.BytesDownloaded,
                TotalBytes = record.TotalBytes,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt
            };

            _tasks[task.Id] = task;
            TaskChanged?.Invoke(task);
        }
    }

    public ManagedDownloadTask CreateTask(DownloadRequest request, string? outputDirectory = null)
    {
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!;
        var task = new ManagedDownloadTask { Id = id, Request = request, OutputDirectory = outputDirectory };
        _tasks[id] = task;
        Enqueue(id);
        PersistAll();
        TaskChanged?.Invoke(task);
        return task;
    }

    public bool TryGetTask(string id, out ManagedDownloadTask task) => _tasks.TryGetValue(id, out task!);

    public void PauseTask(string id)
    {
        if (_tasks.TryGetValue(id, out var task))
        {
            task.RunCts?.Cancel();
        }
    }

    public void ResumeTask(string id)
    {
        if (_tasks.TryGetValue(id, out var task) &&
            task.Status is DownloadTaskStatus.Paused or DownloadTaskStatus.Failed or DownloadTaskStatus.Queued)
        {
            task.IsCanceledByUser = false;
            Enqueue(id);
        }
    }

    public void CancelTask(string id)
    {
        if (_tasks.TryGetValue(id, out var task))
        {
            task.IsCanceledByUser = true;
            task.RunCts?.Cancel();
            task.Status = DownloadTaskStatus.Canceled;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            PersistAll();
            TaskChanged?.Invoke(task);
        }
    }

    public void RemoveTask(string id, bool deleteFile)
    {
        if (_tasks.TryRemove(id, out var task))
        {
            TryDeleteSidecars(task);
            if (deleteFile)
            {
                TryDeleteFile(task.FilePath);
            }

            PersistAll();
        }
    }

    public void PauseAll()
    {
        // Queued tasks have no RunCts; they must leave the FIFO and become Paused so
        // TryStartNext cannot start them after a PauseAll / ExitApplication.
        lock (_queueLock)
        {
            _pendingQueue.Clear();
        }

        foreach (var task in _tasks.Values)
        {
            if (task.Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Probing or DownloadTaskStatus.Queued)
            {
                if (task.Status is DownloadTaskStatus.Queued)
                {
                    task.Status = DownloadTaskStatus.Paused;
                    task.UpdatedAt = DateTimeOffset.UtcNow;
                    TaskChanged?.Invoke(task);
                }

                task.RunCts?.Cancel();
            }
        }

        PersistAll();
    }

    /// <summary>Blocks until in-flight <see cref="RunTaskAsync"/> calls finish persisting
    /// (or <paramref name="timeout"/> elapses). Used by ExitApplication so teardown does not
    /// race sidecar / tasks.json writes.</summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        var pending = _inFlightRuns.Values.Select(tcs => tcs.Task).ToArray();
        if (pending.Length == 0)
        {
            return true;
        }

        return Task.WhenAll(pending).Wait(timeout);
    }

    public void ResumeAll()
    {
        foreach (var task in _tasks.Values)
        {
            if (task.Status is DownloadTaskStatus.Paused)
            {
                ResumeTask(task.Id);
            }
        }
    }

    private void Enqueue(string id)
    {
        lock (_queueLock)
        {
            _pendingQueue.Enqueue(id);
        }

        TryStartNext();
    }

    private void TryStartNext()
    {
        var settings = _settingsStore.Load();

        while (true)
        {
            string? nextId = null;
            lock (_queueLock)
            {
                if (_runningCount >= settings.MaxConcurrentTasks || _pendingQueue.Count == 0)
                {
                    return;
                }

                nextId = _pendingQueue.Dequeue();
                _runningCount++;
            }

            if (!_tasks.TryGetValue(nextId, out var task))
            {
                lock (_queueLock)
                {
                    _runningCount--;
                }

                continue;
            }

            _ = RunTaskAsync(task, settings);
        }
    }

    private async Task RunTaskAsync(ManagedDownloadTask task, AppSettings settings)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlightRuns[task.Id] = completion;

        task.RunCts = new CancellationTokenSource();

        try
        {
            if (task.Status is DownloadTaskStatus.Paused or DownloadTaskStatus.Canceled ||
                task.IsCanceledByUser ||
                task.RunCts.IsCancellationRequested)
            {
                return;
            }

            task.Status = DownloadTaskStatus.Probing;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            TaskChanged?.Invoke(task);

            var options = new SegmentedDownloadOptions
            {
                OutputDirectory = string.IsNullOrWhiteSpace(task.OutputDirectory) ? settings.DownloadDirectory : task.OutputDirectory,
                Connections = settings.ConnectionsPerTask,
                ResumeFilePath = task.FilePath
            };

        var progress = new Progress<DownloadProgressSnapshot>(snapshot =>
        {
            task.Status = snapshot.Status;
            task.BytesDownloaded = snapshot.BytesDownloaded;
            task.TotalBytes = snapshot.TotalBytes ?? task.TotalBytes;
            task.SegmentCount = snapshot.SegmentCount;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            TaskChanged?.Invoke(task);
        });

            try
            {
                var result = await _engine.DownloadAsync(task.Request, options, progress, task.RunCts.Token);
                task.Status = result.Status;
                task.FilePath = result.FilePath;
                task.BytesDownloaded = result.BytesWritten;
                task.SegmentCount = result.SegmentCount;
            }
            catch (Exception ex)
            {
                task.Status = task.IsCanceledByUser ? DownloadTaskStatus.Canceled : DownloadTaskStatus.Failed;
                task.ErrorMessage = ex.Message;
            }
        }
        finally
        {
            task.UpdatedAt = DateTimeOffset.UtcNow;
            task.RunCts?.Dispose();
            task.RunCts = null;

            lock (_queueLock)
            {
                _runningCount--;
            }

            PersistAll();
            TaskChanged?.Invoke(task);
            completion.TrySetResult();
            _inFlightRuns.TryRemove(task.Id, out _);
            TryStartNext();
        }
    }

    private static void TryDeleteSidecars(ManagedDownloadTask task)
    {
        if (string.IsNullOrWhiteSpace(task.FilePath))
        {
            return;
        }

        TryDeleteFile($"{task.FilePath}.part");
        TryDeleteFile($"{task.FilePath}.task.json");
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void PersistAll()
    {
        var records = _tasks.Values.Select(t => new PersistedTaskRecord
        {
            Id = t.Id,
            Url = t.Request.Url ?? string.Empty,
            SuggestedFilename = t.Request.SuggestedFilename,
            Referrer = t.Request.Referrer,
            UserAgent = t.Request.UserAgent,
            Status = t.Status.ToString().ToLowerInvariant(),
            FilePath = t.FilePath,
            OutputDirectory = t.OutputDirectory,
            TotalBytes = t.TotalBytes,
            BytesDownloaded = t.BytesDownloaded,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt
        }).ToList();

        _taskRegistryStore.Save(records);
    }
}
