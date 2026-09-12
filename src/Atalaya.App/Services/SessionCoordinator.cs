using Atalaya.Domain;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage.Sync;

namespace Atalaya.App.Services;

/// <summary>What to audit (§5.1–5.3).</summary>
/// <param name="ConfirmedUnits">
/// Cuántas unidades vio y aceptó el usuario antes de lanzar (F5.13). Null = nadie lo declaró y no
/// hay nada que comprobar (recuperaciones, tests antiguos, cualquier camino que no venga de la
/// barra de selección). Con valor, el coordinador se niega a auditar más de eso.
/// </param>
/// <param name="Trigger">
/// Qué provocó el lanzamiento (F9 §6). Solo se GUARDA: no cambia nada de cómo se audita, y sirve
/// para que Métricas pueda algún día distinguir la cobertura inicial del mantenimiento sin tener
/// que reinterpretar sesiones antiguas.
/// </param>
public sealed record SessionRequest(
    string Slug, AuditMode Mode, IReadOnlyList<string> UnitPaths, int? ConfirmedUnits = null,
    SessionTrigger Trigger = SessionTrigger.Manual);

/// <summary>
/// La sesión se ha negado a arrancar porque iba a auditar MÁS de lo que el usuario aceptó (F5.13).
/// <para>
/// Es la salvaguarda de última línea del incidente del 2026-08-26: una casilla de módulo mal
/// interpretada convirtió «una clase» en «un módulo entero» y la auditoría salió sin que nadie la
/// hubiera aceptado. La corrección de fondo es que el contador y la lista de lanzamiento sean el
/// mismo método; esto es lo que impide que un desajuste futuro se pague en tokens en vez de en un
/// mensaje de error.
/// </para>
/// </summary>
public sealed class LaunchMismatchException : Exception
{
    public LaunchMismatchException(int confirmed, int actual)
        : base($"Lanzamiento abortado: se confirmaron {confirmed} unidad(es) y la sesión iba a auditar "
               + $"{actual}. No se ha llamado al modelo ni se ha gastado nada. Vuelve al Inventario y "
               + "revisa la selección.")
    {
        Confirmed = confirmed;
        Actual = actual;
    }

    public int Confirmed { get; }

    public int Actual { get; }
}

/// <summary>
/// La sesión acaba de arrancar (F5.2). Identidad y unidades que va a tocar: es lo que necesita la
/// marca de sesión abierta para poder recuperarla si el proceso muere de golpe (D-110).
/// </summary>
public sealed record SessionStarted(
    Ulid Id,
    string Slug,
    AuditMode Mode,
    string Commit,
    string By,
    string Machine,
    DateTimeOffset StartedUtc,
    IReadOnlyList<string> Units,
    bool Exhaustive = false);

/// <summary>
/// Por qué murió una sesión que ya había empezado a auditar (BUGFIX-CUOTA).
/// <para>
/// Va en el resultado y no como excepción a propósito: una excepción que sube desde el barrido se
/// lleva por delante el cierre ordenado —el registro de la sesión, las marcas del inventario, la
/// liberación de los claims y el informe—, y eso fue exactamente lo que pasó al agotarse la cuota:
/// tres unidades auditadas y pagadas, y ni una línea escrita en el hub.
/// </para>
/// </summary>
/// <param name="Problem">La clasificación del proveedor, para que la vista ofrezca el remedio que toca.</param>
/// <param name="Message">La frase accionable.</param>
/// <param name="Detail">El error crudo del proveedor, copiable. Vacío si no lo hubo.</param>
/// <param name="UnitsDone">Cuántas unidades se llegaron a auditar antes del corte.</param>
/// <param name="UnitsTotal">Cuántas pedía la sesión.</param>
public sealed record SessionFailure(
    AgentProblem Problem, string Message, string? Detail, int UnitsDone, int UnitsTotal)
{
    /// <summary>Lo que se lee en el resumen: qué se salvó y qué no.</summary>
    public string Summary => UnitsDone == 0
        ? "No se auditó ninguna unidad."
        : $"Se auditaron {UnitsDone} de {UnitsTotal} unidad(es) antes del corte, y lo hecho hasta "
          + "ahí está guardado: los hallazgos remitidos siguen en el hub.";
}

/// <summary>Outcome of a session run.</summary>
public sealed record SessionResult(Ulid SessionId, SessionCounters Counters, bool ReachedZeroPending)
{
    /// <summary>True when this session actually triggered the cycle close (§5.1).</summary>
    public bool CycleClosed { get; init; }

    /// <summary>
    /// El proveedor tumbó la sesión a mitad del barrido (BUGFIX-CUOTA). No null significa que la
    /// sesión NO cubrió lo que decía cubrir — pero lo que llegó a auditarse está guardado y se
    /// resume igual: descartar trabajo ya pagado porque el último tramo falló sería tirar dinero.
    /// </summary>
    public SessionFailure? Failure { get; init; }

    /// <summary>
    /// Lo que quedaba envejecido en el momento del cierre (F9.2 §2). Solo tiene contenido cuando
    /// esta sesión cerró el ciclo; la pantalla de cierre lo dice tal cual.
    /// </summary>
    public CycleAging CycleAging { get; init; } = CycleAging.None;

    /// <summary>
    /// El cierre entero, cuando lo hubo (F12 §G): con qué cobertura cerró, cuánto sembró y dónde
    /// está su informe. <see cref="CycleClosed"/> y <see cref="CycleAging"/> siguen siendo lo que
    /// eran; esto es lo que hace posible AVISAR del cierre en vez de dejarlo pasar en silencio.
    /// </summary>
    public CycleCloseResult CycleClose { get; init; } = CycleCloseResult.NotClosed;

    /// <summary>
    /// Unidades en las que el auditor dejó hallazgos existentes sin veredicto (F4). No bloquea la
    /// sesión, pero es visible: esos hallazgos no se han tocado y hay que volver sobre ellos.
    /// </summary>
    public int IncompleteUnits { get; init; }

    /// <summary>
    /// Qué patrón silenciado suprimió cuánto (F5.12). Viaja en el resultado —y no solo en la
    /// sesión guardada— para que la pantalla de cierre pueda nombrarlos sin releer el hub.
    /// </summary>
    public IReadOnlyList<PatternSuppressionTally> SuppressionsByPattern { get; init; }
        = Array.Empty<PatternSuppressionTally>();

    /// <summary>
    /// El usuario pulsó «Detener» (F5.1b). La sesión se cierra igualmente —registro, informe,
    /// claims liberados y push— con lo que se llevara auditado; simplemente no cubrió todo y no
    /// cierra ciclo.
    /// </summary>
    public bool Interrupted { get; init; }
}

/// <summary>
/// Orchestrates a full audit session end-to-end (§5.1): claims → per-unit agent audit with live
/// ingestion y reconciliación por el auditor → inventory update → session + report → commit/push.
/// UI-agnostic; V5 subscribes to its events.
/// <para>
/// F4: NO hay resolución implícita. Un hallazgo previo solo cambia de estado si el auditor emite
/// un veredicto explícito sobre su ULID (<c>report_verdicts</c>). Los que quedan sin veredicto
/// dejan la unidad marcada como <c>incompleta</c> y permanecen intactos.
/// </para>
/// </summary>
public sealed class SessionCoordinator
{
    private readonly HubContext _hub;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly MachineConfigStore _machines;
    private readonly IUlidFactory _ulids;
    private readonly IAuditorProvider _agent;
    private readonly SettingsService _settings;
    private readonly CycleService? _cycles;
    private readonly StatusExporter? _statusExporter;
    private readonly DisplayIdService? _aliases;
    private readonly DirectiveService? _directives;

    /// <param name="directives">
    /// Quien lee las convenciones del proyecto del clon (F7). Opcional para no romper a quien
    /// construya el coordinador a mano; en la aplicación va siempre puesto. Sin él, la auditoría
    /// se comporta exactamente como antes de F7: sin sección de directivas.
    /// </param>
    public SessionCoordinator(
        HubContext hub, FindingIngestionService ingestion, ReconciliationService reconciliation,
        MachineConfigStore machines, IUlidFactory ulids, IAuditorProvider agent,
        SettingsService settings,
        CycleService? cycles = null, StatusExporter? statusExporter = null,
        DisplayIdService? aliases = null, DirectiveService? directives = null)
    {
        _hub = hub;
        _ingestion = ingestion;
        _reconciliation = reconciliation;
        _machines = machines;
        _ulids = ulids;
        _agent = agent;
        _settings = settings;
        _cycles = cycles;
        _statusExporter = statusExporter;
        _aliases = aliases;
        _directives = directives;
    }

