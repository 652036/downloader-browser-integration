using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDownloader.Core;
using Microsoft.Win32;

namespace LocalDownloader.App.ViewModels;

public enum ConfirmDownloadOutcome
{
    Start,
    Cancel,
    ReturnToBrowser
}

/// <summary>
/// Backs the IDM-style confirmation popup shown for each incoming download.create message:
/// editable filename, source domain, size (browser-supplied estimate, refined once probed),
/// and a browsable save directory. Buttons: Start / Cancel / Return to Browser.
/// </summary>
public sealed partial class ConfirmDownloadViewModel : ObservableObject
{
    public DownloadRequest Request { get; }

    public ConfirmDownloadOutcome Outcome { get; private set; } = ConfirmDownloadOutcome.Cancel;

    public event Action? RequestClose;

    [ObservableProperty]
    private string _fileName;

    [ObservableProperty]
    private string _sourceDomain;

    [ObservableProperty]
    private string _sizeDisplay;

    [ObservableProperty]
    private string _saveDirectory;

    public ConfirmDownloadViewModel(DownloadRequest request, string defaultSaveDirectory)
        : this(request, defaultSaveDirectory, categorizeByType: false)
    {
    }

    public ConfirmDownloadViewModel(DownloadRequest request, string defaultSaveDirectory, bool categorizeByType)
    {
        Request = request;
        _fileName = ResolveInitialFileName(request);
        _sourceDomain = ResolveDomain(request.Url);
        _sizeDisplay = FormatSize(request.FileSize);
        _saveDirectory = categorizeByType
            ? Path.Combine(defaultSaveDirectory, FileCategoryClassifier.Classify(_fileName))
            : defaultSaveDirectory;
    }

    [RelayCommand]
    private void Start()
    {
        Outcome = ConfirmDownloadOutcome.Start;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Outcome = ConfirmDownloadOutcome.Cancel;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void ReturnToBrowser()
    {
        Outcome = ConfirmDownloadOutcome.ReturnToBrowser;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void BrowseSaveDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(SaveDirectory) ? SaveDirectory : null
        };

        if (dialog.ShowDialog() == true)
        {
            SaveDirectory = dialog.FolderName;
        }
    }

    private static string ResolveInitialFileName(DownloadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SuggestedFilename))
        {
            return Path.GetFileName(request.SuggestedFilename.Replace('\\', '/'));
        }

        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return "download.bin";
    }

    private static string ResolveDomain(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "未知来源";
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is not > 0)
        {
            return "大小未知";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes.Value;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.0} {units[unitIndex]}";
    }
}
