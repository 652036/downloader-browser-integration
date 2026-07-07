using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using LocalDownloader.App.Ipc;
using LocalDownloader.App.Services;
using LocalDownloader.App.Settings;
using LocalDownloader.App.Tasks;
using LocalDownloader.App.ViewModels;
using LocalDownloader.App.Views;
using LocalDownloader.Core;

namespace LocalDownloader.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instanceGuard;
    private TrayIconService? _trayIcon;
    private PipeServer? _pipeServer;
    private HttpClient? _httpClient;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private SettingsWindow? _settingsWindow;
    private ClipboardWatcherService? _clipboardWatcher;
    private readonly ClipboardDedupeTracker _clipboardDedupeTracker = new();

    private readonly Queue<DownloadRequest> _pendingConfirmations = new();
    private readonly object _confirmLock = new();
    private bool _confirmWindowOpen;

    public DownloadManagerService? DownloadManager { get; private set; }

    public SettingsStore SettingsStore { get; } = new();

    public bool IsExiting { get; private set; }

    public static new App Current => (App)System.Windows.Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.IsPrimaryInstance)
        {
            Shutdown();
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsStore.DefaultSettingsPath())!);

        _httpClient = new HttpClient();
        var taskRegistryStore = new TaskRegistryStore();
        DownloadManager = new DownloadManagerService(_httpClient, SettingsStore, taskRegistryStore);
        DownloadManager.LoadPersistedTasks();

        var messageHandler = new AppMessageHandler(DownloadManager, SettingsStore);
        messageHandler.DownloadRequested += OnDownloadRequested;
        _pipeServer = new PipeServer
        {
            OnMessageReceived = messageHandler.HandleAsync
        };
        _pipeServer.Start();

        _mainViewModel = new MainViewModel(DownloadManager);
        _mainWindow = new MainWindow(_mainViewModel);

        _trayIcon = new TrayIconService();
        _trayIcon.ShowMainWindowRequested += ShowMainWindow;
        _trayIcon.PauseAllRequested += () => DownloadManager.PauseAll();
        _trayIcon.ResumeAllRequested += () => DownloadManager.ResumeAll();
        _trayIcon.ExitRequested += ExitApplication;
        _trayIcon.Initialize();

        _clipboardWatcher = new ClipboardWatcherService(
            _clipboardDedupeTracker,
            () => SettingsStore.Load().InterceptExtensions,
            () => SettingsStore.Load().WatchClipboard);
        _clipboardWatcher.DownloadUrlDetected += OnClipboardDownloadUrlDetected;
        _clipboardWatcher.Start();
    }

    private void OnClipboardDownloadUrlDetected(string url)
    {
        var request = new DownloadRequest
        {
            Type = IpcMessageType.DownloadCreate,
            Id = Guid.NewGuid().ToString("N"),
            Url = url,
            Source = "clipboard"
        };

        OnDownloadRequested(request);
    }

    private void OnDownloadRequested(DownloadRequest request)
    {
        Dispatcher.Invoke(() =>
        {
            lock (_confirmLock)
            {
                _pendingConfirmations.Enqueue(request);
            }

            TryShowNextConfirmation();
        });
    }

    private void TryShowNextConfirmation()
    {
        DownloadRequest? next;
        lock (_confirmLock)
        {
            if (_confirmWindowOpen || _pendingConfirmations.Count == 0)
            {
                return;
            }

            next = _pendingConfirmations.Dequeue();
            _confirmWindowOpen = true;
        }

        var settings = SettingsStore.Load();
        var viewModel = new ConfirmDownloadViewModel(
            next,
            settings.DownloadDirectory,
            settings.CategorizeByType,
            ConfirmDownloadViewModel.CreateDefaultSizeProbe());
        var window = new ConfirmDownloadWindow(viewModel);

        window.Closed += (_, _) =>
        {
            switch (viewModel.Outcome)
            {
                case ConfirmDownloadOutcome.Start:
                    var request = viewModel.Request;
                    request.SuggestedFilename = viewModel.FileName;
                    DownloadManager?.CreateTask(request, viewModel.SaveDirectory);
                    ShowMainWindow();
                    break;

                case ConfirmDownloadOutcome.ReturnToBrowser:
                    _ = SendReturnToBrowserAsync(viewModel.Request);
                    break;

                case ConfirmDownloadOutcome.Cancel:
                default:
                    // A canceled clipboard-detected URL must not be offered again (design:
                    // "弹窗被取消的 URL 不再重复弹"); browser-sourced cancellations are harmless
                    // no-ops here since they never populate the dedupe tracker in the first place.
                    if (next.Url is not null)
                    {
                        _clipboardDedupeTracker.MarkCanceled(next.Url);
                    }

                    break;
            }

            lock (_confirmLock)
            {
                _confirmWindowOpen = false;
            }

            TryShowNextConfirmation();
        };

        window.Show();
    }

    private async Task SendReturnToBrowserAsync(DownloadRequest request)
    {
        if (_pipeServer is null)
        {
            return;
        }

        var message = JsonSerializer.Serialize(new
        {
            type = IpcMessageType.DownloadReturnToBrowser,
            id = request.Id,
            url = request.Url,
            suggestedFilename = request.SuggestedFilename
        });

        await _pipeServer.BroadcastAsync(message, CancellationToken.None);
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    public void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(SettingsStore);
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ExitApplication()
    {
        IsExiting = true;

        // Pause outstanding work and flush state before tearing down (design: "退出时未完成任务先暂停落盘").
        DownloadManager?.PauseAll();

        _clipboardWatcher?.Dispose();
        _trayIcon?.Dispose();
        _pipeServer?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _httpClient?.Dispose();
        _instanceGuard?.Dispose();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!IsExiting)
        {
            _clipboardWatcher?.Dispose();
            _trayIcon?.Dispose();
            _instanceGuard?.Dispose();
        }

        base.OnExit(e);
    }
}
