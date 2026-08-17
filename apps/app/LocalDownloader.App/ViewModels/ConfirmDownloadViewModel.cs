using System.IO;
using System.Net.Http;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDownloader.Core;
using LocalDownloader.Core.Segments;
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
    /// <summary>Probes a download request for its real size, returning the total byte count or
    /// null if the probe fails (network error, no Content-Length, etc). Takes the request's
    /// UA/Referer/Cookie headers into account so the probe reaches the same content a real
    /// download would. Injectable for unit tests; production code wires this to
    /// <see cref="DownloadProbe"/> via a real HttpClient (see <see cref="CreateDefaultSizeProbe"/>).</summary>
    public delegate Task<long?> SizeProbe(DownloadRequest request, CancellationToken cancellationToken);

    private readonly SizeProbe? _sizeProbe;
    private readonly SynchronizationContext? _syncContext;

    public DownloadRequest Request { get; }

    public ConfirmDownloadOutcome Outcome { get; private set; } = ConfirmDownloadOutcome.Cancel;

    /// <summary>Clipboard (and other non-browser) sources never canceled a Chrome download, so
    /// "return to browser" is not offered.</summary>
    public bool ShowReturnToBrowser =>
        !string.Equals(Request.Source, "clipboard", StringComparison.OrdinalIgnoreCase);

    /// <summary>A canceled browser-intercepted download must fail-open back to Chrome; the
    /// extension already canceled the original item when it handed off.</summary>
    public bool ShouldFailOpenOnCancel =>
        string.Equals(Request.Source, "browser-download", StringComparison.OrdinalIgnoreCase);

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
        : this(request, defaultSaveDirectory, categorizeByType: false, sizeProbe: null)
    {
    }

    public ConfirmDownloadViewModel(DownloadRequest request, string defaultSaveDirectory, bool categorizeByType)
        : this(request, defaultSaveDirectory, categorizeByType, sizeProbe: null)
    {
    }

    public ConfirmDownloadViewModel(
        DownloadRequest request,
        string defaultSaveDirectory,
        bool categorizeByType,
        SizeProbe? sizeProbe)
    {
        Request = request;
        _fileName = ResolveInitialFileName(request);
        _sourceDomain = ResolveDomain(request.Url);
        _sizeDisplay = FormatSize(request.FileSize);
        _saveDirectory = categorizeByType
            ? Path.Combine(defaultSaveDirectory, FileCategoryClassifier.Classify(_fileName))
            : defaultSaveDirectory;

        _sizeProbe = sizeProbe;
        _syncContext = SynchronizationContext.Current;

        _ = RefreshSizeFromProbeAsync();
    }

    private async Task RefreshSizeFromProbeAsync()
    {
        if (_sizeProbe is null || string.IsNullOrWhiteSpace(Request.Url))
        {
            return;
        }

        long? probedBytes;
        try
        {
            probedBytes = await _sizeProbe(Request, CancellationToken.None);
        }
        catch (Exception)
        {
            // Probe failure keeps whatever size was already displayed (design: "探测失败保持原显示").
            return;
        }

        if (probedBytes is not > 0)
        {
            return;
        }

        var display = FormatSize(probedBytes);
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => SizeDisplay = display, null);
        }
        else
        {
            SizeDisplay = display;
        }
    }

    /// <summary>Real network-backed probe used by production code (see App.xaml.cs); a fresh
    /// short-lived HttpClient per call keeps this self-contained and dependency-free for the
    /// popup's lifetime.</summary>
    public static SizeProbe CreateDefaultSizeProbe()
    {
        return async (request, cancellationToken) =>
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            using var httpClient = new HttpClient();
            var probe = await DownloadProbe.ProbeAsync(
                httpClient,
                uri,
                requestMessage =>
                {
                    if (!string.IsNullOrWhiteSpace(request.UserAgent))
                    {
                        requestMessage.Headers.UserAgent.TryParseAdd(request.UserAgent);
                    }

                    if (Uri.TryCreate(request.Referrer, UriKind.Absolute, out var referrer) &&
                        (referrer.Scheme == Uri.UriSchemeHttp || referrer.Scheme == Uri.UriSchemeHttps))
                    {
                        requestMessage.Headers.Referrer = referrer;
                    }

                    if (!string.IsNullOrWhiteSpace(request.CookieHeader))
                    {
                        requestMessage.Headers.TryAddWithoutValidation("Cookie", request.CookieHeader);
                    }
                },
                cancellationToken);

            return probe.TotalBytes;
        };
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
