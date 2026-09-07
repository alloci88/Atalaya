using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>
/// Dueño del estado de la sesión de auditoría en curso (F5.2, Hito 1).
/// <para>
/// <b>Por qué existe.</b> La sesión ya corría en segundo plano, pero su estado vivía en el
/// view-model de V5, que es <c>Transient</c>: navegar fuera y volver creaba una instancia nueva y
/// la pantalla aparecía vacía aunque la auditoría siguiera corriendo. Aquí el estado es un
/// singleton que sobrevive a la navegación, y V5 pasa a ser una VISTA sobre él: al volver, se
/// reconstruye desde este estado en vez de depender de haber estado abierta.
/// </para>
/// <para>
/// <b>Y arranca aquí, no al navegar.</b> Antes la sesión se lanzaba en <c>LoadAsync</c> de V5, es
/// decir, como efecto secundario de navegar — lo que provocó el bucle de auto-relanzado de D-085.
/// Ahora lanzar es un acto explícito (<see cref="StartAsync"/>) y navegar no ejecuta trabajo
/// jamás. El cerrojo de una-sesión-a-la-vez vive en este método.
/// </para>
/// </summary>
public sealed partial class LiveSessionService : ObservableObject
{
    private readonly Func<SessionCoordinator> _coordinatorFactory;
    private readonly ModelResolver? _models;
    private readonly Func<IAuditorProvider> _agent;
    private readonly OpenSessionStore _marker;
    private readonly HubContext? _hub;

    /// <summary>
    /// El cerrojo COMPARTIDO de F6.9: auditar y arreglar usan el mismo runtime, el mismo asiento y
    /// el mismo clon, así que solo puede haber uno. Opcional para no romper a los tests que
    /// construyen este servicio a mano; sin él se comporta como siempre.
    /// </summary>
    private readonly AgentBusyGate _busy;

    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private ActivityEntry? _currentText;
    private PassProgress? _currentPass;
    private UnitProgress? _currentUnit;
    private OpenSessionMarker? _openMarker;
    private int _unitsDone;

    // Desglose para la pantalla de cierre: qué hallazgos produjeron cada contador.
    // F12 §H.1 — los desgloses de hallazgos llevan la UNIDAD y la severidad, que es lo que permite
    // agruparlos por clase igual que los agrupa la vista de Hallazgos. Los de unidades e
    // incidencias siguen siendo líneas sueltas: ahí la unidad ya es la línea.
    private readonly List<SummaryItem> _new = new();
    private readonly List<SummaryItem> _confirmed = new();
    private readonly List<SummaryItem> _resolved = new();
    private readonly List<SummaryItem> _disputed = new();
    private readonly List<SummaryItem> _refused = new();
    private readonly List<SummaryItem> _nonVerifiable = new();
    private readonly List<SummaryItem> _silenced = new();
    private readonly List<string> _incidents = new();

    /// <param name="hub">
    /// Solo para resolver la ruta del informe que abre la pantalla de cierre. Opcional: sin él la
    /// sesión funciona igual y el botón avisa de que el informe no está localizable.
    /// </param>
    /// <param name="models">
    /// Quién decide el modelo contra la lista real de la cuenta (F5.15). Opcional: sin él la sesión
    /// usa el ajuste tal cual, que es lo que hacía antes.
    /// </param>
    public LiveSessionService(
        Func<SessionCoordinator> coordinatorFactory, IAuditorProvider agent, OpenSessionStore marker,
        HubContext? hub = null, ModelResolver? models = null, AgentBusyGate? busy = null)
        : this(coordinatorFactory, () => agent, marker, hub, models, busy)
    {
    }

    /// <summary>
    /// La forma que usa la aplicación (F14): el proveedor se PIDE en cada arranque de sesión, no
    /// se guarda.
    /// <para>
    /// Este servicio es un singleton y vive lo que vive la aplicación, mientras que el proveedor
    /// elegido se cambia en Ajustes y cambia sin reiniciar. Guardarse el que hubiera al arrancar
    /// haría que la comprobación previa —«¿puede auditar?»— interrogase a Copilot mientras la
    /// sesión iba a correr con Claude Code: exactamente el fallo de BUGFIX-AJUSTES, un valor
    /// capturado en el constructor que obliga a reiniciar para que un ajuste sirva de algo.
    /// </para>
    /// <para>
    /// La sobrecarga que recibe UN agente sigue existiendo para los tests, y ahí capturar es lo
    /// correcto: quien pasa un agente falso está diciendo «este», no «el que esté elegido».
    /// </para>
    /// </summary>
    public LiveSessionService(
        Func<SessionCoordinator> coordinatorFactory, Func<IAuditorProvider> agent, OpenSessionStore marker,
        HubContext? hub = null, ModelResolver? models = null, AgentBusyGate? busy = null)
    {
        _coordinatorFactory = coordinatorFactory;
        _agent = agent;
        _marker = marker;
        _hub = hub;
        _models = models;
        _busy = busy ?? new AgentBusyGate();
    }

    /// <summary>
    /// Un aviso que no es un fallo: «se ha cambiado el modelo a X». Va por toast, como el resto de
    /// lo efímero (F5.3 §3).
    /// </summary>
    public event Action<string>? Notice;

    /// <summary>Cola de unidades con su estado y su narración. La misma instancia toda la sesión.</summary>
    public ObservableCollection<UnitProgress> Units { get; } = new();

    /// <summary>Hallazgos que van entrando, para la columna 3.</summary>
    public ObservableCollection<Finding> Findings { get; } = new();

