using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDownloader.App.Services;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;

namespace LocalDownloader.App.ViewModels;

/// <summary>
/// Backs the main window's DataGrid and toolbar. Progress from
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

    [ObservableProperty]
    private DownloadTaskViewModel? _selectedTask;

    [ObservableProperty]
    private string _newTaskUrl = string.Empty;

    public MainViewModel(DownloadManagerService downloadManager)
    {
        _downloadManager = downloadManager;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var task in downloadManager.Tasks)
        {
            Tasks.Add(new DownloadTaskViewModel(task));
        }

        _downloadManager.TaskChanged += OnTaskChanged;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _refreshTimer.Tick += (_, _) => FlushDirtyTasks();
        _refreshTimer.Start();
    }

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
                viewModel.ApplyFrom(task);
            }
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
    private void PauseSelected()
    {
        if (SelectedTask is not null)
        {
            _downloadManager.PauseTask(SelectedTask.Id);
        }
    }

    [RelayCommand]
    private void ResumeSelected()
    {
        if (SelectedTask is not null)
        {
            _downloadManager.ResumeTask(SelectedTask.Id);
        }
    }

    [RelayCommand]
    private void CancelSelected()
    {
        if (SelectedTask is not null)
        {
            _downloadManager.CancelTask(SelectedTask.Id);
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedTask is not null)
        {
            var id = SelectedTask.Id;
            _downloadManager.RemoveTask(id, deleteFile: false);
            var viewModel = Tasks.FirstOrDefault(t => t.Id == id);
            if (viewModel is not null)
            {
                Tasks.Remove(viewModel);
            }
        }
    }

    [RelayCommand]
    private void OpenContainingFolder()
    {
        if (SelectedTask?.FilePath is not { } filePath)
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

    public void Dispose()
    {
        _refreshTimer.Stop();
        _downloadManager.TaskChanged -= OnTaskChanged;
    }
}