    /// <summary>
    /// <b>La regla de las variantes, encendida</b> (F24). Es una palanca de MEDIDA, no un ajuste:
    /// en producción vale siempre <c>true</c> y nadie la toca. Existe por la misma razón que
    /// <c>CutOnUnitDone</c> en F21 —para poder correr la misma tanda con la regla puesta y quitada
    /// y enseñar la diferencia en pasadas y llamadas—, y vive aquí y no en los ajustes porque no es
    /// una decisión del usuario: es la línea contra la que se compara.
    /// <para>
    /// Apagarla quita las DOS capas a la vez —el contrato del prompt y el filtro de la puerta—,
    /// que es lo que hace la comparación honesta: media medida diría que una capa hace el trabajo
    /// de la otra.
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>Cómo se le pide al auditor que mire</b> (M1). Palanca de MEDIDA del banco: en producción
    /// vale <see cref="AuditStyle.Libre"/> y el prompt sale byte a byte como siempre. Existe para
    /// poder correr el brazo estructurado —recorrido miembro × familia, identidad
    /// <c>(regla, miembro)</c>— sin tocar nada del producto, que es la condición de que M1 sea una
    /// medida y no una fase.
    /// </summary>
    public AuditStyle Style { get; init; } = AuditStyle.Libre;

    private bool _threadWarned;

    /// <summary>
    /// Se avisa una vez por sesión cuando el proveedor no sabe hilar
    /// (<see cref="IThreadedAuditor"/>) y la unidad ha corrido por el camino de respaldo, una
    /// petición por pasada. Existe para que un barrido caro no lo parezca por casualidad: la
    /// diferencia entre los dos caminos es pagar el andamiaje una vez o tantas veces como pasadas.
    /// </summary>
    public event Action<string>? ThreadUnavailable;

    public event Action<string, string>? UnitPhaseChanged;   // (path, phase)

    /// <summary>
    /// <b>Lo que está pasando, según pasa</b> (F30 §1): una llamada a herramienta que acaba de
    /// ejecutarse, o un hito de la propia Atalaya —un corte que no se pudo hacer, una reanudación
    /// fallida—.
    /// <para>
    /// <b>Por qué hacía falta.</b> Todo esto ya se apuntaba: las herramientas en
    /// <c>SessionToolbox.ToolCallLog</c> y los hitos directamente en <c>session.Notes</c>. Pero las
    /// dos cosas se vuelcan al CERRAR la pasada, así que durante los minutos que dura una llamada
    /// —medido sobre las sesiones reales del hub: <b>11,8 s de media y hasta 48 s</b> entre una y
    /// la siguiente— la pantalla no tenía nada que enseñar salvo la prosa del modelo, que es
    /// opcional para él. Las herramientas no lo son.
    /// </para>
    /// <para>
    /// Es puramente aditivo y no cuesta un token: no cambia el prompt, no añade llamadas y no le
    /// pide al modelo que hable más. Enseña lo que la aplicación ya sabía y se guardaba para el
    /// final.
    /// </para>
    /// </summary>
    public event Action<ActivityNote>? ActivityNoted;

    // ---- Superficie de OBSERVACIÓN (F5.2) ----
    // Eventos puramente aditivos: emiten datos que el coordinador ya calculaba y no cambian
    // ninguna decisión. Existen porque V5 necesita narrar el barrido pasada a pasada, y antes
    // esa información solo llegaba a las notas de la sesión cuando ya había terminado.

    /// <summary>La sesión arranca: identidad y unidades reclamadas. Sirve para la marca de sesión abierta.</summary>
    public event Action<SessionStarted>? Started;

    /// <summary>(unidad, número de pasada) al empezar cada pasada del barrido.</summary>
    public event Action<string, int>? PassStarted;

    /// <summary>
    /// Cuántas pasadas SECAS SEGUIDAS cierran una unidad (F12 §E).
    /// <para>
    /// Era una. En el banco de pruebas de F12, una segunda auditoría encontró un hallazgo que la
    /// primera no vio: el barrido había parado en 3 de 5 pasadas porque la tercera vino seca. Con
    /// un modelo no determinista, «esta pasada no vio nada nuevo» no es «no queda nada» — es una
    /// muestra, y una muestra sola no es convergencia.
    /// </para>
    /// <para>
    /// <b>El techo sigue mandando.</b> Se pide <c>min(2, maxPassesPerUnit)</c>: con un tope de 1,
    /// la única pasada que cabe es la que hay, y exigir dos secas convertiría cada unidad en
    /// «cobertura posiblemente incompleta» por una condición que el tope hace inalcanzable. Quien
    /// fija el tope decide cuánto está dispuesto a pagar; esto decide cuándo se para dentro de él.
    /// </para>
    /// </summary>
    private const int DryPassesToFinish = 2;

    /// <summary>(unidad, registro de la pasada) al cerrarla, con sus contadores y si quedó seca.</summary>
    public event Action<string, UnitPassRecord>? PassFinished;

    /// <summary>(unidad, veredicto, consumo) al cerrar una unidad entera.</summary>
    public event Action<string, UnitVerdictRecord, UnitUsageBreakdown>? UnitFinished;

    /// <summary>(hallazgo, qué le pasó: nuevo | reconfirmed | resolved | needsreview | silencerespected).</summary>
    public event Action<Finding, string>? FindingReported;
    public event Action<string>? TextStreamed;
    /// <summary>
    /// El consumo acumulado: tokens y el coste YA RESUELTO —con su número o con su motivo— más el
    /// proveedor con el que se está midiendo. Viaja el <see cref="CostResult"/> entero y no un
    /// <c>decimal?</c> porque el pie tiene que poder decir POR QUÉ no hay número, y un nulo suelto
    /// obliga a inventarse una explicación en la vista (F16 §B).
    /// <para>
    /// Y viajan los CUATRO tipos de token, no solo entrada y salida (F16-RETOQUE §1): cuando la
    /// casa no factura, los tokens son lo único que el pie puede enseñar, y con Claude Code la
    /// caché es la parte gruesa. Se agrupan en un registro porque siete argumentos posicionales
    /// son siete oportunidades de cruzar dos <c>long</c> sin que el compilador diga nada.
    /// </para>
    /// </summary>
    public event Action<LiveUsage>? UsageUpdated;

