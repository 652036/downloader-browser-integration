using System.Windows;
using LocalDownloader.App.ViewModels;

namespace LocalDownloader.App.Views;

/// <summary>
/// IDM-style "new download" confirmation popup. Multiple simultaneous download.create
/// messages are shown one at a time by the caller (see App.ShowConfirmDownloadWindow queue).
/// </summary>
public partial class ConfirmDownloadWindow : Window
{
    public ConfirmDownloadViewModel ViewModel { get; }

    public ConfirmDownloadWindow(ConfirmDownloadViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.RequestClose += () => Dispatcher.Invoke(Close);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 直接关窗等同取消（ViewModel 默认 Outcome 为 Cancel）。
        SystemCommands.CloseWindow(this);
    }
}
