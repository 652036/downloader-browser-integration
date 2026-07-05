using System.Drawing;
using System.Windows.Forms;

namespace LocalDownloader.App.Services;

/// <summary>
/// Owns the persistent tray icon. Double-click (left button) shows the main window; the
/// right-click context menu offers Show / Pause All / Resume All / Exit, matching the design
/// doc's tray menu spec.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event Action? ShowMainWindowRequested;
    public event Action? PauseAllRequested;
    public event Action? ResumeAllRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Local Downloader",
            Visible = false
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowMainWindowRequested?.Invoke());
        menu.Items.Add("Pause All", null, (_, _) => PauseAllRequested?.Invoke());
        menu.Items.Add("Resume All", null, (_, _) => ResumeAllRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();
    }

    public void Initialize()
    {
        _notifyIcon.Visible = true;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
