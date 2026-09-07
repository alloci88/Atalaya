using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.Services;

/// <summary>En qué punto está un paso. Cuatro estados y ninguno más: esto no es un porcentaje.</summary>
public enum StepState
{
    /// <summary>Todavía no le ha tocado. Se lee en terciario.</summary>
    Pendiente,

    /// <summary>Está ocurriendo ahora: giro y reloj subiendo.</summary>
    EnCurso,

    /// <summary>Terminó, con lo que tardó al lado.</summary>
    Hecho,

    /// <summary>No terminó. En peligro, con el motivo <b>en la línea</b> y no en un tooltip.</summary>
    Fallido,
}

/// <summary>Cómo se despliega la lista: en columna, o en una tira de una línea.</summary>
public enum StepFlow
{
    /// <summary>Uno debajo de otro. Es la forma por defecto: cabe el motivo de un fallo.</summary>
    Vertical,

    /// <summary>Todos en una línea. Para una barra de herramientas, donde no hay altura que gastar.</summary>
    Horizontal,
}

/// <summary>
/// <b>Un paso declarado</b> (F30 §4). Es lo que el servicio promete hacer, y de aquí sale tanto lo
/// que se enseña como lo que se ejecuta: la lista es <b>una</b>.
/// </summary>
/// <param name="Id">
/// El identificador estable del paso. No se enseña: es con lo que el servicio lo ejecuta y con lo
/// que el test cuadra lo enseñado con lo hecho. Cambiar el rótulo no puede romper nada.
/// </param>
/// <param name="Label">Lo que se lee. Una acción en infinitivo, como el resto de la casa.</param>
/// <param name="Cancelable">
/// <b>Cancelar aquí deja el hub como estaba.</b> Solo es cierto mientras no se haya escrito nada:
/// en cuanto un paso escribe, cancelar dejaría la mitad de un gesto puesta y la otra no, que es
/// peor que esperar. Donde no es cierto, «Cancelar» <b>no se ofrece</b> — un botón que no puede
/// cumplir lo que promete es peor que su ausencia.
/// </param>
public sealed record StepSpec(string Id, string Label, bool Cancelable = false);

/// <summary>Un paso de una operación, con su estado, su reloj y —si falló— su motivo.</summary>
public sealed partial class Step : ObservableObject
{
    private readonly Stopwatch _clock = new();

    public Step(StepSpec spec)
    {
        Spec = spec;
    }

    public StepSpec Spec { get; }

    public string Id => Spec.Id;

    public string Label => Spec.Label;

    public bool Cancelable => Spec.Cancelable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(HasTime))]
    private StepState _state = StepState.Pendiente;

    /// <summary>
    /// Por qué falló, dicho en su línea. Va aquí y no en un toast porque un toast caduca y la línea
    /// que hay que leer se queda: la pregunta de quien mira una operación parada es «¿cuál?», y la
    /// respuesta tiene que estar pegada al paso que la contesta (D-944.4).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReason))]
    private string _reason = string.Empty;

    public bool HasReason => Reason.Length > 0;

    /// <summary>Lo que lleva o lo que tardó. Vacío mientras no le haya tocado.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public bool HasTime => State != StepState.Pendiente;

    /// <summary>
    /// El tiempo, con la precisión que hace falta y ninguna más. Por debajo del segundo con su
    /// decimal —el escaneo de una aplicación son 175 ms medidos (D-1019), y «0 s» diría que no
    /// pasó nada—; por encima, segundos enteros, como el pie de la sesión.
    /// </summary>
    public string TimeText
    {
        get
        {
            if (State == StepState.Pendiente)
            {
                return string.Empty;
            }

            TimeSpan e = _clock.Elapsed;
            if (e.TotalMinutes >= 1)
            {
                return $"{(int)e.TotalMinutes} m {e.Seconds:00} s";
            }

            return e.TotalSeconds >= 1
                ? $"{(int)e.TotalSeconds} s"
                : $"{e.TotalSeconds.ToString("0.0", AppCulture.Display)} s";
        }
    }

    internal void Reset()
    {
        _clock.Reset();
        Reason = string.Empty;
        State = StepState.Pendiente;
    }

    internal void Begin()
    {
        _clock.Restart();
        Reason = string.Empty;
        State = StepState.EnCurso;
    }

    internal void End(StepState state, string reason)
    {
        _clock.Stop();
        Reason = reason;
        State = state;
    }

    /// <summary>El reloj sube solo: lo repinta el tic de la lista, no un evento del servicio.</summary>
    internal void Tick() => OnPropertyChanged(nameof(TimeText));
}

