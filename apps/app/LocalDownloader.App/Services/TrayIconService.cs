using System.Drawing;
using System.Windows.Forms;

namespace LocalDownloader.App.Services;

/// <summary>
/// Owns the persistent tray icon. Double-click (left button) shows the main window; the
/// right-click context menu offers 显示主窗口 / 全部暂停 / 全部开始 / 退出, matching the design
/// doc's tray menu spec. The icon comes from the executable's embedded app.ico, and the
/// context menu is rendered with the app's dark palette so the tray matches the main UI.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private static readonly Color MenuBg = Color.FromArgb(0x24, 0x26, 0x2C);
    private static readonly Color MenuHover = Color.FromArgb(0x2A, 0x2C, 0x33);
    private static readonly Color MenuBorder = Color.FromArgb(0x34, 0x36, 0x3E);
    private static readonly Color MenuText = Color.FromArgb(0xE8, 0xE9, 0xEC);

    private readonly NotifyIcon _notifyIcon;

    public event Action? ShowMainWindowRequested;
    public event Action? PauseAllRequested;
    public event Action? ResumeAllRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "本地下载器",
            Visible = false
        };

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable()) { RoundedEdges = false },
            BackColor = MenuBg,
            ForeColor = MenuText,
            ShowImageMargin = false
        };

        AddItem(menu, "显示主窗口", () => ShowMainWindowRequested?.Invoke());
        AddItem(menu, "全部暂停", () => PauseAllRequested?.Invoke());
        AddItem(menu, "全部开始", () => ResumeAllRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        AddItem(menu, "退出", () => ExitRequested?.Invoke());

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

    private static void AddItem(ContextMenuStrip menu, string text, Action action)
    {
        var item = new ToolStripMenuItem(text)
        {
            ForeColor = MenuText,
            Padding = new Padding(4, 6, 4, 6)
        };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                var extracted = Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch (Exception)
        {
        }

        return SystemIcons.Application;
    }

    private sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBg;
        public override Color ImageMarginGradientBegin => MenuBg;
        public override Color ImageMarginGradientMiddle => MenuBg;
        public override Color ImageMarginGradientEnd => MenuBg;
        public override Color MenuItemSelected => MenuHover;
        public override Color MenuItemSelectedGradientBegin => MenuHover;
        public override Color MenuItemSelectedGradientEnd => MenuHover;
        public override Color MenuItemPressedGradientBegin => MenuHover;
        public override Color MenuItemPressedGradientEnd => MenuHover;
        public override Color MenuItemBorder => MenuHover;
        public override Color MenuBorder => TrayIconService.MenuBorder;
        public override Color SeparatorDark => TrayIconService.MenuBorder;
        public override Color SeparatorLight => TrayIconService.MenuBorder;
    }
}