    /// <summary>Resumen de cierre, no nulo desde que termina una sesión hasta que empieza otra.</summary>
    public ObservableCollection<SummaryLine> Summary { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _appSlug = string.Empty;

    /// <summary>
    /// <b>El nombre de la aplicación, que es lo que se ENSEÑA</b> (R12). El identificador del hub
    /// —minúsculas, sin espacios— es un dato: sirve para leer y escribir, no para rotular. La
    /// sesión no lo tenía, así que la miga decía «xblast», la cabecera «Lotes · xblast» y el pie
    /// «Auditando xblast» mientras Portafolio e Inventario, dos clics más atrás, decían «XBLAST».
    /// <para>
    /// Con el identificador de respaldo, y no vacío: una aplicación que ya no está en el hub no
    /// tiene nombre que leer, y quedarse sin rótulo sería peor que quedarse con el identificador.
    /// </para>
    /// </summary>
    [ObservableProperty] private string _appName = string.Empty;

    [ObservableProperty] private string _headerText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _unitIndex;
    [ObservableProperty] private int _unitCount;
    [ObservableProperty] private int _currentPassNumber;
    [ObservableProperty] private long _inputTokens;
    [ObservableProperty] private long _outputTokens;
    [ObservableProperty] private long _cacheReadTokens;
    [ObservableProperty] private long _cacheWriteTokens;
    [ObservableProperty] private decimal? _cost;
    [ObservableProperty] private string _costUnit = CostFormat.Unit;

    /// <summary>
    /// El coste con su procedencia: el número, o el motivo por el que no lo hay (F16 §B). Es lo
    /// que permite que el pie diga lo MISMO que el informe de esa misma sesión.
    /// </summary>
    [ObservableProperty] private CostResult _costResult = CostResult.Unavailable(CostUnavailable.TokensMissing);

    /// <summary>Con qué casa se está midiendo, para etiquetar el número con su unidad.</summary>
    [ObservableProperty] private string? _provider;
    [ObservableProperty] private int _calls;

    /// <summary>
    /// Turnos de conversación servidos en la sesión. Es lo que hace legible el desglose de caché del
    /// pie: lo que se escribe por turno es donde se ve si el hilo está haciendo su trabajo.
    /// </summary>
    [ObservableProperty] private int _turns;

    /// <summary>
    /// Adónde va lo que se está gastando, según ocurre (F18 §1). Null hasta la primera medida.
    /// </summary>
    [ObservableProperty] private PromptBudget? _budget;
    [ObservableProperty] private DateTimeOffset? _startedUtc;
    [ObservableProperty] private DateTimeOffset? _endedUtc;
    [ObservableProperty] private string _sessionId = string.Empty;
    [ObservableProperty] private string _reportPath = string.Empty;
    [ObservableProperty] private bool _hasFinished;

    /// <summary>
    /// La sesión NO llegó a completarse: murió al arrancar o reventó a mitad (F5.15).
    /// <para>
    /// Es un tercer estado terminal y no un matiz de <see cref="HasFinished"/>. Sin él, un fallo
    /// dejaba <c>IsRunning=false</c> y <c>HasFinished=false</c>, o sea <c>HasSession=false</c>: el
    /// item del rail desaparecía, «Detener» desaparecía, la pantalla de cierre no se pintaba —está
    /// condicionada a <c>HasFinished</c>— y el mensaje de error se quedaba escrito en una propiedad
    /// que nadie enseñaba. Una sesión zombi con el reloj parado y ni una palabra. Eso es lo que se
    /// vio el 2026-08-26 a las 12:20:06 cuando <c>session.create</c> rechazó el modelo.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    private bool _hasFailed;

    /// <summary>Qué falló, en una frase que el usuario pueda accionar. Vacío si no ha fallado nada.</summary>
    [ObservableProperty] private string _failureMessage = string.Empty;

    /// <summary>
    /// El fallo se arregla eligiendo otro modelo, así que la vista puede ofrecer el atajo a Ajustes.
    /// </summary>
    [ObservableProperty] private bool _failureOffersModelChange;

    /// <summary>
    /// El error del proveedor tal cual, para copiarlo (BUGFIX-CUOTA). Va aparte del mensaje: la
    /// frase dice qué hacer, y esto —tipo, texto y Request ID— es lo que se le pega a quien
    /// administra la organización para que pueda buscar la petición concreta. La vista lo esconde
    /// tras «Ver detalle»: es largo, y desbordado tapaba el resto de la pantalla.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailureDetail))]
    private string _failureDetail = string.Empty;

    public bool HasFailureDetail => FailureDetail.Length > 0;

    /// <summary>
    /// Se ha ejecutado alguna sesión en esta ejecución de la app: corriendo, terminada <b>o
    /// fallida</b>. Lo que decide si hay algo que enseñar en V5 y, con ello, si el rail ofrece el
    /// camino de vuelta.
    /// </summary>
    public bool HasSession => IsRunning || HasFinished || HasFailed;

    /// <summary>Avisa a la carcasa de que hay que repintar el indicador de navegación.</summary>
    public event Action? Changed;

    /// <summary>Sesión terminada, con su resultado. Lo usa el toast de la barra de estado.</summary>
    public event Action<SessionResult>? Completed;

    /// <summary>
    /// La sesión no arrancó o no pudo continuar (F5.15). Lleva la frase accionable. La carcasa la
    /// saca por toast: quien lanza una auditoría suele irse a otra pantalla, y un error que solo
    /// vive en la vista de la sesión es un error que nadie lee.
    /// </summary>
    public event Action<string>? Failed;

    public double Progress => UnitCount == 0 ? 0 : (double)UnitIndex / UnitCount;

    // ------------------------------------------------------------------ ¿está pensando o se cayó?

    /// <summary>
    /// Cuándo llegó la última señal de vida del barrido (F30 §3): un evento de herramienta, un
    /// trozo de texto, una muestra de consumo o un cambio de pasada.
    /// </summary>
    private DateTimeOffset? _lastEventUtc;

    /// <summary>
    /// La línea de «el modelo está escribiendo esta herramienta» que hay viva, si la hay (F30 §2).
    /// Se reescribe con cada elemento nuevo y se suelta en cuanto pasa cualquier otra cosa.
    /// </summary>
    private ActivityEntry? _writingLine;

    /// <summary>
    /// A partir de cuántos segundos sin noticias se dice qué se está esperando.
    /// <para>
    /// <b>Cinco, y no veinte</b> (F30 §2e). §3 los puso en veinte con el argumento de que una
    /// llamada tarda 11,8 s de media y anunciar cada pausa corta sería ruido. El argumento se cayó
    /// en cuanto la línea pasó a decir <b>de qué</b> se espera: «escribiendo el reporte de
    /// hallazgos · 8 s» no es ruido, es la única información que hay en pantalla en ese tramo. Y
    /// con veinte no llegaba a salir nunca en el sitio donde más falta hace —tras la prosa del
    /// modelo—, porque cada delta de texto reinicia el reloj y lo que viene detrás son huecos que
    /// no alcanzan el umbral desde el último.
    /// </para>
    /// </summary>
    public const int QuietSeconds = 5;

    /// <summary>Y a partir de cuántos la espera pasa a ámbar: sigue siendo legal, pero ya es larga.</summary>
    public const int LongWaitSeconds = 90;

    /// <summary>
    /// <b>Cuánto lleva sin llegar nada</b> (F30 §3). Es la diferencia entre «el modelo está
    /// pensando» y «esto se ha caído», que hoy se ven exactamente igual: la pantalla quieta.
    /// <para>
    /// Medido sobre las sesiones reales del hub —14 sesiones, 122 llamadas, 1.440 s— una llamada
    /// tarda <b>11,8 s de media y hasta 48 s</b>. Con esos números, un silencio de 20 s ya merece
    /// una palabra y uno de 90 merece un color; el corte de F19 sigue mandando cuándo se para.
    /// </para>
    /// </summary>
    public TimeSpan SinceLastEvent => _lastEventUtc is { } at && IsRunning
        ? DateTimeOffset.UtcNow - at
        : TimeSpan.Zero;

    /// <summary>
    /// Se está esperando <b>al modelo</b> y ya se nota. Las dos mitades cuentan: que haya pasado el
    /// silencio corto, y que haya una <b>petición en vuelo</b> — mientras Atalaya prepara el suyo o
    /// la sesión ya ha terminado no se espera a nadie, y decirlo sería echarle al modelo un tiempo
    /// que no es suyo.
    /// </summary>
    public bool IsWaiting => IsRunning && _inFlight && SinceLastEvent.TotalSeconds >= QuietSeconds;

    /// <summary>
    /// <b>Qué se estaba haciendo cuando se hizo el silencio</b> (F30 §1c): «tras reportar 11
    /// hallazgos», «tras enviar la pasada 3». Vacío si no hay nada que decir.
    /// <para>
    /// Sin esto, el pie decía «esperando al modelo» y punto, que no distingue el tramo largo de
    /// verdad —el modelo escribiendo los argumentos de una herramienta— de una pausa cualquiera. Y
    /// el tramo importa: es donde está el minuto.
    /// </para>
    /// </summary>
    public string WaitingAfter { get; private set; } = string.Empty;

    /// <summary>La espera es larga: el pie lo dice en ámbar.</summary>
    public bool WaitIsLong => IsRunning && SinceLastEvent.TotalSeconds >= LongWaitSeconds;

    /// <summary>
    /// <b>Hay una petición en vuelo</b> (F30 §1b, corregido en §1c): el turno salió hacia el modelo
    /// y la pasada todavía no ha cerrado.
    /// <para>
    /// <b>Vale para TODOS los tramos, y ahí estuvo el error.</b> §1b lo apagaba en cuanto llegaba
    /// cualquier evento, con lo que la espera solo se contaba desde la entrega hasta la primera
    /// señal — y el tramo largo de verdad no es ése. Un turno con herramientas tiene varios
    /// silencios: mientras el modelo escribe los argumentos de <c>submit_findings</c>, y otra vez
    /// desde el resultado de la herramienta hasta que vuelve a hablar. Los dos son el modelo
    /// generando, y los dos hay que contarlos. Lo que un evento hace es <b>reiniciar el reloj</b>,
    /// no aterrizar el turno; quien lo aterriza es el cierre de la pasada.
    /// </para>
    /// </summary>
    private bool _inFlight;

    /// <summary>
    /// Señal de vida: la llama todo lo que llega del barrido. Un solo sitio, para que no pueda
    /// haber un evento que se pinte y no cuente como actividad.
    /// <para>
    /// <b>Reinicia el reloj y NO cierra el vuelo.</b> Que el modelo haya dicho algo no significa
    /// que haya terminado: entre un evento y el siguiente puede haber cuarenta segundos de
    /// generación, que es exactamente el silencio que esta fase viene a nombrar.
    /// </para>
    /// </summary>
    /// <param name="after">
    /// Qué acaba de pasar, para que el pie pueda decir de qué espera. Vacío deja lo anterior: los
    /// eventos sin nombre —una muestra de consumo— no borran el contexto del último que sí lo tenía.
    /// </param>
    private void Touch(string after = "")
    {
        _lastEventUtc = DateTimeOffset.UtcNow;
        if (after.Length > 0)
        {
            WaitingAfter = after;
        }
    }

    /// <summary>El turno acaba de salir: hay petición en vuelo y el reloj empieza aquí.</summary>
    private void TurnSent(string after)
    {
        _lastEventUtc = DateTimeOffset.UtcNow;
        WaitingAfter = after;
        _inFlight = true;
    }

    /// <summary>
    /// La pasada cerró: ya no hay nada en vuelo. Lo que venga después es Atalaya preparando el
    /// turno siguiente, y eso no se le apunta al modelo.
    /// </summary>
    private void TurnLanded()
    {
        _lastEventUtc = DateTimeOffset.UtcNow;
        WaitingAfter = string.Empty;
        _inFlight = false;
    }

    /// <summary>Línea de la barra de estado inferior mientras corre.</summary>
    /// <summary>
    /// <b>EL PROGRESO SE DICE UNA VEZ</b> (UI-0053). Esta tira decía «Auditando app · unidad 3/6 ·
    /// pasada 2» a 44 px de la barra de la propia vista, que ya dice «Unidad 3 de 6 · 00:07 · 7
    /// llamadas…»: el mismo dato dos veces y escrito de dos maneras, gastando el único renglón que
    /// la carcasa tiene para lo que la vista NO puede decir. El reparto es ese: la unidad y la
    /// pasada son de la vista de sesión —ahí es donde se mira el avance— y la carcasa dice lo que
    /// se necesita saber desde cualquier otra pantalla, que es <b>qué</b> se está auditando.
    /// <para>
    /// El modo exhaustivo se queda: no es progreso, es un aviso de coste (R2 §1). Cuesta el triple
    /// y puede duplicar hallazgos, y quien deja una sesión corriendo mientras mira otra cosa tiene
    /// que poder verlo sin volver a Sesión en vivo.
    /// </para>
    /// </summary>
    public string ProgressLine => !IsRunning
        ? string.Empty
        : $"Auditando {AppName}"
          + (Exhaustive ? $" · {AuditModes.Exhaustive}" : string.Empty);

    /// <summary>
    /// Esta sesión se está barriendo en <b>modo exhaustivo</b> (R2 §1): una petición por pasada.
    /// <para>
    /// Se dice en el pie —junto a la unidad— porque cuesta el triple y puede duplicar hallazgos:
    /// quien mira una sesión correr tiene que poder saber con qué se está pagando. Es lo único que
    /// R2 añade a la línea; D-926 sigue mandando en todo lo demás.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _exhaustive;

    public TimeSpan Elapsed => StartedUtc is null
        ? TimeSpan.Zero
        : (EndedUtc ?? DateTimeOffset.UtcNow) - StartedUtc.Value;

    /// <summary>Coste medio por unidad procesada — la métrica que de verdad manda (D-113).</summary>
    public decimal? CostPerUnit => Cost is { } c && UnitIndex > 0 ? c / UnitIndex : null;

    /// <summary>
    /// Lanza una sesión. Idempotente mientras haya una corriendo: el cerrojo se echa ANTES del
    /// primer await, que es lo que impedía en D-085 que dos disparos casi simultáneos arrancaran
    /// dos sesiones en el mismo segundo.
    /// </summary>
    public Task StartAsync(SessionRequest request, IReadOnlyList<string> displayPaths)
    {
        bool blocked;
        lock (_gate)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            // F6.9: y tampoco si hay un arreglo asistido corriendo. Auditar mientras un agente
            // escribe en el clon es leer código a medio cambiar y publicar hallazgos sobre un
            // estado que no existió nunca. El aviso se da FUERA del cerrojo: pintar la UI con un
            // lock en la mano es cómo se construye un abrazo mortal.
            blocked = !_busy.TryEnter(AgentWork.Auditoria);
            if (!blocked)
            {
                IsRunning = true;
            }
        }

        Reset(request, displayPaths);
        if (blocked)
        {
            Fail(_busy.BusyMessage, offersModelChange: false);
            return Task.CompletedTask;
        }

        return RunAsync(request);
    }

