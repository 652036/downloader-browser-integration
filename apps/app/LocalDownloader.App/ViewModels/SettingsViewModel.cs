using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDownloader.App.Services;
using LocalDownloader.App.Settings;
using Microsoft.Win32;

namespace LocalDownloader.App.ViewModels;

/// <summary>
/// Backs the settings window: save directory, per-task connection count (1-32), concurrent
/// task count, the editable intercept extension list (one per line, textbox-friendly), and
/// launch-at-startup. Saved settings take effect immediately for new tasks and are picked up
/// by the extension on its next settings.get call.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;

    public event Action? RequestClose;

    [ObservableProperty]
    private string _downloadDirectory;

    [ObservableProperty]
    private int _connectionsPerTask;

    [ObservableProperty]
    private int _maxConcurrentTasks;

    [ObservableProperty]
    private string _interceptExtensionsText;

    [ObservableProperty]
    private bool _launchAtStartup;

    [ObservableProperty]
    private bool _categorizeByType;

    [ObservableProperty]
    private bool _watchClipboard;

    public SettingsViewModel(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        var settings = settingsStore.Load();

        _downloadDirectory = settings.DownloadDirectory;
        _connectionsPerTask = settings.ConnectionsPerTask;
        _maxConcurrentTasks = settings.MaxConcurrentTasks;
        _interceptExtensionsText = string.Join(Environment.NewLine, settings.InterceptExtensions);
        _launchAtStartup = settings.LaunchAtStartup;
        _categorizeByType = settings.CategorizeByType;
        _watchClipboard = settings.WatchClipboard;
    }

    [RelayCommand]
    private void BrowseDownloadDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(DownloadDirectory) ? DownloadDirectory : null
        };

        if (dialog.ShowDialog() == true)
        {
            DownloadDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var settings = new AppSettings
        {
            DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory)
                ? AppSettings.DefaultDownloadDirectory()
                : DownloadDirectory,
            ConnectionsPerTask = Math.Clamp(ConnectionsPerTask, 1, 32),
            MaxConcurrentTasks = Math.Max(1, MaxConcurrentTasks),
            InterceptExtensions = ParseExtensions(InterceptExtensionsText),
            InterceptMimePrefixes = AppSettings.DefaultInterceptMimePrefixes(),
            LaunchAtStartup = LaunchAtStartup,
            CategorizeByType = CategorizeByType,
            WatchClipboard = WatchClipboard
        };

        _settingsStore.Save(settings);
        StartupRegistration.SetEnabled(LaunchAtStartup);

        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    private static List<string> ParseExtensions(string text)
    {
        return text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.StartsWith('.') ? line : $".{line}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
