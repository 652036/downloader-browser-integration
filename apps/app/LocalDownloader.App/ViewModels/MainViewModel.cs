using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDownloader.App.Services;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.ViewModels;

/// <summary>主窗口左侧导航的分区。</summary>
public enum TaskSection
{
    /// <summary>下载中：所有非已完成的任务。</summary>
    Active,

    /// <summary>已完成的任务。</summary>
    Completed
}

/// <summary>
/// Backs the main window's card list and toolbar. Progress from
/// <see cref="DownloadManagerService.TaskChanged"/> arrives on background threads and can fire
/// very frequently (once per segment progress persist), so updates are coalesced and flushed
/// to the UI thread at most every 250ms via a DispatcherTimer, per the design doc.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DownloadManagerService _downloadManager;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly HashSet<string> _dirtyTaskIds = new();
    private readonly object _dirtyLock = new();

    public ObservableCollection<DownloadTaskViewModel> Tasks { get; } = new();

    /// <summary>按 <see cref="SelectedSection"/> 过滤后的视图，卡片列表绑定这里。</summary>
    public ICollectionView TasksView { get; }

    [ObservableProperty]
    private string _newTaskUrl = string.Empty;

    [ObservableProperty]
    private TaskSection _selectedSection = TaskSection.Active;

    public MainViewModel(DownloadManagerService downloadManager)
    {
        _downloadManager = downloadManager;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var task in downloadManager.Tasks)
        {
            Tasks.Add(new DownloadTaskViewModel(task));
        }

        TasksView = CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = MatchesSection;

        _downloadManager.TaskChanged += OnTaskChanged;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _refreshTimer.Tick += (_, _) => FlushDirtyTasks();
        _refreshTimer.Start();
    }

    /// <summary>当前分区是否包含该任务：下载中=非已完成的全部；已完成=Completed。</summary>
    public bool MatchesSection(object? item)
    {
        if (item is not DownloadTaskViewModel task)
        {
            return false;
        }

        return SelectedSection == TaskSection.Completed
            ? task.Status == DownloadTaskStatus.Completed
            : task.Status != DownloadTaskStatus.Completed;
    }

    partial void OnSelectedSectionChanged(TaskSection value)
    {
        TasksView.Refresh();
    }

    [RelayCommand]
    private void ShowActiveSection() => SelectedSection = TaskSection.Active;

    [RelayCommand]
    private void ShowCompletedSection() => SelectedSection = TaskSection.Completed;

    private void OnTaskChanged(ManagedDownloadTask task)
    {
        lock (_dirtyLock)
        {
            _dirtyTaskIds.Add(task.Id);
        }
    }

    private void FlushDirtyTasks()
    {
        string[] dirtyIds;
        lock (_dirtyLock)
        {
            if (_dirtyTaskIds.Count == 0)
            {
                return;
            }

            dirtyIds = _dirtyTaskIds.ToArray();
            _dirtyTaskIds.Clear();
        }

        var sectionMembershipChanged = false;

        foreach (var id in dirtyIds)
        {
            if (!_downloadManager.TryGetTask(id, out var task))
            {
                var stale = Tasks.FirstOrDefault(t => t.Id == id);
                if (stale is not null)
                {
                    Tasks.Remove(stale);
                }

                continue;
            }

            var viewModel = Tasks.FirstOrDefault(t => t.Id == id);
            if (viewModel is null)
            {
                Tasks.Add(new DownloadTaskViewModel(task));
            }
            else
            {
                var wasCompleted = viewModel.Status == DownloadTaskStatus.Completed;
                viewModel.ApplyFrom(task);
                if (wasCompleted != (viewModel.Status == DownloadTaskStatus.Completed))
                {
                    sectionMembershipChanged = true;
                }
            }
        }

        // 只有任务在“下载中/已完成”分区之间迁移时才刷新视图，避免高频 Refresh 反复重建卡片。
        if (sectionMembershipChanged)
        {
            TasksView.Refresh();
        }
    }

    [RelayCommand]
    private void CreateTaskFromUrl()
    {
        if (string.IsNullOrWhiteSpace(NewTaskUrl))
        {
            return;
        }

        var request = new DownloadRequest
        {
            Type = IpcMessageType.DownloadCreate,
            Id = Guid.NewGuid().ToString("N"),
            Url = NewTaskUrl.Trim(),
            Source = "manual"
        };

        if (DownloadRequestValidator.TryValidate(request, out _))
        {
            _downloadManager.CreateTask(request);
        }

        NewTaskUrl = string.Empty;
    }

    [RelayCommand]
    private void PauseTask(DownloadTaskViewModel? task)
    {
        if (task is not null)
        {
            _downloadManager.PauseTask(task.Id);
        }
    }

    [RelayCommand]
    private void ResumeTask(DownloadTaskViewModel? task)
    {
        if (task is not null)
        {
            _downloadManager.ResumeTask(task.Id);
        }
    }

    [RelayCommand]
    private void CancelTask(DownloadTaskViewModel? task)
    {
        if (task is not null)
        {
            _downloadManager.CancelTask(task.Id);
        }
    }

    [RelayCommand]
    private void DeleteTask(DownloadTaskViewModel? task)
    {
        if (task is null)
        {
            return;
        }

        _downloadManager.RemoveTask(task.Id, deleteFile: false);
        var viewModel = Tasks.FirstOrDefault(t => t.Id == task.Id);
        if (viewModel is not null)
        {
            Tasks.Remove(viewModel);
        }
    }

    [RelayCommand]
    private void OpenFolder(DownloadTaskViewModel? task)
    {
        if (task?.FilePath is not { } filePath)
        {
            return;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        if (File.Exists(filePath))
        {
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        else
        {
            Process.Start("explorer.exe", $"\"{directory}\"");
        }
    }

    [RelayCommand]
    private void PauseAll() => _downloadManager.PauseAll();

    [RelayCommand]
    private void ResumeAll() => _downloadManager.ResumeAll();

    public void Dispose()
    {
        _refreshTimer.Stop();
        _downloadManager.TaskChanged -= OnTaskChanged;
    }
}