/// <summary>
/// <b>Los pasos de una operación que dura segundos</b> (F30 §4).
/// <para>
/// <b>Por qué existe.</b> El §0 de F30 midió que escanear una aplicación tarda 175 ms y que lo que
/// se lleva el tiempo del alta es lo de después —escribir el inventario, reconciliar las unidades
/// grandes, publicar en el hub—, <b>sin una sola señal</b>: el botón se apagaba y la pantalla se
/// quedaba quieta. No hay nada que sacar del hilo de interfaz ni nada que contar por ficheros; lo
/// que faltaba es que esos pasos se vean, y que si uno falla se sepa cuál.
/// </para>
/// <para>
/// <b>Son pasos, no cantidades.</b> Ni porcentaje ni barra: nadie sabe qué fracción de un alta es
/// «escribir el inventario», y una barra que avanza a saltos inventados es peor que ninguna. Lo que
/// se puede decir con verdad es en qué paso va, cuánto lleva ése y cuánto tardaron los anteriores.
/// </para>
/// <para>
/// <b>Una sola lista.</b> Los pasos que se enseñan son los mismos objetos que el servicio ejecuta
/// —<see cref="Run{T}(string, Func{T})"/> los busca por identificador y revienta si no están
/// declarados—, así que un paso nuevo en el servicio no puede quedarse sin salir en pantalla. Es la
/// regla que protege <c>StepListTests</c>, y la única de esta pieza que se rompería en silencio.
/// </para>
/// </summary>
public sealed partial class StepList : ObservableObject
{
    /// <summary>
    /// <b>Lo que se lee cuando el hub no aceptó el push</b> (F31). El trabajo está escrito en el
    /// clon —no se ha perdido nada— y sale con lo pendiente en cuanto el hub conteste; lo que hace
    /// falta es que quien acaba de pulsar sepa que el equipo todavía no lo ve. Se escribe una vez
    /// porque lo dicen las cuatro operaciones.
    /// </summary>
    public const string PendingPublish = "no se pudo publicar · queda pendiente de publicar";

    /// <summary>Cada cuánto sube el reloj del paso en curso. Fino, porque hay pasos de medio segundo.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer? _clock;

    private readonly List<string> _executed = new();

    private CancellationTokenSource? _cts;

    public StepList(IReadOnlyList<StepSpec> plan, StepFlow flow = StepFlow.Vertical)
    {
        Flow = flow;
        foreach (StepSpec spec in plan)
        {
            Steps.Add(new Step(spec));
        }

        // La misma guarda que el reloj de la sesión (D-1017): sin `Application` no hay dispatcher
        // al que colgar un temporizador, y un test no puede levantar la suya.
        if (Application.Current is not null)
        {
            _clock = new DispatcherTimer { Interval = TickInterval };
            _clock.Tick += (_, _) => Current?.Tick();
        }
    }

    /// <summary>Los pasos, en orden. Se crean al construir la lista y no se añaden ni se quitan.</summary>
    public ObservableCollection<Step> Steps { get; } = new();

    /// <summary>En columna o en una tira. Lo decide quien la coloca, no el servicio.</summary>
    public StepFlow Flow { get; }

    /// <summary>El plan declarado, en orden. Es lo que la lista <b>enseña</b>.</summary>
    public IReadOnlyList<string> Plan => Steps.Select(s => s.Id).ToList();

    /// <summary>Lo que de verdad se ha ejecutado, en orden. Es con lo que se cuadra el plan.</summary>
    public IReadOnlyList<string> Executed => _executed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private bool _hasFailed;

    /// <summary>
    /// La lista se enseña mientras la operación dura, y <b>se queda</b> si algo falló: lo que hay
    /// que leer después de un fallo es qué paso fue, y eso no puede irse con el último tic.
    /// </summary>
    public bool IsVisible => IsRunning || HasFailed;

    /// <summary>El paso en curso, o null si no hay ninguno.</summary>
    public Step? Current => Steps.FirstOrDefault(s => s.State == StepState.EnCurso);

    /// <summary>
    /// <b>Cancelar solo donde cancelar deja el hub como estaba</b>: mientras el paso en curso lo
    /// declare. En los demás no se ofrece.
    /// </summary>
    public bool CanCancel => IsRunning && Current is { Cancelable: true };

    /// <summary>El testigo de cancelación de esta pasada. Lo mira el servicio entre pasos.</summary>
    public CancellationToken Token => (_cts ??= new CancellationTokenSource()).Token;

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Empieza una pasada: todo vuelve a pendiente y se abre un testigo nuevo. Se llama cada vez
    /// que se pulsa el botón, no una sola vez — reintentar es lo normal después de un fallo.
    /// </summary>
    public void Start()
    {
        Ui(() =>
        {
            foreach (Step step in Steps)
            {
                step.Reset();
            }

            _executed.Clear();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            HasFailed = false;
            IsRunning = true;
            OnPropertyChanged(nameof(CanCancel));
            _clock?.Start();
        });
    }

