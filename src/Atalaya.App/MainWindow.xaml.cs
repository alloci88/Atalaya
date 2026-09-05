using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Atalaya.App.Services;
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
        // Y el intervalo SIGUE al ajuste (BUGFIX-AJUSTES): se fijaba al construir la ventana, así
        // que cambiar la frecuencia en Ajustes no hacía nada hasta el siguiente arranque —sin que
        // nada lo dijera—. Ajustarlo en el tick cuesta una comparación por minuto.
        _pollTimer.Tick += (_, _) => SyncPollingInterval();
        _pollTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();
        // El re-chequeo de versión de las instancias que no se reinician (F8 §3). Va en el mismo
        // tick del sondeo —no hace falta un reloj más para esto— pero como manejador APARTE: un
        // fallo del sondeo no puede llevarse por delante el chequeo, ni al revés. El tick solo
        // pregunta «¿le toca?»; las 24 h las decide UpdateCheckService.
        _pollTimer.Tick += async (_, _) => await _viewModel.RecheckForUpdatesIfDueAsync();
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
        // El tamaño se guarda al cerrar, no en cada arrastre: escribir el settings.json a cada
        // píxel de un redimensionado sería cientos de escrituras por gesto.
        Closed += (_, _) => _viewModel.SaveWindowPlacement(
            WindowPlacementService.Capture(
                WindowState,
                RestoreBounds,
                new Rect(Left, Top, Width, Height)));

        // F5.8 §1: volver al primer plano recalcula el estado de vinculación de la página viva.
        // Es el momento en que el usuario acaba de venir del explorador de archivos, que es donde
        // se mueven y se borran las carpetas de las que el piloto habla.
        Activated += async (_, _) => await _viewModel.OnWindowActivatedAsync();

        // AL CAMBIAR DE PÁGINA, EL FOCO ENTRA EN LA PÁGINA (UI-0042).
        _viewModel.Navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Services.NavigationService.Current))
            {
                FocusPage();
            }
        };

        ApplySavedPlacement();
    }

    /// <summary>
    /// Coloca la ventana donde estaba (F26 Parte A, D-956). La PRIMERA vez, maximizada: Atalaya se
    /// desarrolló y se probó en una ventana pequeña, y estrenarla así es empezar por el peor
    /// tamaño que tiene. Después, lo que el usuario dejara.
    /// </summary>
    private void ApplySavedPlacement()
    {
        var (maximized, left, top, width, height) =
            WindowPlacementService.Resolve(_viewModel.SavedWindowPlacement, VirtualScreen());

        Rect area = SystemParameters.WorkArea;
        Width = StartupSize.Clamp(width, MinWidth, area.Width);
        Height = StartupSize.Clamp(height, MinHeight, area.Height);

        if (left is not null && top is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left.Value;
            Top = top.Value;
        }

        if (maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// LLEVA EL FOCO AL PRIMER CONTROL ÚTIL DE LA PÁGINA (UI-0042).
    /// <para>
    /// <b>De dónde viene.</b> Al navegar con el teclado el foco no iba a la página nueva ni se
    /// quedaba en la entrada pulsada: volvía al elemento ventana. Desde ahí, llegar al primer
    /// control del contenido costaba <b>14 paradas en Portafolio, 13 en Hallazgos y 22 en
    /// Ajustes</b>, la mayoría recorriendo otra vez la carcasa entera. Navegar es «ya estoy aquí,
    /// déjame trabajar»; el foco tiene que ir donde va la mirada.
    /// </para>
    /// <para>
    /// <b>Al fondo de la cola</b>: cuando <c>Current</c> cambia, el <c>ContentControl</c> todavía
    /// no ha montado la vista nueva —la plantilla se aplica al renderizar— y no habría a quién
    /// enfocar. Y solo se mueve si el foco está FUERA de la página: durante una carga larga el
    /// usuario puede haber pulsado ya algo dentro, y quitárselo sería peor que no haberlo movido.
    /// </para>
    /// </summary>
    private void FocusPage() => Dispatcher.BeginInvoke(
        DispatcherPriority.Input,
        new Action(() =>
        {
            if (PageHost.IsKeyboardFocusWithin)
            {
                return;
            }

            PageHost.MoveFocus(new System.Windows.Input.TraversalRequest(
                System.Windows.Input.FocusNavigationDirection.First));
        }));

    /// <summary>
    /// El raíl se pliega solo cuando la ventana se estrecha (principio 1: reorganizar, no encoger).
    /// Se mide el ancho de la carcasa —lo que hay debajo de la barra de título— y no el de la
    /// ventana, porque es el que de verdad se reparten raíl y contenido.
    /// </summary>
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        double threshold = TryFindResource("Rail.CollapseBelow") is double d ? d : 1120;
        _viewModel.OnShellWidthChanged(e.NewSize.Width, threshold);
    }

    /// <summary>
    /// Pone el reloj del sondeo a lo que digan los ajustes ahora mismo. Solo toca el temporizador
    /// cuando de verdad ha cambiado: reasignar <c>Interval</c> lo reinicia, y hacerlo en cada tick
    /// dejaría el sondeo perpetuamente aplazado.
    /// </summary>
    private void SyncPollingInterval()
    {
        var wanted = TimeSpan.FromSeconds(_viewModel.PollingSeconds);
        if (_pollTimer.Interval != wanted)
        {
            _pollTimer.Interval = wanted;
        }
    }

    /// <summary>
    /// El escritorio ENTERO, con todos sus monitores. Es contra esto —y no contra el monitor
    /// principal— contra lo que se comprueba una posición guardada: una ventana que se cerró en el
    /// segundo monitor tiene coordenadas perfectamente válidas que no están en el primero (F5.4 §6,
    /// F26 §A).
    /// </summary>
    private static Rect VirtualScreen() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

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
        if (_confirmedClose)
        {
            return;
        }

        if (_viewModel.FixIsRunning && !ConfirmClosingWithFix())
        {
            e.Cancel = true;
            return;
        }

        if (!_viewModel.SessionIsRunning)
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

    /// <summary>
    /// Cerrar con un arreglo asistido en curso (F6.9 §4). El aviso NO puede ser el de la
    /// auditoría: allí lo que se pierde es cobertura, aquí lo que queda son <b>ficheros ya
    /// modificados en el clon del usuario</b>. Eso hay que decirlo, y hay que decir también que se
    /// pueden descartar después — el registro de lo tocado sobrevive al proceso.
    /// </summary>
    private bool ConfirmClosingWithFix()
    {
        System.Windows.MessageBoxResult answer = System.Windows.MessageBox.Show(
            "Hay un arreglo asistido en curso.\n\n"
            + "Si cierras ahora se detendrá ordenadamente, pero los cambios que el agente ya haya "
            + "aplicado SE QUEDAN en tu clon, sin commitear.\n\n"
            + "Podrás descartarlos la próxima vez que abras Atalaya, desde «Arreglo asistido».\n\n"
            + "¿Cerrar Atalaya?",
            "Arreglo en curso",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            return false;
        }

        _viewModel.StopFix();
        return true;
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
