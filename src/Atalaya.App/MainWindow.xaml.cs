using System.Windows.Threading;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _pollTimer;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Polling loop (§3): pull on a timer, off the UI thread, results marshalled back here.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(viewModel.PollingSeconds) };
        _pollTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();
        Loaded += (_, _) => _pollTimer.Start();
        Closed += (_, _) => _pollTimer.Stop();
    }
}
