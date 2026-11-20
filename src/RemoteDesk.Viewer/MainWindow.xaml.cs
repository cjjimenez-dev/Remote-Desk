using System.Windows;
using RemoteDesk.Viewer.ViewModels;

namespace RemoteDesk.Viewer;

public partial class MainWindow : Window
{
    private readonly ViewerViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new ViewerViewModel(Dispatcher);
        DataContext = _viewModel;

        Closed += (s, e) => _viewModel.Dispose();
    }
}
