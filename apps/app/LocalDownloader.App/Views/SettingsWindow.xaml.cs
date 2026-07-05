using System.Windows;
using LocalDownloader.App.ViewModels;

namespace LocalDownloader.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.RequestClose += () => Dispatcher.Invoke(Close);
    }
}
