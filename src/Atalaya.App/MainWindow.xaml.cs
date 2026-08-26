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

    /// <summary>
    /// Barrido de avisos efímeros (F5.3 §3). Va aparte del sondeo del hub: aquél corre cada 60 s
    /// como poco, y un aviso que dura 8 s no puede depender de un reloj quince veces más lento.
    /// </summary>
    private readonly DispatcherTimer _toastTimer;

    private bool _confirmedClose;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Polling loop (§3): pull on a timer, off the UI thread, results marshalled back here.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(viewModel.PollingSeconds) };
        _pollTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _toastTimer.Tick += (_, _) => _viewModel.SweepToasts();

        Loaded += (_, _) =>
        {
            _pollTimer.Start();
            _toastTimer.Start();
        };
        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            _toastTimer.Stop();
        };
        Closing += OnClosing;

        // F5.8 §1: volver al primer plano recalcula el estado de vinculación de la página viva.
        // Es el momento en que el usuario acaba de venir del explorador de archivos, que es donde
        // se mueven y se borran las carpetas de las que el piloto habla.
        Activated += async (_, _) => await _viewModel.OnWindowActivatedAsync();

        FitToScreen();
    }

    /// <summary>
    /// Recorta el tamaño inicial a lo que de verdad cabe en la pantalla (F5.4 §6).
    /// <para>
    /// El ancho por defecto lo fija la barra de filtros de V3, que necesita 1314 px de ventana para
    /// caber en una línea. Eso pasa de sobra en un monitor normal, pero WPF mide en unidades
    /// independientes del dispositivo: con la pantalla al 150 %, un 1920 físico son 1280 de
    /// escritorio, y la ventana abriría más ancha que la pantalla y CENTRADA — es decir, con la
    /// barra de título a medias y los bordes fuera por los dos lados. Nunca por debajo del mínimo:
    /// más vale una ventana que se sale un poco que una inutilizable.
    /// </para>
    /// </summary>
    private void FitToScreen()
    {
        Rect area = SystemParameters.WorkArea;
        Width = StartupSize.Clamp(Width, MinWidth, area.Width);
        Height = StartupSize.Clamp(Height, MinHeight, area.Height);
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

/// <summary>El tamaño inicial que de verdad cabe. Separado de la ventana para poder probarlo.</summary>
public static class StartupSize
{
    /// <summary>Aire que se deja alrededor para que la ventana no toque los bordes del escritorio.</summary>
    public const double Margin = 40;

    /// <summary>
    /// El deseado, recortado a lo que queda de pantalla, pero nunca por debajo del mínimo: una
    /// ventana que se sale un poco es molesta; una por debajo de su mínimo es inutilizable.
    /// </summary>
    public static double Clamp(double desired, double minimum, double available)
        => Math.Max(minimum, Math.Min(desired, available - Margin));
}