    /// <summary>La pasada terminó. Si nada falló, la lista se va; si algo falló, se queda.</summary>
    public void Finish()
    {
        Ui(() =>
        {
            _clock?.Stop();
            IsRunning = false;
            OnPropertyChanged(nameof(CanCancel));
        });
    }

    /// <summary>
    /// Ejecuta un paso <b>declarado</b> y devuelve lo suyo. Un identificador que no esté en el plan
    /// revienta aquí y no en pantalla: es la única forma de que un paso nuevo no pueda ejecutarse
    /// sin enseñarse.
    /// </summary>
    public T Run<T>(string id, Func<T> work)
    {
        Step step = Find(id);
        _executed.Add(id);
        Ui(() =>
        {
            step.Begin();

            // «Cancelar» depende del paso EN CURSO, así que cambia con cada paso y no con el
            // estado de la operación: se avisa aquí y al salir.
            OnPropertyChanged(nameof(CanCancel));
        });

        try
        {
            T result = work();
            Ui(() =>
            {
                step.End(StepState.Hecho, string.Empty);
                OnPropertyChanged(nameof(CanCancel));
            });
            return result;
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                step.End(StepState.Fallido, Motivo(ex));
                HasFailed = true;
                OnPropertyChanged(nameof(CanCancel));
            });
            throw;
        }
    }

    /// <inheritdoc cref="Run{T}(string, Func{T})"/>
    public void Run(string id, Action work)
        => Run(id, () =>
        {
            work();
            return true;
        });

    /// <summary>
    /// Lo mismo para un paso que se espera. <b>Es el que enseña el tiempo del agente</b>: la
    /// llamada al modelo es un paso en curso con su reloj subiendo, igual que los demás — no un
    /// hueco distinto ni una espera sin nombre (F30 §2e).
    /// </summary>
    public async Task<T> RunAsync<T>(string id, Func<Task<T>> work)
    {
        Step step = Find(id);
        _executed.Add(id);
        Ui(() =>
        {
            step.Begin();
            OnPropertyChanged(nameof(CanCancel));
        });

        try
        {
            T result = await work().ConfigureAwait(false);
            Ui(() =>
            {
                step.End(StepState.Hecho, string.Empty);
                OnPropertyChanged(nameof(CanCancel));
            });
            return result;
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                step.End(StepState.Fallido, Motivo(ex));
                HasFailed = true;
                OnPropertyChanged(nameof(CanCancel));
            });
            throw;
        }
    }

    /// <inheritdoc cref="RunAsync{T}(string, Func{Task{T}})"/>
    public Task RunAsync(string id, Func<Task> work)
        => RunAsync(id, async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        });

    /// <summary>
    /// <b>El paso salió, pero no salió bien</b>, y la operación sigue. Es el caso de publicar en el
    /// hub: el trabajo ya está escrito en el clon, así que un push que no sale no tumba nada — deja
    /// lo hecho «pendiente de publicar» (F31), y eso se dice en su línea en vez de fingir un check.
    /// </summary>
    public void Fail(string id, string reason)
    {
        Step step = Find(id);
        Ui(() =>
        {
            step.End(StepState.Fallido, reason);
            HasFailed = true;
        });
    }

    /// <summary>Vuelve a esconder la lista. La llama quien reabre el formulario.</summary>
    public void Reset()
    {
        Ui(() =>
        {
            _clock?.Stop();
            foreach (Step step in Steps)
            {
                step.Reset();
            }

            _executed.Clear();
            HasFailed = false;
            IsRunning = false;
        });
    }

    private Step Find(string id)
        => Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))
           ?? throw new InvalidOperationException(
               $"El paso «{id}» no está declarado en esta lista: un paso que no se declara no se "
               + "enseña, y una operación con pasos invisibles es la que esta pieza vino a retirar.");

    /// <summary>
    /// El motivo que se lee en la línea. Una cancelación no es un error del sistema y no se cuenta
    /// como tal: se dice lo que pasó y que no se ha escrito nada.
    /// </summary>
    private static string Motivo(Exception ex)
        => ex is OperationCanceledException
            ? "cancelado · no se ha escrito nada en el hub"
            : ex.Message;

    /// <summary>
    /// Las escrituras de la lista van al hilo de interfaz <b>sin esperar</b>, por lo mismo que el
    /// hilo de la conversación (F30 §2d): quien ejecuta los pasos suele ser un hilo de fondo que no
    /// puede quedarse detrás de la maquetación. Sin <c>Application</c> —los tests— se ejecuta en
    /// línea, que además es lo que hace falta para poder leer el estado justo después.
    /// </summary>
    private static void Ui(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Send, action);
    }
}