    /// <summary>
    /// Cierra la sesión como FALLIDA (F5.15): un estado terminal visible, no la ausencia de estado.
    /// <para>
    /// Deja las tres cosas que faltaban aquella noche: la frase accionable donde la vista la pinta,
    /// <see cref="HasSession"/> en true para que el rail siga ofreciendo el camino de vuelta, y un
    /// aviso por toast para quien ya se había ido a otra pantalla. El <c>finally</c> de
    /// <see cref="RunAsync"/> se encarga del resto —marca de sesión abierta, reloj, IsRunning— así
    /// que un fallo no deja nada colgando.
    /// </para>
    /// </summary>
    /// <param name="keepStatusMessage">
    /// La sesión llegó a terminar y tiene pantalla de cierre propia (BUGFIX-CUOTA). Ahí el estado ya
    /// dice «cortada por el proveedor» con sus contadores, y el banner de arriba lleva el mensaje
    /// entero: pisar uno con el otro es escribir el mismo párrafo dos veces en la misma pantalla.
    /// </param>
    private void Fail(string message, bool offersModelChange, string? detail = null,
        bool keepStatusMessage = false)
    {
        FailureMessage = message;
        FailureOffersModelChange = offersModelChange;
        FailureDetail = detail ?? string.Empty;
        HasFailed = true;
        if (!keepStatusMessage)
        {
            StatusMessage = message;
        }

        Changed?.Invoke();
        Failed?.Invoke(message);
    }

