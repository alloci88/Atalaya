using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Atalaya.App.ViewModels;
using Wpf.Ui.Controls;

namespace Atalaya.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _pollTimer;
    private bool _confirmedClose;

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
        Closing += OnClosing;
    }

    /// <summary>
    /// Cerrar con una auditoría corriendo pregunta antes (F5.2, Hito 1).
    /// <para>
    /// Y si se confirma, se detiene por el camino ORDENADO de F5.1b: la misma parada que el botón
    /// «Detener», que escribe el registro de sesión, libera los claims y publica. No se duplica el
    /// camino de parada — un segundo camino sería justo el que se olvidaría de alguno de los tres.
    /// </para>
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_confirmedClose || !_viewModel.SessionIsRunning)
        {
            return;
        }

        System.Windows.MessageBoxResult answer = System.Windows.MessageBox.Show(
            "Hay una auditoría en curso.\n\n"
            + "Si cierras ahora se detendrá ordenadamente: lo auditado hasta la parada se guarda "
            + "con su informe y las unidades se liberan.\n\n¿Cerrar Atalaya?",
            "Sesión en curso",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _confirmedClose = true;
        _viewModel.StopSession();
    }
}
