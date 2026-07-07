using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LocalDownloader.App.Services;

/// <summary>
/// Thin Win32 shell for clipboard monitoring: registers a message-only window as a clipboard
/// format listener (<c>AddClipboardFormatListener</c>) and reacts to <c>WM_CLIPBOARDUPDATE</c>.
/// All actual URL detection and dedupe logic lives in <see cref="ClipboardUrlDetector"/> and
/// <see cref="ClipboardDedupeTracker"/> so it can be unit tested without a real clipboard.
/// </summary>
public sealed class ClipboardWatcherService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int HWND_MESSAGE = -3;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly ClipboardDedupeTracker _dedupeTracker;
    private readonly Func<IReadOnlyCollection<string>> _getInterceptExtensions;
    private readonly Func<bool> _isEnabled;
    private HwndSource? _hwndSource;
    private string? _lastSelfWrittenText;

    /// <summary>Raised on the UI thread with a detected, not-recently-offered download URL.</summary>
    public event Action<string>? DownloadUrlDetected;

    public ClipboardWatcherService(
        ClipboardDedupeTracker dedupeTracker,
        Func<IReadOnlyCollection<string>> getInterceptExtensions,
        Func<bool> isEnabled)
    {
        _dedupeTracker = dedupeTracker;
        _getInterceptExtensions = getInterceptExtensions;
        _isEnabled = isEnabled;
    }

    public void Start()
    {
        if (_hwndSource is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("LocalDownloaderClipboardWatcher")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(HWND_MESSAGE)
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
        AddClipboardFormatListener(_hwndSource.Handle);
    }

    /// <summary>Call right after the App itself writes to the clipboard (e.g. a "copy link"
    /// action elsewhere in the UI) so the resulting WM_CLIPBOARDUPDATE is ignored instead of
    /// popping a confirmation window for content the app produced itself.</summary>
    public void NotifySelfWrite(string text)
    {
        _lastSelfWrittenText = text;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardUpdated();
        }

        return IntPtr.Zero;
    }

    private void OnClipboardUpdated()
    {
        if (!_isEnabled())
        {
            return;
        }

        string? text;
        try
        {
            text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        }
        catch (Exception)
        {
            // The clipboard can be transiently locked by another process; skip this update.
            return;
        }

        if (text is null)
        {
            return;
        }

        if (_lastSelfWrittenText is not null && string.Equals(text, _lastSelfWrittenText, StringComparison.Ordinal))
        {
            return;
        }

        var url = ClipboardUrlDetector.FindDownloadUrl(text, _getInterceptExtensions());
        if (url is null)
        {
            return;
        }

        if (!_dedupeTracker.ShouldOffer(url))
        {
            return;
        }

        DownloadUrlDetected?.Invoke(url);
    }

    public void Dispose()
    {
        if (_hwndSource is not null)
        {
            RemoveClipboardFormatListener(_hwndSource.Handle);
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
