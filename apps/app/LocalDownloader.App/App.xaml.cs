using System.IO;
using System.Net.Http;
using System.Windows;
using LocalDownloader.App.Ipc;
using LocalDownloader.App.Services;
using LocalDownloader.App.Settings;
using LocalDownloader.App.Tasks;
using LocalDownloader.App.Views;

namespace LocalDownloader.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instanceGuard;
    private TrayIconService? _trayIcon;
    private PipeServer? _pipeServer;
    private HttpClient? _httpClient;
    private MainWindow? _mainWindow;

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

        var appDataDirectory = SettingsStore.Load().DownloadDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsStore.DefaultSettingsPath())!);

        _httpClient = new HttpClient();
        var taskRegistryStore = new TaskRegistryStore();
        DownloadManager = new DownloadManagerService(_httpClient, SettingsStore, taskRegistryStore);
        DownloadManager.LoadPersistedTasks();

        var messageHandler = new AppMessageHandler(DownloadManager, SettingsStore);
        _pipeServer = new PipeServer
        {
            OnMessageReceived = messageHandler.HandleAsync
        };
        _pipeServer.Start();

        _mainWindow = new MainWindow();

        _trayIcon = new TrayIconService();
        _trayIcon.ShowMainWindowRequested += () => ShowMainWindow();
        _trayIcon.PauseAllRequested += () => DownloadManager.PauseAll();
        _trayIcon.ResumeAllRequested += () => DownloadManager.ResumeAll();
        _trayIcon.ExitRequested += () => ExitApplication();
        _trayIcon.Initialize();
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

    private void ExitApplication()
    {
        IsExiting = true;

        // Pause outstanding work and flush state before tearing down (design: "退出时未完成任务先暂停落盘").
        DownloadManager?.PauseAll();

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
            _trayIcon?.Dispose();
            _instanceGuard?.Dispose();
        }

        base.OnExit(e);
    }
}