    /// <summary>
    /// ARCHIVA una sesión terminada (BUGFIX-CIERRE). La pantalla desaparece del rail y deja de
    /// ocupar sitio; el informe y el registro de la sesión siguen donde estaban.
    /// <para>
    /// <b>Cerrar no borra historia.</b> Lo único que se tira es el estado de PANTALLA —los
    /// contadores en vivo, la cola de unidades, el resumen— que solo existía en memoria. Lo que
    /// pasó vive en el hub: su sesión, sus hallazgos y su informe, accesibles desde Informes.
    /// </para>
    /// <para>
    /// Solo cierra lo TERMINAL. Mientras la sesión corre, lo que hay es «Detener», que es otra
    /// cosa: archivar una sesión viva la dejaría corriendo sin ninguna superficie que la enseñe —
    /// exactamente el zombi que F5.15 vino a matar.
    /// </para>
    /// </summary>
    public bool Close()
    {
        if (IsRunning || !HasSession)
        {
            return false;
        }

        OnUi(() =>
        {
            Units.Clear();
            Findings.Clear();
            Summary.Clear();
        });

        HasFinished = false;
        HasFailed = false;
        FailureMessage = string.Empty;
        FailureDetail = string.Empty;
        FailureOffersModelChange = false;
        StatusMessage = string.Empty;
        HeaderText = string.Empty;
        AppSlug = string.Empty;
        AppName = string.Empty;
        SessionId = string.Empty;
        ReportPath = string.Empty;
        StartedUtc = null;
        EndedUtc = null;
        UnitIndex = UnitCount = CurrentPassNumber = Calls = 0;
        InputTokens = OutputTokens = CacheReadTokens = CacheWriteTokens = 0;
        Turns = 0;
        Exhaustive = false;
        Budget = null;
        Cost = null;

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// <b>Vigila la tarea de la sesión</b> (F30 §2c): si termina en excepción, lo dice en vez de
    /// dejar la pantalla girando.
    /// <para>
    /// <b>El agujero que cierra.</b> Quien lanza una sesión la descartaba —<c>_ = StartAsync(…)</c>—,
    /// que es lo normal para algo que no se espera. El problema es lo que pasa cuando falla: los
    /// fallos que <c>RunAsync</c> lanza DENTRO de su <c>try</c> ya se cuentan y se cierran bien,
    /// pero los de antes —construir el coordinador, resolver un servicio— salían por arriba, no los
    /// recogía nadie y la sesión se quedaba con <c>IsRunning</c> en true: el giro puesto, «Detener»
    /// sin nada que detener y ni una línea que dijera qué pasó. Reproducido: basta con que el
    /// contenedor no sepa construir una dependencia.
    /// </para>
    /// </summary>
    public void Observe(Task session) => _ = ObserveAsync(session);

    private async Task ObserveAsync(Task session)
    {
        try
        {
            await session;
        }
        catch (OperationCanceledException)
        {
            // Detener es un final legítimo, no un fallo.
        }
        catch (Exception ex)
        {
            // `Fail` deja el estado terminal visible y suelta el giro; sin esto, la única señal
            // sería una pantalla parada.
            Fail(ex.Message, offersModelChange: false, ex.ToString());
        }
        finally
        {
            if (IsRunning)
            {
                // La red de la red: pase lo que pase, una sesión que ya no corre no puede seguir
                // diciendo que corre.
                IsRunning = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Detener: la parada ordenada de F5.1b. No hay un segundo camino de parada.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        StatusMessage = "Deteniendo tras la unidad actual…";
        Changed?.Invoke();
    }

    private void Reset(SessionRequest request, IReadOnlyList<string> displayPaths)
    {
        OnUi(() =>
        {
            Units.Clear();
            Findings.Clear();
            Summary.Clear();

            // El nombre corto de la cola se calcula sobre el LOTE (F5.3): saber si hace falta
            // añadir un tramo de ruta exige mirar a las demás unidades, no solo a esta.
            IReadOnlyList<string> shortNames = UnitProgress.ShortNames(displayPaths);
            for (int i = 0; i < displayPaths.Count; i++)
            {
                Units.Add(new UnitProgress { Path = displayPaths[i], ShortName = shortNames[i] });
            }
        });

        _new.Clear();
        _confirmed.Clear();
        _resolved.Clear();
        _disputed.Clear();
        _refused.Clear();
        _nonVerifiable.Clear();
        _silenced.Clear();
        _incidents.Clear();
        _currentText = null;
        _currentPass = null;
        _currentUnit = null;
        _unitsDone = 0;

        AppSlug = request.Slug;
        AppName = _hub?.Store.TryReadApp(request.Slug)?.Name is { Length: > 0 } name
            ? name
            : request.Slug;
        HeaderText = $"{request.Mode} · {AppName}";
        UnitCount = displayPaths.Count;
        UnitIndex = 0;
        CurrentPassNumber = 0;
        InputTokens = OutputTokens = CacheReadTokens = CacheWriteTokens = 0;
        Turns = 0;
        Exhaustive = false;
        Budget = null;
        Cost = null;
        Calls = 0;

        // F30 §2e — Y EL PIE SE LIMPIA ENTERO. Estos cuatro se quedaban con lo de la sesión
        // anterior, así que al arrancar una con Copilot el pie enseñaba «incluido en tu suscripción
        // de Claude» hasta que llegaba la primera muestra de consumo — el pie de una sesión
        // hablando de otra, que es peor que no decir nada. Van aquí, con el resto de los
        // contadores, y no en el sitio donde se leen: quien arranca una sesión limpia la pantalla,
        // no quien la pinta.
        Provider = null;
        CostResult = CostResult.Unavailable(CostUnavailable.TokensMissing);
        CostUnit = CostFormat.Unit;

        // Y el estado de espera, que también es del pie: una sesión nueva no espera a nadie
        // todavía, y el reloj de la espera no puede arrancar heredado.
        _lastEventUtc = null;
        _inFlight = false;
        WaitingAfter = string.Empty;
        _writingLine = null;
        lock (_textGate)
        {
            _pendingText.Clear();
            _textFlushQueued = false;
        }

        SessionId = string.Empty;
        ReportPath = string.Empty;
        HasFinished = false;
        HasFailed = false;
        FailureMessage = string.Empty;
        FailureOffersModelChange = false;
        FailureDetail = string.Empty;
        EndedUtc = null;
        StartedUtc = DateTimeOffset.UtcNow;
        // F16 §C: se nombra a QUIEN se va a comprobar. «Comprobando Copilot…» mientras la
        // sesión iba a correr con Claude Code era la misma clase de mentira que el pie del coste.
        StatusMessage = $"Comprobando {_agent().ProviderName}…";
        Changed?.Invoke();
    }

    private async Task RunAsync(SessionRequest request)
    {
        SessionCoordinator coordinator = _coordinatorFactory();
        Subscribe(coordinator);
        bool closedOrderly = false;
        try
        {
            AgentReadiness readiness = await _agent().CheckAsync(CancellationToken.None);
            if (!readiness.Ready)
            {
                Fail(readiness.Message, readiness.Problem == AgentProblem.ModelUnavailable,
                    readiness.Detail);
                return;
            }

            // F5.15: el modelo se resuelve contra la lista REAL de la cuenta antes de crear nada.
            // Un id caducado se sustituye y se dice; sin ningún modelo utilizable no se arranca —
            // dejar que el runtime lo rechace después solo cambia un aviso claro por un fallo feo.
            if (_models is not null)
            {
                ModelResolution resolution = await _models.ResolveAsync(CancellationToken.None);
                if (resolution.Failed)
                {
                    Fail(resolution.Notice ?? CopilotHelp.ModelUnavailable(null), offersModelChange: true);
                    return;
                }

                if (resolution.Notice is { Length: > 0 } notice)
                {
                    Notice?.Invoke(notice);
                }
            }

            StatusMessage = "Auditando…";
            Changed?.Invoke();
            _cts = new CancellationTokenSource();
            SessionResult result = await Task.Run(() => coordinator.RunAsync(request, _cts.Token));

            // El coordinador llegó a su cierre ordenado, que es quien suelta los claims. Sin esta
            // marca, el finally no sabría distinguir «cerró bien» de «reventó», y soltar dos veces
            // los mismos claims no rompe nada pero soltar CERO veces sí (BUGFIX-ACTIVIDAD).
            closedOrderly = true;

            SessionId = result.SessionId.ToString();
            ReportPath = _hub is null ? string.Empty : _hub.HubPaths.ReportFile(AppSlug, SessionId);
            BuildSummary(result);
            StatusMessage = Describe(result);
            HasFinished = true;

            // BUGFIX-CUOTA: la sesión que el proveedor cortó a mitad es las DOS cosas a la vez —
            // terminada (hay trabajo guardado que resumir) y fallida (no cubrió lo que decía). Se
            // marcan las dos: el banner dice por qué se paró, y debajo sigue el resumen de lo que
            // sí se auditó. Enseñar solo el banner tiraría a la basura la única prueba de que ese
            // trabajo existe.
            if (result.Failure is { } failed)
            {
                Fail($"{failed.Message} {failed.Summary}", offersModelChange: false, failed.Detail,
                    keepStatusMessage: true);
            }

            Completed?.Invoke(result);
        }
        catch (HubPublishException hubEx)
        {
            // BUGFIX-PUSH — el hub no contestó a tiempo. Se dice tal cual y no envuelto en «se ha
            // interrumpido por un error»: el motivo ya está escrito para leerse, dice dónde mirar
            // —la red, la cuenta— y no se parece a un fallo del proveedor, que es la otra familia
            // de fallos de esta pantalla. Lo que NO puede pasar es lo de hoy: quedarse girando.
            Fail(hubEx.Message, offersModelChange: false);
        }
        catch (AuditorModelUnavailableException modelEx)
        {
            // F5.15: el fallo con remedio de un clic. Se nombra el modelo y se ofrece Ajustes.
            Fail(modelEx.Message, offersModelChange: true, modelEx.Detail);
        }
        catch (AuditorProviderException providerEx)
        {
            // BUGFIX-CUOTA: cuota, asiento, credenciales, red o desconocido — cada uno ya trae su
            // frase y su remedio desde el clasificador. Aquí no se vuelve a diagnosticar nada: dos
            // sitios decidiendo la causa es como «quota» acabó significando «sin asiento».
            Fail(providerEx.Message, offersModelChange: false, providerEx.Detail);
        }
        catch (Exception ex)
        {
            // Ni siquiera es del proveedor. Se enseña el crudo: inventar una causa es peor.
            Fail($"La sesión se ha interrumpido por un error: {ex.Message}",
                offersModelChange: false, CopilotFailure.Raw(ex));
        }
        finally
        {
            Unsubscribe(coordinator);

            // BUGFIX-ACTIVIDAD. Antes esto era un `_marker.Delete()` a secas, y ahí estaba el fallo:
            // si la sesión murió por una excepción —la cuota agotada, sin ir más lejos—, el cierre
            // ordenado del coordinador no había llegado a soltar los claims, y borrar la marca
            // dejaba también sin nada que encontrar a la recuperación del arranque. Resultado: la
            // tarjeta del Portafolio anunciando «auditando ahora» durante media hora, y el fichero
            // del claim en el hub para siempre.
            //
            // Ahora se sueltan AQUÍ, en el instante del fallo, antes de retirar la marca.
            if (!closedOrderly && _openMarker is { } open && _hub is not null)
            {
                SessionClaims.Release(_hub, open.Slug, open.Units);
            }

            // La marca de sesión abierta se retira SIEMPRE que el proceso siga vivo: si llegamos
            // aquí, esta sesión no necesita recuperación (D-110).
            _marker.Delete();
            _openMarker = null;
            EndedUtc = DateTimeOffset.UtcNow;
            IsRunning = false;
            FoldForLastSession();
            _cts?.Dispose();
            _cts = null;
            _busy.Exit(AgentWork.Auditoria);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// <b>Cómo queda el hilo cuando la sesión ya no corre</b> (F30 §3): todas las unidades abiertas
    /// y, dentro de cada una, <b>solo la última pasada</b>.
    /// <para>
    /// En vivo interesa todo —se está mirando cómo pasa—; en Última sesión interesa dónde acabó
    /// cada unidad, y las pasadas anteriores son historia que se despliega si se busca. Es la regla
    /// que el servicio ya aplicaba EN VIVO desde F12 y que ahí estorbaba: escondía lo que se acababa
    /// de ver.
    /// </para>
    /// </summary>
    private void FoldForLastSession() => OnUi(() =>
    {
        foreach (UnitProgress unit in Units)
        {
            unit.IsExpanded = true;
            for (int i = 0; i < unit.Passes.Count; i++)
            {
                unit.Passes[i].IsExpanded = i == unit.Passes.Count - 1;
            }
        }
    });

    private void Subscribe(SessionCoordinator c)
    {
        c.Started += OnStarted;
        c.UnitPhaseChanged += OnUnitPhase;
        c.PassStarted += OnPassStarted;
        c.PassFinished += OnPassFinished;
        c.UnitFinished += OnUnitFinished;
        c.FindingReported += OnFinding;
        c.TextStreamed += OnText;
        c.UsageUpdated += OnUsage;
        c.ActivityNoted += OnActivityNoted;
    }

    private void Unsubscribe(SessionCoordinator c)
    {
        c.Started -= OnStarted;
        c.UnitPhaseChanged -= OnUnitPhase;
        c.PassStarted -= OnPassStarted;
        c.PassFinished -= OnPassFinished;
        c.UnitFinished -= OnUnitFinished;
        c.FindingReported -= OnFinding;
        c.TextStreamed -= OnText;
        c.UsageUpdated -= OnUsage;
        c.ActivityNoted -= OnActivityNoted;
    }

    /// <summary>
    /// <b>Lo que acaba de pasar, en el hilo de la pasada</b> (F30 §1). Es la mitad que le faltaba a
    /// la narración de una auditoría: hasta aquí, entre una llamada y la siguiente —11,8 s de media
    /// en las sesiones reales del hub, y hasta 48 s— lo único que podía aparecer era la prosa del
    /// modelo, que él decide si escribe. Las herramientas no son opcionales: si el auditor está
    /// trabajando, las llama.
    /// <para>
    /// La frase la pone <see cref="ActivityWording"/> y no esta clase: el texto que llega es la
    /// traza de diagnóstico que ya viaja a las notas de la sesión, y traducirla aquí sería tenerla
    /// escrita dos veces.
    /// </para>
    /// </summary>
    private void OnActivityNoted(ActivityNote note) => PostUi(() =>
    {
        FlushText();
        // F30 §2 — LA LÍNEA DE «SE ESTÁ ESCRIBIENDO» SE REESCRIBE, no se apila. El modelo emite un
        // trozo por elemento completado, así que apilarlas dejaría once líneas casi iguales donde
        // hay un solo gesto. Se sustituye el texto de la que ya hay, y la ejecución la releva por
        // la definitiva.
        if (note.Kind == ActivityNoteKind.ToolWriting)
        {
            // F30 §3 — y la CLASE va con el texto. El tramo empieza en «Razonando…» y sigue en
            // «Reportando hallazgos…»: es la misma línea reescribiéndose —apilar dos cambiaría lo
            // que se narra— pero no es la misma clase de evento, y la burbuja del razonamiento se
            // lee en cursiva. Por eso `Kind` es mutable en la base.
            if (_writingLine is not null && _currentPass?.Entries.Contains(_writingLine) == true)
            {
                _writingLine.Text = note.Text;
                _writingLine.Kind = ActivityWording.KindOf(note);
            }
            else
            {
                _writingLine = ActivityEntry.Event("⚒", note.Text, kind: ActivityWording.KindOf(note));
                Add(_currentPass, _writingLine);
            }

            _currentText = null;

            // F30 §2e — y el pie dice de qué es este tramo. Antes se tocaba el reloj sin cambiar la
            // frase, así que un turno que empieza escribiendo la herramienta sin decir una palabra
            // —el caso normal de una unidad sin hallazgos vivos— dejaba el pie en «esperando al
            // modelo» mientras el modelo llevaba medio minuto escribiendo el reporte.
            Touch(note.Waiting);
            Changed?.Invoke();
            return;
        }

        // Cualquier otra cosa cierra la línea en curso: lo que venga es otro gesto.
        _writingLine = null;

        Add(_currentPass, note.Kind switch
        {
            ActivityNoteKind.Tool => ActivityEntry.Event(
                ActivityWording.GlyphFor(note.Text), ActivityWording.Describe(note.Text),
                kind: ActivityWording.KindOf(note)),
            ActivityNoteKind.Handover => ActivityEntry.Event(
                "→", note.Text, kind: ConversationKind.Entrega),
            _ => ActivityEntry.Event("◆", note.Text, kind: ConversationKind.Hito),
        });

        // Un evento cierra el bloque de texto en curso: lo siguiente que diga el modelo es otra
        // cosa, y pegarlo al párrafo anterior lo haría ilegible.
        _currentText = null;

        // La entrega ABRE el vuelo; los demás eventos solo reinician el reloj y dicen de qué se
        // espera a partir de ahora (F30 §1c). Cerrar el vuelo es cosa del cierre de la pasada.
        if (note.Kind == ActivityNoteKind.Handover)
        {
            TurnSent(ActivityWording.After(note));
        }
        else
        {
            Touch(ActivityWording.After(note));
        }

        Changed?.Invoke();
    });

    private void OnStarted(SessionStarted started)
    {
        SessionId = started.Id.ToString();
        // R2 §1 — la forma de barrer se toma de la SESIÓN y no del ajuste: la sesión ya escribió con
        // cuál empezó, y mover el interruptor mientras corre no puede cambiar lo que el pie dice de
        // ella.
        Exhaustive = started.Exhaustive;
        _openMarker = new OpenSessionMarker
        {
            SessionId = started.Id.ToString(),
            Slug = started.Slug,
            Mode = started.Mode.ToString(),
            Commit = started.Commit,
            By = started.By,
            Machine = started.Machine,
            StartedUtc = started.StartedUtc,
            Units = started.Units.ToList(),
            UnitsDone = 0,
        };
        _marker.Write(_openMarker);
    }

    private void OnUnitPhase(string path, string phase) => OnUi(() =>
    {
        UnitProgress? unit = Units.FirstOrDefault(u => u.Path == path);
        if (unit is null)
        {
            return;
        }

        if (phase == "auditing")
        {
            // F30 §3 — EN VIVO NO SE PLIEGA NADA. El hilo es una sola columna que se lee de arriba
            // abajo, y plegar la unidad anterior mientras la sesión corre esconde lo que el usuario
            // acaba de ver pasar. Lo que se pliega —y solo al terminar— lo decide `FoldForLastSession`.
            _currentUnit = unit;
            unit.IsExpanded = true;
            unit.State = UnitRunState.Auditando;
            UnitIndex = Units.IndexOf(unit) + 1;
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressLine));
            Changed?.Invoke();
        }
    });

    private void OnPassStarted(string path, int pass) => OnUi(() =>
    {
        FlushText();
        Touch();
        UnitProgress? unit = Units.FirstOrDefault(u => u.Path == path);
        if (unit is null)
        {
            return;
        }

        unit.CurrentPass = pass;
        CurrentPassNumber = pass;
        _currentPass = new PassProgress { Index = pass };
        unit.Passes.Add(_currentPass);
        _currentText = null;
        OnPropertyChanged(nameof(ProgressLine));
        Changed?.Invoke();
    });

    private void OnPassFinished(string path, UnitPassRecord record) => OnUi(() =>
    {
        FlushText();
        // La pasada cerró: no queda petición en vuelo (F30 §1c). Lo que venga hasta la entrega
        // siguiente es Atalaya, y son 16 ms.
        TurnLanded();

        PassProgress? pass = _currentPass;
        if (pass is null)
        {
            return;
        }

        string verdicts = record.Confirmed + record.Resolved + record.NonVerifiable + record.Disputed > 0
            ? $" · veredictos: {record.Confirmed} presente"
              + (record.Resolved > 0 ? $" · {record.Resolved} arreglado" : "")
              + (record.NonVerifiable > 0 ? $" · {record.NonVerifiable} no concluyente" : "")
              + (record.Disputed > 0 ? $" · {record.Disputed} disputado" : "")
            : "";

        // F12 §H.2 — EL TITULAR CUENTA LO QUE HIZO LA PASADA, no solo lo que trajo de nuevo. Una
        // pasada de reconciliación que confirmaba siete hallazgos se titulaba «seca», que se lee
        // como «aquí no ha pasado nada»: lo que no aportó fueron NUEVOS, y confirmar siete es
        // trabajo hecho y pagado. Los tres números van siempre, en el mismo orden.
        //
        // R11 §1b — y van en PASTILLAS, no en una frase con puntos medios. Lo que se cuenta es lo
        // mismo; lo que cambia es que ahora se ve sin leer: los nuevos en verde cuando los hay, el
        // resto en neutro, y la pasada seca en ámbar apagado.
        var chips = new List<PassChip>
        {
            new(PassProgress.Counted(record.New, "nuevo", "nuevos"),
                record.New > 0 ? PassTone.Success : PassTone.Neutral),
            new(PassProgress.Counted(record.Confirmed, "confirmado", "confirmados"), PassTone.Neutral),
            new(PassProgress.Counted(record.Disputed, "disputado", "disputados"), PassTone.Neutral),
        };

        if (record.LocationsAdded > 0)
        {
            chips.Add(new PassChip(
                PassProgress.Counted(record.LocationsAdded, "ubicación", "ubicaciones"), PassTone.Neutral));
        }

        if (record.Dry)
        {
            chips.Add(new PassChip("seca", PassTone.Warning));
        }

        pass.Chips = chips;

        // Los veredictos van en las DOS ramas (F5.14). Estaban solo en la de la pasada con
        // aportación, así que el caso más común —una pasada seca en la que el auditor confirmó
        // cinco hallazgos como presentes— se narraba «seca — unidad completa» y nada más: se leía
        // como «aquí no ha pasado nada» cuando habían pasado cinco reconfirmaciones. «Presente» no
        // tiene línea propia por unidad a propósito (sería una fila por hallazgo en cada pasada),
        // pero contarlo agregado es la diferencia entre resumir y callar.
        Add(pass, ActivityEntry.Event(
            record.Dry ? "✓" : "↻",
            kind: ConversationKind.Hito,
            text:
            // F12 §E — una pasada seca ya no cierra la unidad, así que tampoco lo dice: hacen falta
            // DOS seguidas. Y «unidad completa» sobraba de todos modos — una unidad barrida es una
            // unidad de la que el auditor no saca más, que no es lo mismo que una unidad sin
            // defectos.
            (record.Dry
                ? $"Pasada {record.Index} seca — el auditor no aportó nada nuevo"
                : $"Pasada {record.Index}: {record.New} nuevo(s)"
                  + (record.LocationsAdded > 0 ? $", {record.LocationsAdded} ubicación(es) añadida(s)" : ""))
            + verdicts
            + (record.Disputed > 0 && verdicts.Length == 0 ? $" · {record.Disputed} disputado" : "")));
        _currentText = null;
    });

    private void OnUnitFinished(string path, UnitVerdictRecord verdict, UnitUsageBreakdown usage) => OnUi(() =>
    {
        UnitProgress? unit = Units.FirstOrDefault(u => u.Path == path);
        if (unit is null)
        {
            return;
        }

        unit.State = verdict.Verdict switch
        {
            "presupuesto-superado" => UnitRunState.CortadaPorPresupuesto,
            "incompleta" => UnitRunState.Incompleta,
            "cobertura posiblemente incompleta" => UnitRunState.CoberturaIncompleta,
            "no-localizado" => UnitRunState.NoLocalizada,
            _ => UnitRunState.Completa,
        };
        unit.PassCount = verdict.Passes?.Count ?? 0;
        unit.Findings = verdict.Passes?.Sum(p => p.New) ?? 0;
        unit.Cost = usage.Cost;
        unit.Tokens = usage.InputTokens + usage.OutputTokens;
        unit.CurrentPass = 0;

        if (verdict.Verdict is "presupuesto-superado" or "incompleta"
            || verdict.CoverageIncomplete || verdict.RejectedPayloads > 0)
        {
            _incidents.Add($"{path} — {verdict.Verdict}: {verdict.Summary}");
        }

        if (verdict.Verdict == "presupuesto-superado")
        {
            Add(_currentPass, ActivityEntry.Event(
                "✂", $"Cortada por presupuesto — {verdict.Summary}", kind: ConversationKind.Unidad));
        }

        _unitsDone++;
        if (_openMarker is not null)
        {
            _openMarker.UnitsDone = _unitsDone;
            _marker.Write(_openMarker);
        }
    });

    private void OnFinding(Finding finding, string kind) => OnUi(() =>
    {
        FlushText();
        Touch();
        string title = finding.Title;
        string alias = finding.DisplayId ?? finding.Id.ToString();
        string unit = finding.Locations.Count > 0 ? finding.Locations[0].Path : "(sin ubicación)";

        SummaryItem Item(string text) => new(unit, finding.Severity, text, finding.Id);

        switch (kind)
        {
            case "nuevo":
                Findings.Add(finding);
                // SIN el corchete de gravedad delante (R10 §5): la pinta la pastilla del
                // sistema, que es la misma de la cabecera del resumen y la del informe. Escrito
                // aquí era texto plano de 25 px sin color dentro de una línea que sí tenía sitio
                // para la pastilla.
                _new.Add(Item(title));
                Add(_currentPass, ActivityEntry.Event(
                    "＋", $"Hallazgo: {title}", finding.Severity, ConversationKind.Hallazgo));
                break;

            case "ubicaciones":
                Add(_currentPass, ActivityEntry.Event(
                    "⊕", $"Ubicaciones añadidas a {alias}", kind: ConversationKind.Veredicto));
                break;

            case "disputed":
                _disputed.Add(Item($"{alias} «{title}» — el auditor sostiene que nunca fue un defecto"));
                Add(_currentPass, ActivityEntry.Event(
                    "⚖", $"Disputado: {title}", kind: ConversationKind.Veredicto));
                break;

            case "resolutionrefused":
                _refused.Add(Item($"{alias} «{title}» — «arreglado» sin evidencia de cambio, degradado a presente"));
                Add(_currentPass, ActivityEntry.Event(
                    "⚠", $"«Arreglado» sin evidencia de cambio: {title}", kind: ConversationKind.Veredicto));
                break;

            case "resolved":
                _resolved.Add(Item($"{alias} «{title}»"));
                Add(_currentPass, ActivityEntry.Event(
                    "✔", $"Resuelto: {title}", kind: ConversationKind.Veredicto));
                break;

            case "needsreview":
                _nonVerifiable.Add(Item($"{alias} «{title}»"));
                break;

            case "silencerespected":
                _silenced.Add(Item($"{alias} «{title}»"));
                break;

            case "reconfirmed":
                _confirmed.Add(Item($"{alias} «{title}»"));
                break;
        }
    });

    /// <summary>
    /// Texto del agente. Se acumula en la última entrada de texto de la pasada en curso: los
    /// deltas llegan en trozos de pocos caracteres y una fila por trozo haría inmanejable la lista.
    /// </summary>
    // -------------------------------------------------- F30 §2d: el texto no bloquea el flujo

    private readonly object _textGate = new();

    private readonly System.Text.StringBuilder _pendingText = new();

    private bool _textFlushQueued;

    /// <summary>
    /// <b>El texto del modelo, SIN bloquear el hilo que lo lee</b> (F30 §2d).
    /// <para>
    /// <b>La regresión que cierra.</b> Hasta la entrega 1, Claude Code entregaba su texto UNA vez
    /// por mensaje —el evento <c>assistant</c>, ya cerrado—. Al empezar a leer <c>text_delta</c>
    /// pasó a entregarlo <b>token a token</b>, y esto hacía un <c>Dispatcher.Invoke</c>
    /// <b>síncrono</b> por cada uno: a los 64 tokens/s medidos, sesenta y cuatro idas y vueltas al
    /// hilo de interfaz por segundo, cada una tocando una propiedad enlazada y disparando su
    /// maquetación. El bucle de lectura es el MISMO que tiene que consumir <c>message_delta</c>
    /// —las cuentas de F21— y <c>result</c> —el fin del turno—, así que quedarse detrás de la
    /// interfaz es quedarse sin cerrar el turno. La ventana respondía (el hilo de interfaz estaba
    /// bien); lo que no avanzaba era la sesión.
    /// </para>
    /// <para>
    /// <b>Lo que se hace.</b> El trozo se acumula en el hilo que lee —sin marshalear— y se vuelca
    /// de una vez, en diferido. Si ya hay un volcado pendiente no se encola otro: lo que llegue
    /// mientras tanto viaja en el mismo. El orden se conserva porque el volcado es uno y escribe lo
    /// acumulado en el orden en que llegó, y porque cualquier otro evento <b>drena primero</b> lo
    /// que haya pendiente antes de escribir lo suyo.
    /// </para>
    /// </summary>
    private void OnText(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        bool encolar;
        lock (_textGate)
        {
            _pendingText.Append(chunk);
            encolar = !_textFlushQueued;
            _textFlushQueued = true;
        }

        // Señal de vida sin pasar por la interfaz: es un campo, no una escritura de pantalla.
        //
        // F30 §2d — y deja apuntado que lo último fue TEXTO. Con eso el pie puede nombrar el
        // tramo que viene después: el silencio que sigue al último delta de texto es, medido en
        // D-1014, el modelo escribiendo los argumentos de la herramienta. Copilot no publica esos
        // argumentos (§2b), así que ahí no hay nada que contar — pero sí se puede decir qué está
        // pasando, que es lo que hace falta para no creer que se ha caído.
        Touch(ActivityWording.AfterText);

        if (encolar)
        {
            PostUi(FlushText);
        }
    }

    /// <summary>
    /// Vuelca de una vez lo que el modelo lleva escrito. Corre SIEMPRE en el hilo de interfaz: lo
    /// llama el volcado diferido, y lo llaman los demás eventos antes de escribir lo suyo para que
    /// el hilo no se desordene.
    /// </summary>
    private void FlushText()
    {
        string text;
        lock (_textGate)
        {
            _textFlushQueued = false;
            if (_pendingText.Length == 0)
            {
                return;
            }

            text = _pendingText.ToString();
            _pendingText.Clear();
        }

        if (_currentText is null)
        {
            _currentText = ActivityEntry.Text_(text);
            Add(_currentPass, _currentText);
        }
        else
        {
            _currentText.Text += text;
        }

        Changed?.Invoke();
    }

    private void OnUsage(LiveUsage u) => PostUi(() =>
    {
        FlushText();
        Touch();
        InputTokens = u.InputTokens;
        OutputTokens = u.OutputTokens;
        CacheReadTokens = u.CacheReadTokens;
        CacheWriteTokens = u.CacheWriteTokens;
        CostResult = u.Cost;
        Cost = u.Cost.Credits;
        Provider = u.Provider;
        Calls = u.Calls;
        Turns = u.Turns;
        CostUnit = CostFormat.BillingUnit;
        Budget = u.Budget;
        OnPropertyChanged(nameof(CostPerUnit));
    });

    private void Add(PassProgress? pass, ActivityEntry entry)
    {
        if (pass is not null)
        {
            pass.Entries.Add(entry);
            return;
        }

        // Fuera de una pasada (arranque, cierre): cuelga de la última que hubiera, y si no hay
        // ninguna se descarta en vez de perder el orden de la narración.
        _currentUnit?.Passes.LastOrDefault()?.Entries.Add(entry);
    }

    /// <summary>
    /// Pantalla de cierre: cada contador con su explicación y su desglose. Ningún número sin causa
    /// — la disputa invisible de D-114 fue el último aviso.
    /// </summary>
    private void BuildSummary(SessionResult result) => OnUi(() =>
    {
        SessionCounters c = result.Counters;
        Summary.Clear();

        void Line(string label, int count, string explanation, List<string> details, bool warning = false)
        {
            if (count == 0 && details.Count == 0)
            {
                return;
            }

            Summary.Add(new SummaryLine
            {
                Label = label,
                Count = count,
                Explanation = explanation,
                Details = details.ToList(),
                IsWarning = warning,
            });
        }

        // La misma línea, con el desglose AGRUPADO POR CLASE (F12 §H.1).
        void Findings_(string label, int count, string explanation, List<SummaryItem> items, bool warning = false)
        {
            if (count == 0 && items.Count == 0)
            {
                return;
            }

            Summary.Add(new SummaryLine
            {
                Label = label,
                Count = count,
                Explanation = explanation,
                Groups = SummaryLine.GroupOf(items),
                IsWarning = warning,
            });
        }

        Findings_("Nuevos", c.New, "Hallazgos que no estaban en el baseline de la unidad.", _new);
        Findings_("Confirmados", c.Confirmed, "El auditor los declaró presentes; sigue contando la máquina de confianza.", _confirmed);
        Findings_("Resueltos", c.Resolved,
            "Cerrados con evidencia de cambio: la unidad cambió desde la última vez que se vieron.", _resolved);
        Findings_("Disputados", c.Disputed,
            "El auditor sostiene que nunca fueron un defecto. NO están resueltos: los decides tú en Hallazgos.",
            _disputed, warning: true);
        Findings_("«Arreglado» sin evidencia", c.ResolutionsRefused,
            "El auditor los dio por arreglados, pero la unidad no había cambiado: degradados a presente.",
            _refused, warning: true);
        Findings_("No concluyentes", c.NoVerificables,
            "El auditor miró la unidad y no pudo decidir. No es una confirmación: quedan marcados "
            + "para revisión, con el paso siguiente escrito en su ficha.", _nonVerifiable);
        Findings_("Silenciados detectados", c.SilencedRespected,
            "Siguen presentes, pero el silencio es una decisión humana y el auditor no la revoca.", _silenced);
        Line("Ubicaciones añadidas", c.LocationsAdded,
            "Un defecto sistémico es UN hallazgo con varias ubicaciones.", new List<string>());
        // F5.12: nunca supresión invisible. Si el auditor se calló algo por un patrón silenciado,
        // la pantalla de cierre lo dice y nombra el patrón — sin esto, una sesión con patrones se
        // leería igual que una unidad limpia.
        Line("Suprimidos por patrón", c.SuppressedByPattern,
            "El auditor no los reportó por corresponder a un tipo de problema silenciado en esta aplicación.",
            result.SuppressionsByPattern
                .Select(t => $"{t.PatternId} · {t.Exemplar} — {t.Count} detección(es)")
                .ToList());
        Line("Incidencias por unidad", _incidents.Count,
            "Unidades incompletas, cortadas por presupuesto o con payloads rechazados.", _incidents, warning: true);

        // BUGFIX-CUOTA: la línea que explica por qué el barrido no llegó al final. Va con las
        // unidades que se quedaron sin mirar, que es el número que le falta a quien lo lee.
        if (result.Failure is { } failed)
        {
            Summary.Add(new SummaryLine
            {
                Label = "Cortada por el proveedor",
                Count = failed.UnitsTotal - failed.UnitsDone,
                Explanation = failed.Message + " " + failed.Summary,
                Details = Units.Where(u => u.State == UnitRunState.Pendiente).Select(u => u.Path).ToList(),
                IsWarning = true,
            });

            foreach (UnitProgress pending in Units.Where(u => u.State == UnitRunState.Pendiente))
            {
                pending.State = UnitRunState.Detenida;
            }
        }

        if (result.Interrupted && result.Failure is null)
        {
            Summary.Add(new SummaryLine
            {
                Label = "Sesión detenida",
                Count = UnitCount - UnitIndex,
                Explanation = "Se detuvo antes de cubrir todas sus unidades. Lo auditado hasta la parada está guardado.",
                Details = Units.Where(u => u.State == UnitRunState.Pendiente).Select(u => u.Path).ToList(),
                IsWarning = true,
            });

            foreach (UnitProgress pending in Units.Where(u => u.State == UnitRunState.Pendiente))
            {
                pending.State = UnitRunState.Detenida;
            }
        }
    });

    private string Describe(SessionResult result)
    {
        SessionCounters c = result.Counters;
        return (result.Failure is not null
                ? "Sesión cortada por el proveedor; lo auditado queda guardado."
                : result.Interrupted ? "Sesión detenida; lo auditado queda guardado." : "Sesión completada.")
            + $" Nuevos {c.New}, confirmados {c.Confirmed}, resueltos {c.Resolved}, "
            + $"silenciados respetados {c.SilencedRespected}."
            + (c.NoVerificables > 0 ? $" {c.NoVerificables} no verificables (marcados para revisión)." : "")
            + (c.Disputed > 0
                ? $" ⚖ {c.Disputed} disputado(s): el auditor sostiene que nunca fueron defecto; "
                  + "no se han resuelto, los decides tú en Hallazgos."
                : "")
            + (c.ResolutionsRefused > 0
                ? $" ⚠ {c.ResolutionsRefused} «arreglado» sin evidencia de cambio, degradado(s) a presente."
                : "")
            + (result.IncompleteUnits > 0
                ? $" ⚠ {result.IncompleteUnits} unidad(es) incompleta(s): el auditor dejó hallazgos sin veredicto."
                : "")
            + (result.ReachedZeroPending ? " Ciclo sin pendientes." : "")
            // F9.2 §2: cerrar no maquilla. Si el codigo se movio mientras duraba el ciclo, la
            // pantalla de cierre lo dice — es lo que el ciclo siguiente hereda como pendiente.
            + (result.CycleClosed && result.CycleAging.Sentence is { } aged ? " " + aged : "");
    }

    /// <summary>
    /// <b>El único camino de escritura de la conversación</b> (BUGFIX-RELEASE §1, F30 §3). Marshalea
    /// y, sobre todo, escribe <b>una a la vez</b>: la reentrada deja de ser reentrada y pasa a ser
    /// el siguiente elemento de la cola. Es la misma pieza que usa el arreglo asistido.
    /// <para>
    /// <see cref="ConversationWrites.Post"/> es el camino de lo que emite el hilo que lee al
    /// proveedor —el texto, el consumo y las líneas del hilo—: <b>no espera</b>, porque un proceso
    /// cuya tubería de salida se llena se bloquea escribiendo (F30 §2e). <see cref="ConversationWrites.Send"/>
    /// es el de lo demás, y las dos entran en la MISMA cola y en la misma prioridad, que es lo que
    /// conserva el orden: con esto en <c>Normal</c>, el cierre de una pasada adelantaría por la
    /// izquierda a las líneas que el agente acababa de mandar.
    /// </para>
    /// </summary>
    private readonly ConversationWrites _writes = new();

    private void PostUi(Action action) => _writes.Post(action);

    private void OnUi(Action action) => _writes.Send(action);
}
