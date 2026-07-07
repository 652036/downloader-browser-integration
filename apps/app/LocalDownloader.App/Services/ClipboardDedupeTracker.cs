namespace LocalDownloader.App.Services;

/// <summary>
/// Suppresses repeat confirmation popups for a clipboard-detected URL: the same URL is only
/// offered once per <see cref="_window"/> (default 10 minutes), and a URL the user explicitly
/// canceled in the confirmation popup is never offered again for the rest of the process
/// lifetime (until the process restarts). Injectable clock so tests do not depend on real time.
/// </summary>
public sealed class ClipboardDedupeTracker
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DateTimeOffset> _recentlyOffered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _canceled = new(StringComparer.OrdinalIgnoreCase);

    public ClipboardDedupeTracker()
        : this(DefaultWindow, () => DateTimeOffset.UtcNow)
    {
    }

    public ClipboardDedupeTracker(TimeSpan window, Func<DateTimeOffset> now)
    {
        _window = window;
        _now = now;
    }

    /// <summary>Returns true (and records the URL as offered) if this URL should trigger a
    /// confirmation popup right now: it hasn't been offered within the dedupe window, and it
    /// hasn't been permanently suppressed by a prior user cancellation.</summary>
    public bool ShouldOffer(string url)
    {
        if (_canceled.Contains(url))
        {
            return false;
        }

        var now = _now();
        if (_recentlyOffered.TryGetValue(url, out var lastOffered) && now - lastOffered < _window)
        {
            return false;
        }

        _recentlyOffered[url] = now;
        return true;
    }

    /// <summary>Marks a URL as canceled by the user: it will not be offered again for the
    /// lifetime of this tracker, even after the dedupe window would otherwise have elapsed.</summary>
    public void MarkCanceled(string url)
    {
        _canceled.Add(url);
    }
}
