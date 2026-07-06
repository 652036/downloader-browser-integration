using System.ComponentModel;
using System.Windows;
using LocalDownloader.App.ViewModels;

namespace LocalDownloader.App.Views;

/// <summary>
/// Closing the main window minimizes to tray instead of exiting; only the tray "退出" menu
/// item terminates the process (see design doc lifecycle rules).
/// </summary>
public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        App.Current.ShowSettingsWindow();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }
}