    /// <summary>
    /// Las tarifas del hub, releídas en cada muestra. Es barato —un JSON pequeño— y evita que una
    /// sesión larga siga midiendo con una tabla que alguien ya corrigió.
    /// </summary>
    private ModelRateTable? ModelRates()
    {
        try
        {
            return _hub.Store.TryReadModelRates();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<SessionResult> RunAsync(SessionRequest request, CancellationToken ct)
    {
        AppConfig app = _hub.Store.TryReadApp(request.Slug)
            ?? throw new InvalidOperationException($"App '{request.Slug}' no existe.");
        string? clone = _machines.Load().ClonePathFor(request.Slug);
        if (string.IsNullOrWhiteSpace(clone) || !Directory.Exists(clone))
        {
            throw new InvalidOperationException("No hay clon local configurado para esta app en esta máquina.");
        }

        InventoryCycle inventory = _hub.Store.TryReadInventory(request.Slug, app.CurrentCycle)
            ?? new InventoryCycle { CycleN = app.CurrentCycle };

        var units = ResolveUnits(request, inventory);

        // SALVAGUARDA DE ÚLTIMA LÍNEA (F5.13). Va aquí —antes del sello, antes de publicar claims,
        // antes de cualquier llamada al agente— porque su única razón de existir es que un
        // desajuste entre lo confirmado y lo lanzado NO cueste dinero. Se compara contra las dos
        // listas: la que llegó en la petición y la que de verdad se va a auditar tras resolverla
        // contra el inventario. Ninguna puede superar lo que el usuario aceptó.
        GuardAgainstUnconfirmedScope(request, units.Count);

        string commit = GitInfo.HeadSha(clone);
        string by = _hub.ResolveIdentity().Name;
        Ulid sessionId = _ulids.NewUlid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var session = new AuditSession
        {
            Id = sessionId,
            AppSlug = request.Slug,
            Mode = request.Mode,
            Trigger = request.Trigger,
            By = by,
            Machine = Environment.MachineName,
            StartedUtc = now,
            Commit = commit,
            CycleN = app.CurrentCycle,
            // F17: la lupa del ciclo queda escrita en la sesión, que es lo que el informe lee.
            Theme = inventory.Theme,
            Model = _agent.ModelName,
            Provider = _agent.ProviderId,
            MaxPassesPerUnit = Math.Max(1, _settings.Current.MaxPassesPerUnit),
            // R2 §1 — la forma de barrer queda escrita en la sesión, como el tope: sin ella, un
            // coste tres veces mayor no se puede distinguir de un modelo que se portó mal, y
            // Métricas no podría separar el gasto de las dos formas. Se lee UNA vez, al arrancar:
            // cambiar el interruptor a mitad de un barrido no toca la sesión en curso.
            Exhaustive = _settings.Current.ExhaustiveSweep,
        };

        // «Detener» solo podia actuar dentro del bucle de unidades: todo lo previo (publicar
        // claims, que hace commit+push) es incancelable, asi que pulsar Detener durante esa fase
        // no hacia nada visible. Estos dos cortes hacen que la sesion aborte en cuanto la fase
        // termina, en vez de seguir y auditar la unidad igualmente.
        //
        // F5.1b: y detener ya NO aborta la sesion a medio cerrar. Hasta aqui, cancelar hacia que
        // RunAsync lanzara OperationCanceledException y se saltara TODO lo posterior al bucle:
        // el registro de sesion, el informe, la liberacion de claims y el commit+push. Los
        // hallazgos, en cambio, ya estaban escritos (la ingesta persiste en vivo), asi que una
        // sesion detenida dejaba el hub mutado sin ninguna traza de quien lo hizo — justo lo que
        // se vio el 2026-08-25 a las 12:13 local: un hallazgo confirmado sin fichero de sesion.
        // Ahora una parada es un final ordenado: se cierra con lo que se llevara hecho.
        Started?.Invoke(new SessionStarted(
            sessionId, request.Slug, request.Mode, commit, by, Environment.MachineName, now,
            units.Select(u => u.Path).ToList(), session.Exhaustive));

        // F30 §1 — la unidad y la pasada en curso, para poder situar cada llamada a herramienta y
        // cada hito en el hilo de actividad. La toolbox no las conoce (ni tiene por qué: su trabajo
        // es validar y persistir), así que las lleva quien conduce el barrido.
        //
        // BUGFIX-PUSH — y se declaran AQUÍ ARRIBA, antes del primer push, porque el primer hito que
        // hay que narrar es justamente ése: publicar las reservas. Estaban doscientas líneas más
        // abajo y por eso el arranque no tenía voz.
        string activityUnit = string.Empty;
        int activityPass = 0;
        void Note(ActivityNoteKind kind, string text)
            => ActivityNoted?.Invoke(new ActivityNote(activityUnit, activityPass, kind, text));

        bool stopped = ct.IsCancellationRequested;
        SessionFailure? providerFailure = null;
        if (!stopped)
        {
            PublishClaims(request.Slug, units, inventory, by, Note, ct);
            stopped = ct.IsCancellationRequested;
        }

        var newFindings = new List<Finding>();
        void OnFinding(Finding f, string kind)
        {
            if (kind == "nuevo")
            {
                newFindings.Add(f);
            }

            FindingReported?.Invoke(f, kind);
        }

        void OnText(string t) => TextStreamed?.Invoke(t);

        // Hito 1a: per-unit breakdown. The coordinator owns which unit is "current" so the
        // usage handler can attribute each SDK sample to the right row.
        // Hito 1c: also enforces the per-unit token budget from Thresholds.MaxTokensPerUnit —
        // when tripped, the current unit's CTS is cancelled and the unit is closed as
        // "presupuesto-superado", but the SESSION continues with the next unit.
        UnitUsageBreakdown? currentBreakdown = null;
        CancellationTokenSource? unitCts = null;
        bool budgetTripped = false;
        // Cuál de los dos techos saltó, para poder decirlo: un «presupuesto superado» sin nombrar
        // el techo obliga a adivinar si el agente gastó mucho o dio muchas vueltas (N-2).
        string? trippedBy = null;
        long maxTokensPerUnit = Math.Max(0, app.Thresholds.MaxTokensPerUnit);
        // F19 §3 — el techo de LLAMADAS por pasada. Mide lo que de verdad multiplica el coste:
        // cada llamada reenvía el prompt entero. Con lo medido en F19 —2 llamadas por pasada, 3
        // con lectura de firmas— doce es holgura de sobra y corta un bucle mucho antes que el
        // techo de tokens. 0 lo desactiva.
        int maxCallsPerPass = Math.Max(0, app.Thresholds.MaxCallsPerPass);
        // F5.1: el tope del barrido es un ajuste de ESTA máquina (Ajustes), no de app.json — el
        // barrido gasta los tokens del asiento de quien lanza la sesión. Queda registrado en la
        // sesión y en el informe para que «cobertura posiblemente incompleta» se lea contra él.
        int maxPasses = session.MaxPassesPerUnit;

        // F4.1 — DECISIÓN: MaxTokensPerUnit se aplica POR PASADA, no al barrido completo.
        // El barrido de 3 pasadas del 2026-08-25 gastó 224,5 k de los 300 k del tope, así que
        // medirlo contra el barrido entero habría cortado unidades sanas por el mero hecho de
        // barrerlas. El techo real por unidad pasa a ser MaxTokensPerUnit × MaxPassesPerUnit
        // (900 k por defecto) en el peor caso; con consolidación no debería acercarse.
        long passInput = 0;
        long passOutput = 0;

        // F18 §1 — el consumo de la PASADA, que hasta aquí no existía como dato. El desglose por
        // unidad (Hito 1a) no distingue abrir una unidad de insistir sobre ella, y esa es
        // justamente la diferencia que decide si el gasto está en el barrido o en el prompt.
        int passCalls = 0;
        long passCacheRead = 0;
        long passCacheWrite = 0;

        // Turnos de conversación de la sesión entera. El pie en vivo desglosa la caché POR TURNO, que
        // es la unidad en la que ahora se paga: una pasada ya no es una petición con su prefijo
        // dentro, es un turno de una conversación que el proveedor ya tiene cacheada.
        int sessionTurns = 0;
        void OnUsage(UsageSample u)
        {
            session.Usage.Add(
                u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.Cost, u.Calls);
            if (u.CostUnit is not null && string.IsNullOrEmpty(session.Usage.Currency))
            {
                session.Usage.Currency = u.CostUnit;
            }

            if (currentBreakdown is not null)
            {
                currentBreakdown.Calls += u.Calls;
                currentBreakdown.InputTokens += u.InputTokens;
                currentBreakdown.OutputTokens += u.OutputTokens;
                currentBreakdown.CacheReadTokens += u.CacheReadTokens;
                currentBreakdown.CacheWriteTokens += u.CacheWriteTokens;
                if (u.Cost is not null)
                {
                    currentBreakdown.Cost = (currentBreakdown.Cost ?? 0m) + u.Cost.Value;
                }

                currentBreakdown.Samples.Add(new CallSample(
                    currentBreakdown.Calls,
                    u.InputTokens, u.OutputTokens,
                    u.CacheReadTokens, u.CacheWriteTokens,
                    u.Cost, u.Model));

                passInput += u.InputTokens;
                passOutput += u.OutputTokens;
                passCacheRead += u.CacheReadTokens;
                passCacheWrite += u.CacheWriteTokens;
                passCalls += u.Calls;
                if (!budgetTripped && maxTokensPerUnit > 0 && passInput + passOutput > maxTokensPerUnit)
                {
                    budgetTripped = true;
                    trippedBy = $"{passInput + passOutput}/{maxTokensPerUnit} tokens";
                    try { unitCts?.Cancel(); } catch { /* already disposed */ }
                }
                else if (!budgetTripped && maxCallsPerPass > 0 && passCalls > maxCallsPerPass)
                {
                    budgetTripped = true;
                    trippedBy = $"{passCalls}/{maxCallsPerPass} llamadas en una pasada";
                    try { unitCts?.Cancel(); } catch { /* already disposed */ }
                }
            }

            // F15 — lo que viaja a la vista en vivo son CREDITS derivados de los tokens con la
            // tarifa del modelo de esta sesión, no el número que informó el proveedor: aquél está
            // en peticiones premium, la unidad retirada. Se deriva aquí, en el mismo sitio que lo
            // acumula, para que la cifra en vivo y la del informe sean la misma cuenta.
            CostResult live = CreditCalculator.Calculate(session, ModelRates());
            UsageUpdated?.Invoke(new LiveUsage(
                session.Usage.InputTokens,
                session.Usage.OutputTokens,
                session.Usage.CacheReadTokens,
                session.Usage.CacheWriteTokens,
                live,
                session.Provider,
                session.Usage.Calls,
                PromptBudget.From(session),
                sessionTurns));
        }

        _agent.TextStreamed += OnText;
        _agent.UsageReported += OnUsage;

        // F21 §2 — las pasadas que NO se pudieron cortar en `unit_done`, con su motivo. El corte
        // ahorra la llamada de cortesía del CLI; cuando no se puede —porque el proveedor no había
        // publicado el consumo de todas sus llamadas, o porque quedaba una herramienta a medias—
        // la pasada cuesta una llamada más, y eso NO puede quedar como una cifra sin causa (N-2).
        //
        // PROV-2 §2 — el coordinador escucha a quien DECLARE el corte (`ICuttingAuditor`), no a
        // una casa por su tipo concreto. Cortar no es de nadie: es una capacidad, y la tiene quien
        // la implemente. Quien no la declare no se entera de que existe, y aquí no se nombra a
        // ningún proveedor — que es la regla de esta carpeta.
        var cutSkipped = new List<string>();
        void OnCutSkipped(string why)
        {
            lock (cutSkipped)
            {
                cutSkipped.Add(why);
            }

            // Y se dice MIENTRAS pasa: el corte que no se pudo hacer cuesta una llamada de
            // cortesía más, y hasta aquí eso solo existía en el anexo del informe.
            Note(ActivityNoteKind.Milestone,
                $"No se pudo cortar la pasada: {why} — cuesta una llamada de cortesía más");
        }

        var cutting = _agent as ICuttingAuditor;
        if (cutting is not null)
        {
            cutting.CutSkipped += OnCutSkipped;
        }

        // El modelo va en el sello (F5.1b): es quien hace la observación, y hace falta para poder
        // nombrar a quién discrepa cuando dos modelos se contradicen sobre el mismo hallazgo.
        var stamp = new DetectionStamp(
            now, request.Mode, commit, by, Model: _agent.ModelName, Provider: _agent.ProviderId);

        // Los tipos de problema silenciados en ESTA app (F5.12), congelados al arrancar: los
        // mismos en el prompt de todas las unidades y en la lectura de lo que el auditor declara.
        // Uno que caduque a mitad de sesión no cambia las reglas del juego a media partida.
        PatternSilenceSet patterns = PatternSilenceSet.From(_hub.Store.ListPatternSilences(request.Slug), now);

        // F7 — las convenciones intencionales del proyecto, leídas del clon AHORA y congeladas
        // para toda la sesión, por la misma razón que los patrones: las mismas reglas en todas las
        // unidades. El contenido va al prompt; el hash va al informe, que es lo que permite releer
        // dentro de un año con qué criterio se auditó esto.
        DirectiveBundle directives = _directives?.Bundle(request.Slug, clone, DirectiveScope.Auditoria)
                                     ?? DirectiveBundle.Empty;
        session.Directives = directives.Records.ToList();

        // Cuánto ha suprimido cada patrón en ESTA sesión, para el informe y para el contador de
        // trabajo del propio patrón.
        var suppressionTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var suppressionExemplars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // F17 — la lupa del ciclo, leída del inventario vigente y congelada para toda la sesión
        // como los patrones y las directivas: la misma en todas las unidades. Decide con qué
        // temática nacen los hallazgos nuevos, qué existentes se reconcilian y qué dice el prompt.
        AuditTheme theme = inventory.Theme;
        var toolbox = new SessionToolbox(
            request.Slug, request.Mode, stamp, _ingestion, _reconciliation, _hub.Store, clone!, OnFinding,
            patterns, theme);

        // F30 §1 — cada herramienta, en cuanto se ejecuta. Es la misma costura que el arreglo
        // asistido tiene desde F16 (`FixToolbox.FileRead`, `Edited`…), del lado de la auditoría.
        void OnToolInvoked(string entry) => Note(ActivityNoteKind.Tool, entry);

        toolbox.ToolInvoked += OnToolInvoked;

        // F30 §2 — y cada herramienta MIENTRAS el modelo la escribe, para el proveedor que sepa
        // contarlo. La línea se va reescribiendo hasta que la ejecución la sustituye por la
        // definitiva; ahí está el tramo largo que D-1014 midió.
        void OnToolStreamed(ToolStream tool)
            => ActivityNoted?.Invoke(new ActivityNote(
                activityUnit, activityPass, ActivityNoteKind.ToolWriting, ActivityWording.Writing(tool),
                ActivityWording.WaitingFor(tool)));

        var narrating = _agent as INarratingAuditor;
        if (narrating is not null)
        {
            narrating.ToolStreamed += OnToolStreamed;
        }
        var auditedPaths = new HashSet<string>(StringComparer.Ordinal);
        int incompleteUnits = 0;

        try
        {
            AuditorBrief brief = PillarBrief.Parts(app.Stack);
            foreach (InventoryUnit unit in units)
            {
                ct.ThrowIfCancellationRequested();
                UnitPhaseChanged?.Invoke(unit.Path, "auditing");

                string abs = Path.Combine(clone!, unit.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs))
                {
                    var missing = new UnitVerdictRecord(unit.Path, unit.Module, "no-localizado", null);
                    session.Units.Add(missing);
                    UnitPhaseChanged?.Invoke(unit.Path, "missing");
                    UnitFinished?.Invoke(unit.Path, missing, new UnitUsageBreakdown { Unit = unit.Path });
                    continue;
                }

                string content = await File.ReadAllTextAsync(abs, ct);

                // Huella del contenido EXACTO que el auditor va a ver (F5.1b). Se calcula sobre los
                // bytes crudos, igual que el inventario, para que ambos hashes sean comparables. Es
                // la segunda capa de la guarda de evidencia de cambio: permite distinguir "el repo
                // avanzó" de "esta unidad cambió", que es lo único que legitima un «arreglado».
                string? unitContentHash = TryHashUnit(abs);

                var breakdown = new UnitUsageBreakdown { Unit = unit.Path };
                session.UsageBreakdown.Add(breakdown);
                currentBreakdown = breakdown;
                var unitClock = System.Diagnostics.Stopwatch.StartNew();

                // F4.1 — BARRIDO HASTA AGOTAR. Una pasada del auditor no cubre la unidad: declara
                // haberla cubierto y, al repetir, encuentra más (2026-08-25: la pasada 1 dijo haber
                // revisado ConvertToDetId/ConvertToSeq y la 2 halló tres defectos ahí). Así que la
                // app repite hasta que el barrido CONVERGE — F12 §E: dos pasadas secas seguidas, no
                // una. Las pasadas son internas: para el usuario una auditoría sigue siendo una
                // unidad barrida entera.
                //
                // Y «barrida» no es «sin defectos»: es que el auditor no saca más de esta unidad
                // con este criterio. Ningún texto de la aplicación puede sugerir lo otro.
                //
                // Cada pasada recalcula la lista de existentes, así que la siguiente ve lo que
                // reportó la anterior y lo reconcilia por ULID en vez de duplicarlo — es la misma
                // maquinaria de F4, aplicada dentro de la sesión.
                var passes = new List<UnitPassRecord>();

                int rejectedInUnit = 0;
                var reasonsInUnit = new List<string>();
                string? coverageSummary = null;
                bool overBudget = false;
                bool dry = false;
                int dryStreak = 0;
                int dryToFinish = Math.Min(DryPassesToFinish, maxPasses);
                IReadOnlyList<Finding> withoutVerdict = Array.Empty<Finding>();
                toolbox.BeginUnitSweep(unit.Path, unitContentHash);
                int locationsInUnit = 0;

                // F25 — la unidad se audita como UNA conversación con el proveedor, y cada pasada es
                // un turno suyo. El hilo puede tener que reabrirse a mitad de unidad: porque el
                // proveedor no pudo continuar la sesión, porque la pasada se cortó —cortar mata la
                // conversación— o porque llegó al techo de contexto. En los tres casos la unidad NO
                // se pierde: la pasada siguiente arranca un hilo nuevo con el prompt recompuesto, y
                // el reinicio queda contado con su motivo.
                //
                // El `await using` es la RED: pase lo que pase —una excepción del proveedor, una
                // cancelación—, la conversación que esté viva se cierra; un proveedor esperando un
                // turno que ya no va a llegar es una sesión colgada gastando cuota. Guarda el hilo
                // VIVO y no «el hilo», que es la diferencia que importa cuando puede haber varios a
                // lo largo de la unidad. Pero el cierre normal se pide más abajo y a mano, y no es
                // un capricho: cerrar la conversación es lo que hace
                // llegar el evento final del CLI con las cuentas de todo lo consumido (D-879), y ese
                // consumo tiene que entrar en el desglose de la unidad ANTES de que se cierre.
                await using var conversation = new UnitConversation();

                // Turnos servidos por el hilo VIVO. Se pone a cero al reabrir, porque el turno 1 de
                // un hilo nuevo lleva el prompt entero: la conversación no se hereda.
                int turnsInThread = 0;

                // Por qué murió el hilo anterior. Se apunta cuando de verdad se abre otro: cerrar el
                // último hilo de una unidad no es reiniciar nada.
                string? pendingRestart = null;

                // R2 §1 — el MODO EXHAUSTIVO no es un tercer camino: es este mismo `threadless`, que
                // ya existía para el proveedor que no sabe hilar (D-922), puesto a mano. Con él la
                // unidad no abre conversación y cada pasada viaja como una petición nueva con el
                // prompt recompuesto — el barrido de antes de F25, byte a byte. Nada más abajo
                // pregunta por el ajuste: si lo hiciera, sería una rama propia que mantener.
                bool threadless = session.Exhaustive;

                for (int pass = 1; pass <= maxPasses && dryStreak < dryToFinish && !overBudget; pass++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (conversation.Thread is null && !threadless)
                    {
                        conversation.Thread = await OpenThreadAsync(session, unit.Path, toolbox, ct);
                        turnsInThread = 0;
                        if (conversation.Thread is null)
                        {
                            threadless = true;
                        }
                        else if (pendingRestart is not null)
                        {
                            breakdown.ThreadRestarts++;
                            breakdown.ThreadRestartReasons.Add(pendingRestart);
                            session.Notes.Add(
                                $"{unit.Path} (pasada {pass}): la conversación de la unidad se reabre desde cero "
                                + $"—{pendingRestart}—, así que esta pasada vuelve a mandar el prompt entero.");
                            pendingRestart = null;
                        }
                    }

                    activityUnit = unit.Path;
                    activityPass = pass;
                    PassStarted?.Invoke(unit.Path, pass);

                    // F30 §1b — EL TRAMO DE ATALAYA, cronometrado de verdad. Lo que va de aquí al
                    // envío es todo lo que hace la aplicación entre una pasada y la siguiente:
                    // releer los hallazgos vivos de la unidad y recomponer el prompt. Se mide en vez
                    // de estimarse porque el número es el argumento: sin él, «guardando…» se lee
                    // como «Atalaya está tardando», y no es verdad.
                    var prep = System.Diagnostics.Stopwatch.StartNew();
                    IReadOnlyList<Finding> existing = _reconciliation.ExistingForUnit(request.Slug, unit.Path);
                    toolbox.BeginPass(existing);
                    // F17 §3: al auditor se le listan para reconciliar SOLO los de la temática del
                    // ciclo (todos, con General). Los de otras temáticas viajan aparte, como «no los
                    // juzgues»: están en la unidad y sin verlos los re-reportaría como nuevos.
                    var listed = existing.Where(f => ThemeScope.Reconciles(theme, f.Theme)).Select(ToExisting).ToList();
                    var offTheme = existing.Where(f => !ThemeScope.Reconciles(theme, f.Theme)).Select(ToExisting).ToList();
                    // F18 §2 — el prompt se compone PARTIDO por la costura de la caché: lo estable
                    // (reglas, rúbrica, catálogo, temática, directivas, patrones) delante y sin nada
                    // que varíe por unidad ni por pasada, y lo variable detrás. Concatenarlo da el
                    // prompt de siempre byte a byte; quien sepa marcar el prefijo lo marca.
                    ComposedUnitPrompt composed = PromptComposer.Compose(
                        unit.Path, content, brief, request.Mode, listed, patterns, directives, theme, offTheme,
                        Style);
                    string prompt = composed.Text;

                    // Lo que de verdad SALE en esta pasada. Con el hilo apagado es el prompt de
                    // siempre; encendido, a partir de la segunda es solo la continuación — y el
                    // censo tiene que decir eso, no lo que se compuso y no se mandó. Un informe de
                    // una medida que declarara 30.000 tokens donde viajaron 150 no valdría nada.
                    bool continuacion = conversation.Thread is not null && turnsInThread > 0;
                    string sent = continuacion ? PromptComposer.ContinuationTurn : prompt;
                    PromptComposition census = continuacion
                        ? new PromptComposition(Reglas: EstimateTokens(sent))
                        : composed.Composition;
                    breakdown.PromptTokensEstimate += EstimateTokens(sent);

                    budgetTripped = false;
                    trippedBy = null;
                    passInput = 0;
                    passOutput = 0;
                    passCacheRead = 0;
                    passCacheWrite = 0;
                    passCalls = 0;
                    var passClock = System.Diagnostics.Stopwatch.StartNew();

                    // La pasada se apunta ANTES de correr, con su composición y sin consumo: el
                    // resumen en vivo tiene que poder decir «el código es el 2 % de lo que se manda»
                    // mientras la pasada está pasando, que es cuando sirve. Al terminar se sustituye
                    // por la fila con sus tokens.
                    breakdown.Passes.Add(new PassUsage(pass, Composition: census));
                    int passRow = breakdown.Passes.Count - 1;
                    var unitRequest = new AuditUnitRequest(
                        unit.Path, content, prompt, app.Stack, request.Mode, listed, patterns,
                        composed.StablePrefix, composed.UnitPart);

                    // F30 §1b — LOS DOS TRAMOS DEL HUECO, cada uno con su hora. El primero es lo que
                    // acaba de hacer Atalaya, ya terminado y con su medida; el segundo es la entrega
                    // al agente, y a partir de ahí el pie cuenta la espera. Con las dos horas
                    // delante, el hueco deja de ser un silencio y pasa a tener dueño.
                    prep.Stop();
                    Note(ActivityNoteKind.Milestone,
                        $"Turno preparado · {existing.Count} hallazgo(s) vivo(s) en la unidad "
                        + $"· {prep.ElapsedMilliseconds} ms");
                    Note(ActivityNoteKind.Handover, $"Pasada {pass} · enviada al agente");

                    unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    try
                    {
                        if (conversation.Thread is null)
                        {
                            await _agent.AuditUnitAsync(unitRequest, toolbox, unitCts.Token);
                        }
                        else
                        {
                            try
                            {
                                // El turno 1 lleva el prompt ENTERO. Los siguientes, solo la
                                // continuación: ni reglas, ni código, ni lista de existentes — todo
                                // eso sigue delante del modelo porque es la misma conversación.
                                await conversation.Thread.TurnAsync(sent, unitCts.Token);
                                turnsInThread++;
                                breakdown.ThreadTurns++;
                                sessionTurns++;
                            }
                            catch (UnitThreadBrokenException broken)
                            {
                                // Primer respaldo: el proveedor no pudo continuar la sesión y esta
                                // pasada NO se ha servido. Se rehace como se hacía antes —una
                                // petición nueva, con el prompt recompuesto y la lista de
                                // existentes—, que es el mecanismo que sigue estando aquí para esto.
                                session.Notes.Add(
                                    $"{unit.Path} (pasada {pass}): la conversación de la unidad no pudo continuar "
                                    + $"—{broken.Message}—, así que la pasada se ha hecho con una petición nueva.");

                                // Y se dice MIENTRAS pasa (F30 §1). Hasta aquí esto solo existía en
                                // el anexo del informe, que se lee cuando ya ha terminado todo.
                                Note(ActivityNoteKind.Milestone,
                                    "Reanudación fallida, abriendo petición nueva");
                                pendingRestart = "no se pudo continuar la conversación";
                                await conversation.DisposeAsync();
                                turnsInThread = 0;

                                // Y el censo dice lo que SALIÓ: aquí viajó el prompt entero, no la
                                // continuación que se había compuesto y no llegó a mandarse.
                                breakdown.PromptTokensEstimate += EstimateTokens(prompt) - EstimateTokens(sent);
                                census = composed.Composition;

                                await _agent.AuditUnitAsync(unitRequest, toolbox, unitCts.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (budgetTripped && !ct.IsCancellationRequested)
                    {
                        overBudget = true;
                    }
                    finally
                    {
                        breakdown.ToolCalls += toolbox.ToolCallCount;
                        passClock.Stop();
                        breakdown.Passes[passRow] = new PassUsage(
                            pass, passCalls, passInput, passOutput, passCacheRead, passCacheWrite,
                            census, passClock.ElapsedMilliseconds);
                        unitCts.Dispose();
                        unitCts = null;
                    }

                    // ¿Sigue sirviendo este hilo para la pasada siguiente? Dos motivos para que no,
                    // y los dos se deciden AQUÍ, con la pasada ya contada.
                    if (conversation.Thread is { } live && !overBudget)
                    {
                        if (live.Closed)
                        {
                            // Segundo respaldo: la pasada se cortó en `unit_done` y cortar mata la
                            // conversación. La pasada cuenta igual que siempre; lo que no se hace
                            // NUNCA es intentar reanudar un hilo cortado.
                            pendingRestart = "la pasada se cortó en unit_done";
                            await conversation.DisposeAsync();
                            turnsInThread = 0;
                        }
                        else if (passCacheRead + passInput >= UnitThreadLimits.TechoContexto)
                        {
                            // Tercer respaldo: el contexto. Una conversación que desborda la ventana
                            // del modelo no falla con elegancia — empieza a perder lo de antes sin
                            // decirlo, y todo el argumento del hilo es que el modelo tiene delante
                            // lo que ya se dijo.
                            pendingRestart =
                                $"techo de contexto ({UnitThreadLimits.TechoContexto} tokens)";
                            await conversation.DisposeAsync();
                            turnsInThread = 0;
                        }
                    }

                    dry = !overBudget && toolbox.PassIsDry;

                    // F12 §E — DOS SECAS SEGUIDAS. Con un modelo no determinista, «esta pasada no
                    // vio nada nuevo» no es «no queda nada»: en el banco de pruebas el barrido paró
                    // en 3 de 5 porque la tercera vino seca, y una segunda auditoría encontró
                    // después un hallazgo que la primera no vio. Una pasada con aportación reinicia
                    // la cuenta, porque lo que se busca es que el barrido converja, no que acierte
                    // una vez.
                    dryStreak = dry ? dryStreak + 1 : 0;
                    withoutVerdict = toolbox.PendingVerdicts;
                    coverageSummary = toolbox.LastUnitSummary ?? coverageSummary;
                    locationsInUnit += toolbox.PassLocationsAdded;
                    var passRecord = new UnitPassRecord(
                        pass, toolbox.PassNew, toolbox.PassConfirmed, toolbox.PassResolved,
                        toolbox.PassNonVerifiable, toolbox.PassRejected, dry, toolbox.LastUnitSummary,
                        toolbox.PassLocationsAdded, toolbox.PassDisputed);
                    passes.Add(passRecord);
                    PassFinished?.Invoke(unit.Path, passRecord);

                    // Nunca se traga un rechazo: cada pasada vuelca los suyos, etiquetados.
                    rejectedInUnit += toolbox.RejectedPayloads.Count;
                    reasonsInUnit.AddRange(toolbox.RejectionReasons);
                    foreach (string r in toolbox.RejectedPayloads)
                    {
                        session.Notes.Add($"{unit.Path} (pasada {pass}): rechazo · {r}");
                    }

                    // Ninguna degradación es silenciosa (F5.1b): un «arreglado» sin evidencia de
                    // cambio y una discrepancia de criterio quedan nombrados en la sesión, y de ahí
                    // los recoge el informe.
                    foreach (string degraded in toolbox.DegradedVerdicts)
                    {
                        session.Notes.Add($"{unit.Path} (pasada {pass}): veredicto degradado · {degraded}");
                    }

                    // F5.12: lo que el auditor declaró haberse callado, con nombre y apellidos. Una
                    // supresión que no se nombra es indistinguible de una unidad limpia, y el
                    // informe acabaría diciendo «0 nuevos» sin causa visible — el mismo agujero
                    // que D-060 cerró para los rechazos.
                    foreach (KeyValuePair<string, int> s in toolbox.SuppressedByPattern)
                    {
                        string exemplar = toolbox.PatternExemplars.TryGetValue(s.Key, out string? e) ? e : s.Key;
                        session.Notes.Add(
                            $"{unit.Path} (pasada {pass}): suprimido por patrón · {s.Key} · {exemplar} × {s.Value}");
                        suppressionTotals[s.Key] = suppressionTotals.TryGetValue(s.Key, out int n)
                            ? n + s.Value
                            : s.Value;
                        suppressionExemplars[s.Key] = exemplar;
                    }

                    foreach (string entry in toolbox.ToolCallLog)
                    {
                        session.Notes.Add($"{unit.Path} (pasada {pass}): tool · {entry}");
                    }

                    // F21 §2 — «esta pasada costó una llamada más, y por esto». Se vacía por
                    // pasada: lo que se apuntó mientras corría es de ella.
                    lock (cutSkipped)
                    {
                        foreach (string why in cutSkipped)
                        {
                            session.Notes.Add(
                                $"{unit.Path} (pasada {pass}): no se pudo cerrar la pasada en unit_done "
                                + $"—{why}—, así que costó una llamada de cortesía más.");
                        }

                        cutSkipped.Clear();
                    }

                    if (toolbox.PassWasMute)
                    {
                        // F20 — el turno se gastó y no llegó ni un `unit_done`. NO cuenta como
                        // pasada seca (eso lo garantiza PassIsDry), así que el barrido sigue; pero
                        // hay que nombrarlo, porque es gasto sin trabajo.
                        session.Notes.Add(
                            $"{unit.Path} (pasada {pass}): el auditor no llamó a ninguna herramienta — "
                            + "la pasada no cuenta como seca y el barrido continúa.");
                    }
                    else if (toolbox.SubmitInvocations == 0 && toolbox.PassNew == 0 && !dry)
                    {
                        session.Notes.Add(
                            $"{unit.Path} (pasada {pass}): sin invocaciones a submit_finding(s) — el agente terminó sin reportar hallazgos por tool.");
                    }

                    toolbox.RejectedPayloads.Clear();
                    toolbox.RejectionReasons.Clear();
                    toolbox.DegradedVerdicts.Clear();
                    toolbox.SuppressedByPattern.Clear();
                    toolbox.ToolCallLog.Clear();
                }

                // Aquí, y no en el `finally`: lo que el proveedor declara al cerrar es el cuadre del
                // final, y si llegara después de soltar `currentBreakdown` se perdería. Cerrar dos
                // veces no hace nada — el hilo se cierra una sola vez.
                await conversation.DisposeAsync();

                currentBreakdown = null;
                unitClock.Stop();
                breakdown.DurationMs = unitClock.ElapsedMilliseconds;
                string? dominantReason = DominantReason(reasonsInUnit);
                session.Counters.Rejected += rejectedInUnit;

                if (overBudget)
                {
                    long spent = breakdown.InputTokens + breakdown.OutputTokens;
                    string summary = $"Cortada por presupuesto: {trippedBy ?? $"{spent}/{maxTokensPerUnit} tokens"}"
                        + (rejectedInUnit > 0
                            ? $" · {rejectedInUnit} rechazos" + (dominantReason is null ? "" : $": {dominantReason}")
                            : "");
                    var overBudgetRecord = new UnitVerdictRecord(
                        unit.Path, unit.Module, "presupuesto-superado", summary,
                        rejectedInUnit, dominantReason, Passes: passes);
                    session.Units.Add(overBudgetRecord);
                    session.Notes.Add($"{unit.Path}: {summary}");
                    UnitPhaseChanged?.Invoke(unit.Path, "over-budget");
                    UnitFinished?.Invoke(unit.Path, overBudgetRecord, breakdown);
                    continue;
                }

                // F4: sin veredicto no se toca nada. La unidad se marca incompleta y se nombra a
                // los hallazgos huérfanos — visible, pero no bloquea la sesión.
                string unitVerdict = "auditada";
                string? unitSummary = coverageSummary;

                // Tope alcanzado sin convergir: el barrido no garantiza cobertura. Visible, nunca
                // silencioso — es justo el fallo que nos trajo hasta aquí.
                bool coverageIncomplete = dryStreak < dryToFinish;
                if (coverageIncomplete)
                {
                    string convergencia = dryToFinish > 1
                        ? $"sin llegar a {dryToFinish} pasadas secas seguidas"
                        : "sin llegar a una pasada seca";
                    unitVerdict = "cobertura posiblemente incompleta";
                    unitSummary = $"Cobertura posiblemente incompleta: {passes.Count} pasada(s) {convergencia} "
                        + $"(la última aportó {passes[^1].New} nuevo(s) y {passes[^1].LocationsAdded} ubicación(es))"
                        + (unitSummary is null ? "" : $" · {unitSummary}");
                    session.Notes.Add($"{unit.Path}: {unitSummary}");
                }

                if (withoutVerdict.Count > 0)
                {
                    incompleteUnits++;
                    unitVerdict = "incompleta";
                    unitSummary = $"Incompleta: {withoutVerdict.Count} hallazgo(s) existentes sin veredicto del "
                        + "auditor (no se han modificado)"
                        + (unitSummary is null ? "" : $" · {unitSummary}");
                    foreach (Finding f in withoutVerdict)
                    {
                        session.Notes.Add($"{unit.Path}: sin veredicto · {f.Id} «{f.Title}»");
                    }
                }

                var unitRecord = new UnitVerdictRecord(
                    unit.Path, unit.Module, unitVerdict, unitSummary,
                    rejectedInUnit, dominantReason, withoutVerdict.Count, passes, coverageIncomplete);
                session.Units.Add(unitRecord);
                auditedPaths.Add(CodeAnchor.NormalizePath(unit.Path));
                UnitPhaseChanged?.Invoke(unit.Path, withoutVerdict.Count > 0 ? "incomplete" : "done");
                UnitFinished?.Invoke(unit.Path, unitRecord, breakdown);
            }
        }
        catch (OperationCanceledException)
        {
            // Parada del usuario. No es un error: es un final anticipado, y lo auditado hasta aqui
            // ya esta en el hub. Se sigue al cierre ordenado en vez de dejarlo huerfano.
            stopped = true;
        }
        catch (AuditorProviderException ex) when (session.Units.Count > 0)
        {
            // BUGFIX-CUOTA. El proveedor ha cerrado el grifo a mitad del barrido. Se trata IGUAL
            // que una parada: se corta aqui —no se prueba la unidad siguiente, que seria tirar
            // llamadas contra una cuota agotada— y se baja al cierre ordenado. Antes esta excepcion
            // subia entera y se llevaba por delante el registro de la sesion, las marcas del
            // inventario, la liberacion de los claims y el informe: trabajo ya pagado, tirado.
            //
            // El guardia del `when` es el limite: si el fallo llega ANTES de cerrar la primera
            // unidad no hay nada que salvar, y entonces sube tal cual — que es el comportamiento
            // que F5.15 dejo probado y que sigue siendo el correcto (sin sesion en el hub, sin
            // informe y sin una fila que registre que no se hizo nada).
            stopped = true;
            providerFailure = new SessionFailure(
                ex.Problem, ex.Message, ex.Detail, session.Units.Count, units.Count);
        }
        finally
        {
            _agent.TextStreamed -= OnText;
            toolbox.ToolInvoked -= OnToolInvoked;
            if (narrating is not null)
            {
                narrating.ToolStreamed -= OnToolStreamed;
            }
            _agent.UsageReported -= OnUsage;
            if (cutting is not null)
            {
                cutting.CutSkipped -= OnCutSkipped;
            }
        }

        MarkAuditedInInventory(inventory, auditedPaths, sessionId);
        _hub.Store.WriteInventory(request.Slug, inventory);

        // Liberar los claims es lo mas urgente de una parada: sin esto la unidad quedaba reclamada
        // por este usuario hasta que caducara el TTL, bloqueando a los demas por nada.
        ReleaseClaims(request.Slug, units);

        // «Interrumpida» se mide por COBERTURA, no por el botón: si la parada llegó cuando ya se
        // habían procesado todas las unidades pedidas, la sesión cubrió lo que decía cubrir y es
        // una sesión completa a todos los efectos. Lo que marca una sesión es haber dejado
        // unidades sin tocar.
        bool interrupted = stopped && session.Units.Count < units.Count;

        session.EndedUtc = DateTimeOffset.UtcNow;
        session.Counters = toolbox.Counters;
        session.SuppressionsByPattern = suppressionTotals
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new PatternSuppressionTally(kv.Key, suppressionExemplars[kv.Key], kv.Value))
            .ToList();
        session.Interrupted = interrupted;
        if (interrupted && providerFailure is null)
        {
            int pendientes = units.Count - session.Units.Count;
            session.Notes.Add(
                $"Sesión detenida por el usuario: {session.Units.Count} de {units.Count} unidad(es) "
                + $"procesadas, {pendientes} sin auditar. Lo hecho hasta aquí queda registrado.");
        }

        // La causa queda ESCRITA en la sesión, no solo en la pantalla: dentro de un mes, quien mire
        // por qué esta sesión cubrió tres unidades de cuarenta necesita leerlo en el hub.
        if (providerFailure is { } failed)
        {
            session.Notes.Add(
                $"Sesión cortada por el proveedor ({failed.Problem}): {failed.Message} "
                + failed.Summary
                + (string.IsNullOrWhiteSpace(failed.Detail) ? string.Empty : $" [{failed.Detail}]"));
        }

        _hub.Store.WriteSession(session);

        // F5.12: cada patrón acumula lo que ha suprimido. Es «cuánto trabaja este patrón», el
        // único dato con el que se puede decidir si sigue mereciendo la pena o si se puso por un
        // susto puntual. Se escribe tras la sesión y nunca la tumba.
        AccumulatePatternWork(request.Slug, session.SuppressionsByPattern, patterns);

        int pending = inventory.Units.Count(u => u.State == UnitState.Pendiente);
        int large = inventory.Units.Count(u => u.State == UnitState.Grande);
        string report = ReportBuilder.BuildSessionReport(
            app, session, newFindings, pending, large, _hub.OrganizationName, ModelRates());
        _hub.Store.WriteReport(request.Slug, sessionId.ToString(), report);

        // BUGFIX-PUSH — el del cierre también lleva reloj y voz, pero NO tumba la sesión: aquí ya
        // hay trabajo hecho, guardado y con informe, y tirarlo porque el hub no contesta sería
        // perder lo único que importa. Se dice que no se publicó y se apunta como incidencia; el
        // commit se queda en el clon y sale en la siguiente publicación que funcione.
        if (_hub.Sync is { } closing)
        {
            Note(ActivityNoteKind.Milestone, "Publicando la sesión en el hub…");
            var closingClock = System.Diagnostics.Stopwatch.StartNew();
            bool closingOk = closing.CommitAndPush(
                $"session: {request.Mode.ToString().ToLowerInvariant()} {request.Slug} {session.Units.Count} unidades"
                + (interrupted ? " (detenida)" : ""),
                CancellationToken.None);
            closingClock.Stop();

            if (closingOk)
            {
                Note(ActivityNoteKind.Milestone, $"Sesión publicada · {Seconds(closingClock.Elapsed)}");
            }
            else
            {
                string why = closing.LastError ?? HubSyncService.TimedOut(closing.PushTimeout);
                Note(ActivityNoteKind.Milestone, $"La sesión NO se publicó en el hub: {why}");
                session.Notes.Add($"hub: la sesión no se publicó — {why}");
            }
        }

        // El alias legible se reparte TRAS el push (§2, D-228): numerar antes de publicar es lo
        // que hacía colisionar a dos máquinas que auditaban a la vez. Lo que se asigna aquí viaja
        // en el push de la siguiente acción — el alias no es identidad, así que no urge.
        AssignAliases(request.Slug);

        // Courtesy ESTADO.md export into the audited repo (§7).
        _statusExporter?.ExportIfEnabled(request.Slug);

        // If the cycle is now empty, attempt the close (only one user actually closes it).
        // Una sesion detenida NO cierra ciclo: no ha cubierto lo que decia cubrir.
        CycleCloseResult close = CycleCloseResult.NotClosed;
        if (!interrupted && providerFailure is null && pending == 0
            && request.Mode is AuditMode.Lotes or AuditMode.Integral)
        {
            close = _cycles?.TryCloseCycle(request.Slug, app.CurrentCycle) ?? CycleCloseResult.NotClosed;
        }

        return new SessionResult(sessionId, session.Counters, ReachedZeroPending: pending == 0)
        {
            CycleClosed = close.Closed,
            CycleAging = close.Aging,
            CycleClose = close,
            Failure = providerFailure,
            IncompleteUnits = incompleteUnits,
            Interrupted = interrupted,
            SuppressionsByPattern = session.SuppressionsByPattern,
        };
    }

    /// <summary>
    /// Suma al contador de trabajo de cada patrón lo que suprimió en esta sesión (F5.12). Los ids
    /// que el auditor se inventó no corresponden a ningún patrón vivo y no se anotan en ninguno:
    /// quedan en el informe, que es donde se leen.
    /// </summary>
    private void AccumulatePatternWork(
        string slug, IReadOnlyList<PatternSuppressionTally> tallies, PatternSilenceSet patterns)
    {
        if (tallies.Count == 0)
        {
            return;
        }

        try
        {
            foreach (PatternSuppressionTally t in tallies)
            {
                PatternSilence? live = patterns.ByShortId(t.PatternId);
                if (live is null)
                {
                    continue;
                }

                PatternSilence? stored = _hub.Store.TryReadPatternSilence(slug, live.Id);
                if (stored is null)
                {
                    continue;
                }

                stored.Suppressions += t.Count;
                stored.LastSuppressionUtc = DateTimeOffset.UtcNow;
                _hub.Store.WritePatternSilence(slug, stored);
            }
        }
        catch (Exception)
        {
            // Un contador de trabajo que no se pudo escribir no puede tumbar una sesión ya
            // publicada. El informe conserva el dato.
        }
    }

    /// <summary>
    /// Reparte alias a lo recién detectado. Nunca tumba la sesión: el trabajo ya está publicado y
    /// un hallazgo sin alias se lee igual por su título — el backfill del arranque lo recogerá.
    /// </summary>
    private void AssignAliases(string slug)
    {
        try
        {
            _aliases?.AssignPending(slug);
        }
        catch (Exception)
        {
            // Sin alias se sigue trabajando; sin sesión cerrada, no.
        }
    }

    /// <summary>
    /// Se niega a auditar más unidades de las que el usuario aceptó (F5.13). Lanzar es lo correcto
    /// y no devolver un resultado vacío: no hay sesión que registrar —no ha pasado nada— y quien
    /// llama tiene que enterarse en voz alta. <see cref="LiveSessionService"/> lo enseña como
    /// estado de la sesión, que es donde el usuario está mirando cuando ocurre.
    /// </summary>
    private static void GuardAgainstUnconfirmedScope(SessionRequest request, int resolvedUnits)
    {
        if (request.ConfirmedUnits is not { } confirmed)
        {
            return;   // nadie declaró un N: no hay nada contra lo que comparar
        }

        int actual = Math.Max(request.UnitPaths.Count, resolvedUnits);
        if (actual > confirmed)
        {
            throw new LaunchMismatchException(confirmed, actual);
        }
    }

    private static List<InventoryUnit> ResolveUnits(SessionRequest request, InventoryCycle inventory)
    {
        if (request.Mode == AuditMode.Integral)
        {
            return inventory.Units.Where(u => u.State != UnitState.Grande).ToList();
        }

        var wanted = new HashSet<string>(request.UnitPaths, StringComparer.Ordinal);
        return inventory.Units.Where(u => wanted.Contains(u.Path)).ToList();
    }

    /// <summary>
    /// Reserva las unidades y lo publica (§2).
    /// <para>
    /// <b>Con reloj, con voz y con final</b> (BUGFIX-PUSH). Era la primera cosa que hace una sesión
    /// y la que la colgaba: un push sin reloj dentro de libgit2, sin una línea en pantalla y sin
    /// nada que «Detener» pudiera cancelar. Ahora se narra mientras pasa y, si el hub no contesta a
    /// tiempo, la sesión <b>termina con su motivo</b> en vez de quedarse colgada.
    /// </para>
    /// <para>
    /// <b>Y termina, no continúa.</b> Las reservas son lo que impide que dos máquinas auditen la
    /// misma unidad a la vez; seguir sin haberlas publicado es exactamente el caso que vinieron a
    /// evitar. El commit se queda hecho en el clon y sale en la siguiente publicación que funcione.
    /// </para>
    /// </summary>
    private void PublishClaims(
        string slug, IReadOnlyList<InventoryUnit> units, InventoryCycle inv, string by,
        Action<ActivityNoteKind, string> note, CancellationToken ct)
    {
        // El TTL sale del app.json, que es donde se configura (BUGFIX-AJUSTES). Estaba ahí desde
        // §2 y NADIE lo leía: todos los claims nacían con los 30 minutos por defecto del modelo, así
        // que bajarlo o subirlo no cambiaba cuándo se da por muerta una sesión ajena.
        int ttl = Math.Max(1, _hub.Store.TryReadApp(slug)?.Thresholds.ClaimTtlMinutes ?? 30);
        foreach (InventoryUnit unit in units)
        {
            _hub.Store.WriteClaim(slug, new Claim
            {
                Unit = unit.Path,
                Module = unit.Module,
                By = by,
                Machine = Environment.MachineName,
                Utc = DateTimeOffset.UtcNow,
                TtlMinutes = ttl,
            });
        }

        if (_hub.Sync is not { } sync)
        {
            return;
        }

        note(ActivityNoteKind.Milestone, "Publicando las reservas en el hub…");

        // Y los reintentos se dicen mientras pasan: con otra persona publicando contra el mismo
        // hub, el remoto se mueve debajo y hay que rehacer el rebase. Callarlo deja un silencio que
        // se lee como un cuelgue, que es exactamente de lo que venimos.
        void OnRetry(int attempt) => note(
            ActivityNoteKind.Milestone,
            $"Publicando en el hub… · reintento {attempt} de {HubSyncService.PushAttempts}");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        sync.PushRetrying += OnRetry;
        bool published;
        try
        {
            published = sync.CommitAndPush($"claims: {by} {units.Count} unidades en {slug}", ct);
        }
        finally
        {
            sync.PushRetrying -= OnRetry;
        }

        clock.Stop();

        if (!published)
        {
            throw new HubPublishException(sync.LastError ?? HubSyncService.TimedOut(sync.PushTimeout));
        }

        note(ActivityNoteKind.Milestone, $"Reservas publicadas · {Seconds(clock.Elapsed)}");
    }

    /// <summary>«1,2 s», que es como se lee un tramo corto en el hilo.</summary>
    private static string Seconds(TimeSpan elapsed)
        => $"{elapsed.TotalSeconds.ToString("0.#", AppCulture.Display)} s";

    private void ReleaseClaims(string slug, IReadOnlyList<InventoryUnit> units)
    {
        foreach (InventoryUnit unit in units)
        {
            _hub.Store.DeleteClaim(slug, HashUtil.UnitHash(unit.Path));
        }
    }

    private static void MarkAuditedInInventory(InventoryCycle inventory, HashSet<string> auditedPaths, Ulid sessionId)
    {
        foreach (InventoryUnit u in inventory.Units)
        {
            if (auditedPaths.Contains(CodeAnchor.NormalizePath(u.Path)) && u.State != UnitState.Grande)
            {
                u.State = UnitState.Auditada;
                u.AuditedInSession = sessionId;
            }
        }
    }

    /// <summary>Un hallazgo del hub, tal y como se le presenta al auditor (F4).</summary>
    private static ExistingFinding ToExisting(Finding f)
    {
        Location? loc = f.Locations.Count > 0 ? f.Locations[0] : null;
        return new ExistingFinding(
            f.Id.ToString(),
            f.DisplayId,
            f.Title,
            f.Severity.ToString().ToLowerInvariant(),
            loc is null ? "(sin ubicación)" : $"{loc.Path}:{loc.Line}",
            f.Status == FindingStatus.Silenciado ? "silenciado" : "activo",
            ThemeCatalog.Display(f.Theme));
    }

    /// <summary>
    /// Rough token estimate for the initial prompt (Hito 1a). ~4 chars per token is the same
    /// heuristic OpenAI/Anthropic docs quote for English/code; good enough to spot a bloated brief
    /// against actual SDK <c>InputTokens</c> without adding a tokenizer dependency.
    /// <para>
    /// Delega en <see cref="PromptTokens"/> desde F7: el presupuesto de directivas se enseña en el
    /// panel y se declara en el prompt, y dos reglas distintas para el mismo número harían que el
    /// panel dijera una cosa y el prompt otra sobre lo mismo.
    /// </para>
    /// </summary>
    private static int EstimateTokens(string text) => PromptTokens.Estimate(text);

    /// <summary>
    /// SHA-256 de los bytes de la unidad, con el mismo algoritmo y prefijo que usa el inventario
    /// (<c>InventoryScanner</c>), para que los dos hashes se puedan comparar. Null si el fichero no
    /// se puede leer: sin hash la guarda de F5.1b cae a la capa del commit, que es lo correcto —
    /// no poder probar que nada cambió no es lo mismo que probar que cambió.
    /// </summary>
    private static string? TryHashUnit(string absolutePath)
    {
        try
        {
            return HashUtil.Sha256Hex(File.ReadAllBytes(absolutePath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Motivo dominante de rechazo por unidad (F3.1 Bloque 0). Se colapsa cada motivo por su
    /// primera frase (hasta el primer punto o dos puntos) para que "tag inválido 'X'" y "tag
    /// inválido 'Y'" cuenten como el mismo motivo raíz. Devuelve null si no hay rechazos.
    /// </summary>
    /// <summary>
    /// <b>El hilo VIVO de la unidad</b>, sea el primero o el cuarto (F25 §2).
    /// <para>
    /// Existe para que el <c>await using</c> siga siendo la red de siempre ahora que lo que hay
    /// dentro puede cambiar a mitad de unidad: atar la red al primer hilo dejaría sin cerrar
    /// justamente al que quedó abierto. Cerrar dos veces no hace nada.
    /// </para>
    /// </summary>
    private sealed class UnitConversation : IAsyncDisposable
    {
        public IUnitThread? Thread { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Thread is { } thread)
            {
                Thread = null;
                await thread.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// <b>Abre el hilo de la unidad</b> (F25). Devuelve <c>null</c> —y entonces la unidad corre por
    /// el camino de respaldo, una petición por pasada— solo cuando el proveedor no sabe hilar.
    /// <para>
    /// Y eso se DICE, por evento y en las notas de la sesión. Un barrido que costara el triple sin
    /// que nada lo nombrara sería indistinguible de uno caro por otro motivo.
    /// </para>
    /// </summary>
    private async Task<IUnitThread?> OpenThreadAsync(
        AuditSession session, string unitPath, IAuditToolbox toolbox, CancellationToken ct)
    {
        if (_agent is IThreadedAuditor threaded)
        {
            return await threaded.OpenUnitThreadAsync(toolbox, ct);
        }

        // El aviso, UNA vez por sesión: es una propiedad del proveedor y no de la unidad, y
        // repetirlo cuarenta veces lo convertiría en ruido. La nota sí va por unidad, porque el
        // informe se lee unidad a unidad.
        if (!_threadWarned)
        {
            _threadWarned = true;
            ThreadUnavailable?.Invoke(_agent.ProviderName);
        }

        session.Notes.Add(
            $"{unitPath}: {_agent.ProviderName} no audita la unidad como una conversación; "
            + "se ha barrido con una petición por pasada, y cada una vuelve a mandar el prompt entero.");
        return null;
    }

    private static string? DominantReason(IReadOnlyCollection<string> reasons)
    {
        if (reasons.Count == 0)
        {
            return null;
        }

        static string Head(string s)
        {
            int end = s.IndexOfAny(new[] { '\'', ':' });
            string head = end > 0 ? s[..end].TrimEnd() : s;
            return head.TrimEnd('.', ' ');
        }

        return reasons
            .GroupBy(Head, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First().Key;
    }
}
