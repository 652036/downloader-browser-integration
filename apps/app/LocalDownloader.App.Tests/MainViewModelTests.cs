using System.IO;
using System.Net.Http;
using LocalDownloader.App.Services;
using LocalDownloader.App.Settings;
using LocalDownloader.App.Tasks;
using LocalDownloader.App.ViewModels;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DownloadManagerService _downloadManager;
    private readonly TaskRegistryStore _taskRegistryStore;

    public MainViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ldapp-mainvm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var settingsStore = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        _taskRegistryStore = new TaskRegistryStore(Path.Combine(_tempDir, "tasks.json"));
        _downloadManager = new DownloadManagerService(new HttpClient(), settingsStore, _taskRegistryStore);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ManagedDownloadTask CreateTask(DownloadTaskStatus status)
    {
        return new ManagedDownloadTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Request = new DownloadRequest
            {
                Type = IpcMessageType.DownloadCreate,
                Url = "https://example.com/files/a.zip"
            },
            Status = status
        };
    }

    /// <summary>把一个已暂停任务写进注册表并载入，得到一个不会触网的受管任务。</summary>
    private ManagedDownloadTask LoadPausedTask()
    {
        var record = new PersistedTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = "https://example.com/files/a.zip",
            Status = "paused",
            BytesDownloaded = 10,
            TotalBytes = 100,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _taskRegistryStore.Save(new[] { record });
        _downloadManager.LoadPersistedTasks();

        Assert.True(_downloadManager.TryGetTask(record.Id, out var task));
        return task;
    }

    [Fact]
    public void Default_section_is_active_and_hides_completed_tasks()
    {
        using var viewModel = new MainViewModel(_downloadManager);
        var downloading = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Downloading));
        var paused = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Paused));
        var completed = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Completed));
        viewModel.Tasks.Add(downloading);
        viewModel.Tasks.Add(paused);
        viewModel.Tasks.Add(completed);

        Assert.Equal(TaskSection.Active, viewModel.SelectedSection);
        var visible = viewModel.TasksView.Cast<DownloadTaskViewModel>().ToList();
        Assert.Contains(downloading, visible);
        Assert.Contains(paused, visible);
        Assert.DoesNotContain(completed, visible);
    }

    [Fact]
    public void Completed_section_shows_only_completed_tasks()
    {
        using var viewModel = new MainViewModel(_downloadManager);
        var downloading = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Downloading));
        var completed = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Completed));
        viewModel.Tasks.Add(downloading);
        viewModel.Tasks.Add(completed);

        viewModel.ShowCompletedSectionCommand.Execute(null);

        Assert.Equal(TaskSection.Completed, viewModel.SelectedSection);
        var visible = viewModel.TasksView.Cast<DownloadTaskViewModel>().ToList();
        Assert.Equal(new[] { completed }, visible);
    }

    [Fact]
    public void ShowActiveSectionCommand_switches_back_from_completed()
    {
        using var viewModel = new MainViewModel(_downloadManager);
        var paused = new DownloadTaskViewModel(CreateTask(DownloadTaskStatus.Paused));
        viewModel.Tasks.Add(paused);

        viewModel.ShowCompletedSectionCommand.Execute(null);
        Assert.Empty(viewModel.TasksView.Cast<DownloadTaskViewModel>());

        viewModel.ShowActiveSectionCommand.Execute(null);
        Assert.Equal(new[] { paused }, viewModel.TasksView.Cast<DownloadTaskViewModel>().ToList());
    }

    [Fact]
    public void CancelTaskCommand_cancels_task_in_manager()
    {
        var task = LoadPausedTask();
        using var viewModel = new MainViewModel(_downloadManager);
        var cardViewModel = Assert.Single(viewModel.Tasks);

        viewModel.CancelTaskCommand.Execute(cardViewModel);

        Assert.True(_downloadManager.TryGetTask(task.Id, out var canceled));
        Assert.Equal(DownloadTaskStatus.Canceled, canceled.Status);
    }

    [Fact]
    public void DeleteTaskCommand_removes_task_from_manager_and_list()
    {
        var task = LoadPausedTask();
        using var viewModel = new MainViewModel(_downloadManager);
        var cardViewModel = Assert.Single(viewModel.Tasks);

        viewModel.DeleteTaskCommand.Execute(cardViewModel);

        Assert.Empty(viewModel.Tasks);
        Assert.False(_downloadManager.TryGetTask(task.Id, out _));
    }

    [Fact]
    public void PauseTaskCommand_is_safe_for_tasks_without_running_download()
    {
        LoadPausedTask();
        using var viewModel = new MainViewModel(_downloadManager);
        var cardViewModel = Assert.Single(viewModel.Tasks);

        viewModel.PauseTaskCommand.Execute(cardViewModel);

        Assert.Equal(DownloadTaskStatus.Paused, cardViewModel.Status);
    }

    [Fact]
    public void Per_task_commands_ignore_null_parameter()
    {
        using var viewModel = new MainViewModel(_downloadManager);

        viewModel.PauseTaskCommand.Execute(null);
        viewModel.ResumeTaskCommand.Execute(null);
        viewModel.CancelTaskCommand.Execute(null);
        viewModel.DeleteTaskCommand.Execute(null);
        viewModel.OpenFolderCommand.Execute(null);
    }

    [Fact]
    public void OpenFolderCommand_ignores_task_without_file_path()
    {
        LoadPausedTask();
        using var viewModel = new MainViewModel(_downloadManager);
        var cardViewModel = Assert.Single(viewModel.Tasks);

        viewModel.OpenFolderCommand.Execute(cardViewModel);
    }
}
