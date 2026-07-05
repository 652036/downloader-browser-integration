using System.ComponentModel;
using System.Windows;

namespace LocalDownloader.App.Views;

/// <summary>
/// Closing the main window minimizes to tray instead of exiting; only the tray "Exit" menu
/// item terminates the process (see design doc lifecycle rules).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.Current is App app && app.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
