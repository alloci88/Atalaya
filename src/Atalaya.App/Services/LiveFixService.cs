using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>Lo que se le pide al servicio: arreglar UN hallazgo de UNA app.</summary>
public sealed record FixSessionRequest(string Slug, Ulid FindingId);

/// <summary>
/// Dueño del estado de la sesión de ARREGLO en curso (F6.9).
/// <para>
/// Mismo patrón que <see cref="LiveSessionService"/> y por la misma razón: la vista es
/// <c>Transient</c>, así que si el estado viviera en ella, navegar fuera y volver dejaría la
/// pantalla en blanco con el agente todavía escribiendo en el clon. Aquí el estado es un
/// singleton y «Arreglo asistido» es una VISTA sobre él.
/// </para>
/// <para>
/// Además implementa las dos mitades conversacionales que el agente necesita:
/// <see cref="IUserQuestions"/> (su <c>ask_user</c>) y <see cref="IFixApprovals"/> (el permiso
/// para tocar un fichero que no es del hallazgo). Las dos aparecen como TARJETAS en la
/// conversación, no como diálogos modales: la decisión se toma leyendo lo que el agente acaba de
/// explicar, y un modal tapa justo eso.
/// </para>
/// </summary>
public sealed partial class LiveFixService : ObservableObject, IUserQuestions, IFixApprovals
{
    private readonly HubContext _hub;

    /// <summary>
    /// Quién arregla, preguntado en CADA sesión y no capturado (F16). Es la misma regla que hizo
    /// que el registro releyera los ajustes (D-776): un singleton que se quedara con el proveedor
    /// que hubiera al arrancar obligaría a reiniciar la aplicación para que cambiar de casa en
    /// Ajustes sirviera de algo. Dentro de una sesión, en cambio, el motor no puede cambiar a
    /// mitad: por eso se resuelve una vez al empezar y se guarda en <see cref="_agent"/>.
    /// </summary>
    private readonly Func<IAssistedFixProvider?> _fixer;

    private readonly MachineConfigStore _machines;
    private readonly IUlidFactory _ulids;
    private readonly SettingsService _settings;
    private readonly ReferenceCollector _references;
    private readonly FixSnapshotStore _snapshots;
    private readonly AssistedFixLauncher _launcher;
    private readonly AgentBusyGate _busy;
    private readonly BuildRunner _builds;
    private readonly FixCommitter _committer;
    private readonly ModelResolver? _models;
    private readonly DirectiveService? _directives;

    /// <summary>
    /// Las convenciones del proyecto que viajaron en el encargo (F7). Se guarda para que el cierre
    /// pueda escribirlas en la sesión: sin la traza, «el arreglo respeta las convenciones» sería
    /// una afirmación sin forma de comprobarla.
    /// </summary>
    private DirectiveBundle _directiveBundle = DirectiveBundle.Empty;
    private readonly object _gate = new();

    private readonly Dictionary<FixQuestion, TaskCompletionSource<string?>> _pending = new();
    private readonly Queue<string> _queued = new();
    private readonly FixPauseGate _pause = new();

    /// <summary>El proveedor de ESTA sesión, ya resuelto. Null entre sesiones.</summary>
    private IAssistedFixProvider? _agent;

    private CancellationTokenSource? _cts;
    private IFixSteering? _steering;
    private FixToolbox? _toolbox;
    private FixSnapshotSet? _set;
    private FixMessage? _currentAgentText;
    private FixDoneArgs? _done;
    private string? _clonePath;
    private Finding? _finding;
    private AppConfig? _app;

    public LiveFixService(
        HubContext hub,
        Func<IAssistedFixProvider?> fixer,
        MachineConfigStore machines,
        IUlidFactory ulids,
        SettingsService settings,
        ReferenceCollector references,
        FixSnapshotStore snapshots,
        AssistedFixLauncher launcher,
        AgentBusyGate busy,
        BuildRunner? builds = null,
        ModelResolver? models = null,
        DirectiveService? directives = null,
        FixCommitter? committer = null)
    {
        _hub = hub;
        _fixer = fixer;
        _machines = machines;
        _ulids = ulids;
        _settings = settings;
        _references = references;
        _snapshots = snapshots;
        _launcher = launcher;
        _busy = busy;
        _builds = builds ?? new BuildRunner();
        _models = models;
        _directives = directives;
        // F32 - quien commitea el clon del usuario cuando el lo pide. Es un seam por lo mismo que
        // `BuildRunner`: una salvaguarda que solo se puede comprobar con un `git` de verdad
        // delante no se comprueba nunca.
        _committer = committer ?? new FixCommitter();
    }

    // ------------------------------------------------------------------ estado observable

    /// <summary>La conversación: narración del agente, tarjetas de pregunta y avisos de la app.</summary>
    public ObservableCollection<ConversationEntry> Conversation { get; } = new();

    /// <summary>Los ficheros tocados, con su diff. Una pestaña por fichero.</summary>
    public ObservableCollection<FixFileChange> Files { get; } = new();

    /// <summary>La sugerencia de commit, editable. No nula desde que el agente cierra.</summary>
    public CommitSuggestion Commit { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasFinished;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _failureMessage = string.Empty;
    [ObservableProperty] private bool _failureOffersModelChange;

    /// <summary>
    /// El error del proveedor tal cual, para copiarlo (BUGFIX-CUOTA). Mismo criterio que en la
    /// sesión de auditoría: la frase dice qué hacer, y esto —tipo, texto y Request ID— es lo que se
    /// le pega a quien administre la organización.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailureDetail))]
    private string _failureDetail = string.Empty;

    public bool HasFailureDetail => FailureDetail.Length > 0;
    [ObservableProperty] private string _slug = string.Empty;
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _findingAlias = string.Empty;
    [ObservableProperty] private string _findingTitle = string.Empty;
    [ObservableProperty] private string _sessionId = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _risks = string.Empty;
    [ObservableProperty] private string _lastBuild = string.Empty;
    [ObservableProperty] private bool _lastBuildOk;
    [ObservableProperty] private bool _hasBuildResult;

    /// <summary>
    /// Compilar la solución ENTERA en vez del proyecto de lo tocado (H9.1 §2). Es una decisión del
    /// usuario y solo suya: él es quien sabe si su cambio puede haber roto a un vecino, y quien
    /// paga el tiempo de averiguarlo. El agente no puede tocarlo.
    /// </summary>
    [ObservableProperty] private bool _buildFullSolution;
    [ObservableProperty] private long _inputTokens;
    [ObservableProperty] private long _outputTokens;
    [ObservableProperty] private long _cacheReadTokens;
    [ObservableProperty] private long _cacheWriteTokens;

    /// <summary>Con qué modelo y proveedor corre, para poder valorar sus tokens (F15).</summary>
    [ObservableProperty] private string? _model;
    [ObservableProperty] private string? _provider;

    /// <summary>
    /// Cómo se llama esa casa para una persona («GitHub Copilot», «Claude Code»). La pantalla
    /// anuncia con quién se está arreglando (F16): quien mira un diff tiene derecho a saber quién
    /// lo escribió, y el identificador que va al hub no es un nombre que se lea.
    /// </summary>
    [ObservableProperty] private string _providerName = string.Empty;

    [ObservableProperty] private decimal? _cost;
    [ObservableProperty] private string _costUnit = CostFormat.Unit;

    /// <summary>
    /// Lo que el PROVEEDOR declara que ha costado, en su unidad, tal cual lo dice (F16-RETOQUE §1).
    /// <para>
    /// No es el coste de la sesión y no se enseña como tal: el CLI de Claude Code publica un
    /// <c>total_cost_usd</c> a tarifa de lista que su suscripción no factura. Se guarda porque
    /// viene gratis y es un dato medido, y acaba en el informe como una línea informativa. Hasta
    /// aquí este camino escribía en <c>Usage.Cost</c> los credits DERIVADOS, que no es un dato del
    /// proveedor sino una cuenta nuestra: los otros dos caminos —auditoría y verificación— siempre
    /// guardaron ahí lo declarado, y ahora los tres dicen lo mismo.
    /// </para>
    /// </summary>
    [ObservableProperty] private decimal? _declaredCost;

    /// <summary>La unidad de <see cref="DeclaredCost"/>, tal y como la nombra el proveedor.</summary>
    [ObservableProperty] private string? _declaredCostUnit;

    /// <summary>El coste con su motivo cuando no lo hay, igual que en la auditoría (F16 §B).</summary>
    [ObservableProperty] private CostResult _costResult = CostResult.Unavailable(CostUnavailable.TokensMissing);
    [ObservableProperty] private int _calls;
    [ObservableProperty] private DateTimeOffset? _startedUtc;
    [ObservableProperty] private DateTimeOffset? _endedUtc;
    [ObservableProperty] private string _reportPath = string.Empty;

    /// <summary>El hallazgo que se está arreglando; lo necesita «Verificar ahora» al cerrar.</summary>
    public Ulid FindingId { get; private set; }

    /// <summary>
    /// El último veredicto de compilación, ya atribuido: qué se compiló, cuántos errores son
    /// NUEVOS y cuántos ya estaban. Null mientras no se haya compilado nada.
    /// </summary>
    public BuildVerdict? LastVerdict { get; private set; }

    /// <summary>
    /// Si el código afectado tiene tests, resuelto por la aplicación al arrancar (H9.1 §3). Lo lee
    /// el encargo del agente y lo recoge el informe: que no haya tests es un hecho del proyecto que
    /// se dice una vez, no una carencia del arreglo.
    /// </summary>
    public FixTestSituation TestSituation { get; private set; } = FixTestSituation.Unknown;

    /// <summary>Hay algo que enseñar en la vista: corriendo, terminado o fallido.</summary>
    public bool HasSession => IsRunning || HasFinished || HasFailed;

    /// <summary>Quedan cambios en el clon que nadie ha cerrado: el descarte sigue disponible.</summary>
    public bool HasPendingChanges => _set is { Closed: false, Entries.Count: > 0 };

    public int TouchedCount => Files.Count;

    public TimeSpan Elapsed => StartedUtc is null
        ? TimeSpan.Zero
        : (EndedUtc ?? DateTimeOffset.UtcNow) - StartedUtc.Value;

    /// <summary>Repinta el indicador del rail y la barra de estado.</summary>
    public event Action? Changed;

    /// <summary>Avisos efímeros que la carcasa saca por toast.</summary>
    public event Action<string>? Notice;

    /// <summary>Terminó una sesión de arreglo, bien o mal.</summary>
    public event Action<string>? Completed;

    /// <summary>Línea de la barra inferior mientras corre.</summary>
    public string ProgressLine => !IsRunning
        ? string.Empty
        // Con el NOMBRE, no con el identificador (R12): la barra de estado es un rótulo, y la de
        // la auditoría decía lo mismo de la otra manera a dos píxeles de distancia.
        : $"Arreglando {FindingAlias} en {AppName}"
          + (IsPaused ? " · en pausa" : "")
          + (Files.Count > 0 ? $" · {Files.Count} fichero(s)" : "");

    // ------------------------------------------------------------------ arrancar

    /// <summary>
    /// Lanza una sesión de arreglo. El cerrojo de una-sesión-a-la-vez se echa ANTES del primer
    /// <c>await</c>, igual que en la auditoría (D-085): dos pulsaciones seguidas no arrancan dos.
    /// </summary>
    public Task StartAsync(FixSessionRequest request)
    {
        bool blocked;
        lock (_gate)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            blocked = !_busy.TryEnter(AgentWork.Arreglo);
            if (!blocked)
            {
                IsRunning = true;
            }
        }

        Reset(request);
        if (blocked)
        {
            Fail(_busy.BusyMessage, offersModelChange: false);
            return Task.CompletedTask;
        }

        return RunAsync(request);
    }

    private void Reset(FixSessionRequest request)
    {
        OnUi(() =>
        {
            Conversation.Clear();
            Files.Clear();
        });

        lock (_pending)
        {
            _pending.Clear();
        }

        lock (_queued)
        {
            _queued.Clear();
        }

        _currentAgentText = null;
        _done = null;
        _set = null;
        _toolbox = null;
        _steering = null;
        _finding = null;
        _app = null;
        _pause.Resume();

        Slug = request.Slug;
        FindingId = request.FindingId;
        FindingAlias = string.Empty;
        FindingTitle = string.Empty;
        AppName = request.Slug;
        SessionId = string.Empty;
        ReportPath = string.Empty;
        Summary = string.Empty;
        Risks = string.Empty;
        LastBuild = string.Empty;
        HasBuildResult = false;
        LastBuildOk = false;
        IsBuilding = false;
        IsPaused = false;
        HasFinished = false;
        HasFailed = false;
        FailureMessage = string.Empty;
        FailureOffersModelChange = false;
        FailureDetail = string.Empty;
        Commit.Title = string.Empty;
        Commit.Description = string.Empty;
        InputTokens = OutputTokens = CacheReadTokens = CacheWriteTokens = 0;
        Cost = null;
        CostResult = CostResult.Unavailable(CostUnavailable.TokensMissing);
        DeclaredCost = null;
        DeclaredCostUnit = null;
        Calls = 0;

        // F16 — el motor de ESTA sesión se resuelve aquí, una vez, y ya no cambia: dentro de un
        // arreglo el proveedor no puede cambiar a mitad. Con él vienen el modelo y la casa, que
        // son lo que la pantalla anuncia y lo que el informe registra.
        _agent = _fixer();
        Model = _agent?.ModelName;
        Provider = _agent?.ProviderId;
        ProviderName = _agent?.ProviderName ?? string.Empty;
        EndedUtc = null;
        StartedUtc = DateTimeOffset.UtcNow;
        StatusMessage = _agent is null
            ? "Comprobando el clon…"
            : $"Comprobando el clon y {_agent.ProviderName}…";
        Changed?.Invoke();
    }

    private async Task RunAsync(FixSessionRequest request)
    {
        try
        {
            Finding? finding = _hub.Store.TryReadFinding(request.Slug, request.FindingId.ToString());
            if (finding is null)
            {
                Fail("Ese hallazgo ya no está en el hub.", offersModelChange: false);
                return;
            }

            _finding = finding;
            _app = _hub.Store.TryReadApp(request.Slug);
            AppName = _app?.Name ?? request.Slug;
            FindingAlias = finding.DisplayId ?? finding.Id.ToString();
            FindingTitle = finding.Title;

            // Las precondiciones se vuelven a mirar AQUÍ, no solo al pintar el botón: entre pulsar
            // y arrancar el usuario ha podido tocar el clon, y el árbol limpio es la única razón
            // por la que descartar funciona.
            // ignoreBusy: el cerrojo ya lo tenemos nosotros desde StartAsync.
            FixLaunchDecision decision = _launcher.Check(request.Slug, finding, ignoreBusy: true);
            if (!decision.CanStart)
            {
                Fail(decision.Message, offersModelChange: false);
                return;
            }

            _clonePath = _machines.Load().ClonePathFor(request.Slug);

            if (_agent is null)
            {
                Fail(
                    "El proveedor de auditoría elegido no sabe hacer arreglos asistidos. Elige otro "
                    + "en Ajustes → Auditoría, o genera el prompt de arreglo y hazlo a mano.",
                    offersModelChange: false);
                return;
            }

            AgentReadiness readiness = await _agent.CheckAsync(CancellationToken.None);
            if (!readiness.Ready)
            {
                Fail(readiness.Message, readiness.Problem == AgentProblem.ModelUnavailable,
                    readiness.Detail);
                return;
            }

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

            Ulid sessionId = _ulids.NewUlid();
            SessionId = sessionId.ToString();
            _set = _snapshots.Begin(SessionId, request.Slug, _clonePath!, FindingAlias, finding.Title);

            Say(FixMessage.System("◆",
                $"Arreglo asistido de {FindingAlias} sobre tu clon en {_clonePath}, con "
                + $"{_agent.ProviderName}{(Model is { Length: > 0 } m ? $" (modelo {m})" : string.Empty)}. "
                + "El árbol estaba limpio: cualquier cambio que veas a partir de aquí lo ha hecho "
                + "el agente."));

            StatusMessage = "Buscando quién usa este código…";
            Changed?.Invoke();
            ReferenceReport refs = await Task.Run(() => _references.Collect(_clonePath, finding));
            Say(FixMessage.System("⌕", ReferenceLine(refs)));

            IReadOnlyList<FixCodeExcerpt> code = ReadCode(finding);

            // H9.1 §3: la situación de tests la resuelve la APLICACIÓN, leyendo el clon, antes de
            // que el agente gaste un solo turno buscando lo que ya se puede saber.
            TestSituation = FixTestSituation.Detect(_clonePath, finding.Locations.Select(l => l.Path));
            Say(FixMessage.System("⚗", TestSituation.Narration));

            // F7: el estilo de la casa, leído del clon en este momento. Va al prompt para que el
            // arreglo se parezca al proyecto, y su traza va a la sesión para que el informe pueda
            // decir con qué convenciones se arregló.
            _directiveBundle = _directives?.Bundle(request.Slug, _clonePath, DirectiveScope.Arreglo)
                               ?? DirectiveBundle.Empty;
            if (!_directiveBundle.IsEmpty)
            {
                Say(FixMessage.System("§", DirectiveLine(_directiveBundle)));
            }

            string prompt = FixSessionPrompt.Build(
                finding, refs, code, AppName, FixToolbox.DefaultReadBudget, TestSituation,
                _directiveBundle);

            _cts = new CancellationTokenSource();
            var toolbox = new FixToolbox(
                _clonePath!,
                finding.Locations.Select(l => l.Path),
                _snapshots,
                _set,
                this,
                _pause,
                _builds,
                _cts.Token,
                fullSolution: () => BuildFullSolution);
            _toolbox = toolbox;
            Subscribe(toolbox);

            _agent.TextStreamed += OnText;
            _agent.UsageReported += OnUsage;
            StatusMessage = "El agente está trabajando…";
            Changed?.Invoke();

            try
            {
                await _agent.FixAsync(
                    new FixRequest(prompt, _clonePath!),
                    new FixConversation(toolbox, this, NextTurnAsync, OnSteeringReady),
                    _cts.Token);
            }
            finally
            {
                _agent.TextStreamed -= OnText;
                _agent.UsageReported -= OnUsage;
                Unsubscribe(toolbox);
            }

            Close(sessionId, finding, interrupted: _cts.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // Detener es un final ordenado, no un error: lo aplicado se queda y se registra.
            if (_finding is not null && Ulid.TryParse(SessionId, out Ulid stopped))
            {
                Close(stopped, _finding, interrupted: true);
            }
            else
            {
                Fail("La sesión de arreglo se detuvo antes de empezar.", offersModelChange: false);
            }
        }
        catch (AuditorModelUnavailableException modelEx)
        {
            Fail(modelEx.Message, offersModelChange: true, modelEx.Detail);
        }
        catch (AuditorProviderException authEx)
        {
            // BUGFIX-CUOTA: cuota, asiento, credenciales o red, cada uno con su frase ya
            // decidida por el clasificador. Aquí no se vuelve a diagnosticar nada.
            Fail(authEx.Message, offersModelChange: false, authEx.Detail);
        }
        catch (Exception ex)
        {
            Fail($"El arreglo se ha interrumpido por un error: {ex.Message}",
                offersModelChange: false, CopilotFailure.Raw(ex));
        }
        finally
        {
            CancelPendingQuestions();
            EndedUtc = DateTimeOffset.UtcNow;
            IsRunning = false;
            _steering = null;
            _cts?.Dispose();
            _cts = null;
            _busy.Exit(AgentWork.Arreglo);
            Changed?.Invoke();
        }
    }

    // ------------------------------------------------------------------ cierre y registro

    /// <summary>
    /// Escribe la sesión de tipo <c>fix</c>, su informe y el evento en el historial del hallazgo.
    /// <para>
    /// <b>Arreglar NUNCA resuelve</b> (F6.9). El estado del hallazgo no se toca: sigue activo, y
    /// la resolución llega por la vía de siempre —verificar, con evidencia—. Lo que queda escrito
    /// es que alguien lo intentó, qué tocó y cuánto costó.
    /// </para>
    /// </summary>
    private void Close(Ulid sessionId, Finding finding, bool interrupted)
    {
        FixDoneArgs? done = _done;
        Summary = done?.Summary ?? (interrupted
            ? "Sesión detenida por el usuario antes de que el agente cerrara el arreglo."
            : "El agente terminó sin cerrar el arreglo con un resumen.");
        Risks = done?.Risks ?? string.Empty;
        Commit.Title = done?.CommitTitle ?? DefaultCommitTitle(finding);
        Commit.Description = done?.CommitDescription ?? string.Empty;

        var session = new AuditSession
        {
            Id = sessionId,
            AppSlug = Slug,
            Mode = AuditMode.Fix,
            By = _hub.ResolveIdentity().Name,
            Machine = Environment.MachineName,
            StartedUtc = StartedUtc ?? DateTimeOffset.UtcNow,
            EndedUtc = DateTimeOffset.UtcNow,
            Commit = GitInfo.HeadSha(_clonePath),
            CycleN = _app?.CurrentCycle ?? 0,
            Model = Model,
            Provider = Provider,
            Interrupted = interrupted,
            // De qué hallazgo era este arreglo (H9.1 §1). Sin esto, el informe de una sesión fix
            // nombra el hallazgo en su texto pero nadie puede navegar de vuelta a su ficha.
            FixFindingId = finding.Id.ToString(),
            FixFindingAlias = FindingAlias,
            Directives = _directiveBundle.Records.ToList(),
        };
        // La caché ESCRITA también, que se estaba pasando como cero: con Claude Code es el
        // sumando más grande de la factura —escribir en caché se cobra al doble de la entrada— así
        // que perderlo dejaba el informe del arreglo contando de menos justo donde más pesa. Y las
        // llamadas, que hasta ahora no se guardaban en ninguna parte para una sesión sin unidades.
        session.Usage.Add(
            InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens, DeclaredCost, Calls);
        if (DeclaredCostUnit is { Length: > 0 } declaredUnit)
        {
            session.Usage.Currency = declaredUnit;
        }

        session.Notes.Add($"Arreglo asistido de {FindingAlias}: {finding.Title}");
        foreach (FixFileChange file in Files)
        {
            session.Notes.Add($"tocado: {file.RelativePath} ({file.Tally})"
                + (file.InScope ? string.Empty : " — fuera del hallazgo, autorizado por el usuario"));
        }

        if (Files.Count == 0)
        {
            session.Notes.Add("No se modificó ningún fichero.");
        }

        try
        {
            _hub.Store.WriteSession(session);
            string report = ReportBuilder.BuildFixReport(
                _app, session, finding, Files.Select(f => (f.RelativePath, f.Tally, f.InScope)).ToList(),
                Summary, Risks, Commit.Title, Commit.Description, HasBuildResult ? LastVerdict : null,
                _hub.OrganizationName, TestSituation, ModelRates());
            _hub.Store.WriteReport(Slug, SessionId, report);
            ReportPath = _hub.HubPaths.ReportFile(Slug, SessionId);

            // El historial del hallazgo: qué se intentó y con qué resultado. El ESTADO no cambia.
            Finding? stored = _hub.Store.TryReadFinding(Slug, finding.Id.ToString());
            if (stored is not null)
            {
                // El evento apunta a SU sesión (H9.1 §1): del historial de la ficha se llega al
                // informe del arreglo, y del informe se vuelve a la ficha. El círculo se cierra.
                stored.Record(new HistoryEntry(
                    DateTimeOffset.UtcNow, FindingEvent.FixProposed, session.By,
                    $"arreglo asistido ejecutado ({Files.Count} fichero(s) tocado(s)): {Trim(Summary, 400)}")
                {
                    SessionId = SessionId,
                });
                _hub.Store.WriteFinding(Slug, stored);
            }

            WriteFixRecord(sessionId, finding, session.By, session.Commit);
            _hub.Sync?.CommitAndPush($"fix: {FindingAlias} en {Slug} ({Files.Count} fichero(s))");
        }
        catch (Exception ex)
        {
            Say(FixMessage.System("⚠", $"No se pudo registrar la sesión en el hub: {ex.Message}"));
        }

        HasFinished = true;
        // TRES FINALES Y NO DOS (R10 §7). El de «sin ficheros tocados» se contaba como uno de los
        // otros, así que una sesión detenida antes de la primera edición decía «lo aplicado sigue
        // en tu clon» sin que hubiera nada aplicado.
        StatusMessage = Files.Count == 0
            ? "El agente no llegó a escribir nada: tu clon está como lo dejaste."
            : interrupted
                ? "Arreglo detenido. Lo aplicado sigue en tu clon, sin commitear."
                : "Arreglo terminado. Los cambios están en tu clon, sin commitear.";
        Changed?.Invoke();
        Completed?.Invoke(
            $"Arreglo asistido de {FindingAlias}: {Files.Count} fichero(s) tocado(s). "
            + "Los cambios están en tu clon sin commitear.");
    }

    /// <summary>
    /// Deja escrito QUÉ dejó escrito este arreglo (F9 §2): la ruta de cada fichero tocado y la
    /// huella de su contenido tal y como quedó.
    /// <para>
    /// Es lo que impide que el ciclo se muerda la cola. Sin esto, el commit con el que el usuario
    /// publique este arreglo haría que la unidad apareciera «cambiada desde su auditoría» — por
    /// culpa de la propia auditoría—, y cada arreglo realimentaría la lista de candidatas para
    /// siempre.
    /// </para>
    /// <para>
    /// <b>Se guarda el contenido y no un hash de commit porque el commit todavía no existe</b>
    /// (D-556: Atalaya no commitea). Cuando más tarde se busque quién publicó esto, se reconocerá
    /// por el contenido, que es lo único que la aplicación sabe con certeza ahora mismo.
    /// </para>
    /// </summary>
    private void WriteFixRecord(Ulid sessionId, Finding finding, string by, string? baseCommit)
    {
        if (Files.Count == 0 || string.IsNullOrWhiteSpace(_clonePath))
        {
            return;
        }

        var stamps = new List<FixFileStamp>();
        foreach (FixFileChange file in Files)
        {
            string abs = Path.Combine(
                _clonePath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(abs))
                {
                    stamps.Add(new FixFileStamp(
                        file.RelativePath, HashUtil.NormalizedContentHash(File.ReadAllBytes(abs))));
                }
            }
            catch (IOException)
            {
                // Un fichero que no se puede releer se queda sin huella y su unidad saldrá como
                // «cambiada»: re-auditar de más, que es la dirección segura.
            }
        }

        if (stamps.Count == 0)
        {
            return;
        }

        _hub.Store.WriteFix(new FixRecord
        {
            Id = sessionId,
            AppSlug = Slug,
            FindingId = finding.Id.ToString(),
            FindingAlias = FindingAlias,
            Utc = DateTimeOffset.UtcNow,
            By = by,
            BaseCommit = baseCommit,
            Files = stamps,
        });
    }

    private static string DefaultCommitTitle(Finding finding)
    {
        string alias = finding.DisplayId ?? finding.Id.ToString();
        string title = $"Arregla {finding.Title} ({alias})";
        return title.Length <= CommitSuggestion.MaxTitleLength
            ? title
            : title[..(CommitSuggestion.MaxTitleLength - 1)] + "…";
    }

    /// <summary>
    /// ARCHIVA un arreglo terminado (BUGFIX-CIERRE). Quita la pantalla de en medio; no toca el
    /// clon ni el historial.
    /// <para>
    /// <b>Cerrar no es descartar.</b> «Descartar todo» revierte lo que el agente escribió; esto
    /// solo retira la pantalla. Si quedaban cambios y el usuario decide conservarlos, se sueltan de
    /// la contabilidad de Atalaya —<paramref name="keepChanges"/>—: a partir de ahí son suyos y de
    /// su árbol, y la aplicación deja de ofrecerse a revertirlos.
    /// </para>
    /// </summary>
    /// <param name="keepChanges">
    /// El usuario ya ha dicho que conserva los ficheros modificados. Sin esto, un arreglo con
    /// cambios vivos NO se cierra: sería quitar de la vista el único camino al descarte.
    /// </param>
    public bool Close(bool keepChanges = false)
    {
        if (IsRunning || !HasSession)
        {
            return false;
        }

        if (HasPendingChanges && !keepChanges)
        {
            return false;
        }

        if (HasPendingChanges)
        {
            // Los cambios se quedan en el clon, y Atalaya deja de considerarlos suyos: cerrar el
            // conjunto es exactamente decir «esto ya no lo revierto yo». Es el MISMO gesto que
            // «Dar por bueno» — no se estrena un segundo camino para lo mismo.
            if (_set is not null)
            {
                _snapshots.Close(_set);
            }
        }

        OnUi(() =>
        {
            Conversation.Clear();
            Files.Clear();
        });

        HasFinished = false;
        HasFailed = false;
        FailureMessage = string.Empty;
        FailureDetail = string.Empty;
        FailureOffersModelChange = false;
        StatusMessage = string.Empty;
        Changed?.Invoke();
        return true;
    }

    private void Fail(string message, bool offersModelChange, string? detail = null)
    {
        FailureMessage = message;
        FailureOffersModelChange = offersModelChange;
        FailureDetail = detail ?? string.Empty;
        HasFailed = true;
        StatusMessage = message;
        IsRunning = false;
        _busy.Exit(AgentWork.Arreglo);
        Changed?.Invoke();
        Completed?.Invoke(message);
    }

    // ------------------------------------------------------------------ mando a distancia

    private void OnSteeringReady(IFixSteering steering) => _steering = steering;

    /// <summary>Pausa o reanuda. Ver <see cref="FixPauseGate"/> para qué significa exactamente.</summary>
    public void TogglePause()
    {
        if (!IsRunning)
        {
            return;
        }

        if (_pause.IsPaused)
        {
            _pause.Resume();
            IsPaused = false;
            Say(FixMessage.System("▶", "Continuando: el agente puede volver a escribir en el clon."));
        }
        else
        {
            _pause.Pause();
            IsPaused = true;
            Say(FixMessage.System("⏸",
                "En pausa. El agente puede seguir razonando, pero no se aplicará ningún cambio ni "
                + "se compilará nada hasta que continúes."));
        }

        Changed?.Invoke();
    }

    /// <summary>Detiene el agente. Lo ya aplicado se conserva: para deshacerlo está «Descartar todo».</summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        StatusMessage = "Deteniendo el arreglo…";
        _pause.Resume();
        IsPaused = false;
        _cts?.Cancel();
        CancelPendingQuestions();
        IFixSteering? steering = _steering;
        if (steering is not null)
        {
            _ = Task.Run(() => steering.AbortAsync(CancellationToken.None));
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Una orden del usuario a mitad de sesión. Se intenta inyectar en el turno en curso; si el
    /// runtime no la acepta, se ENCOLA para el siguiente turno y se dice — prometer inmediatez que
    /// no se puede garantizar sería peor que la espera.
    /// </summary>
    public async Task SendUserMessageAsync(string message)
    {
        string text = (message ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        Say(FixMessage.User(text));

        if (!IsRunning)
        {
            Say(FixMessage.System("⚠", "La sesión ya no está activa: el agente no va a leer esto."));
            return;
        }

        IFixSteering? steering = _steering;
        bool delivered = false;
        if (steering is not null)
        {
            try
            {
                delivered = await steering.SendAsync(text, _cts?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                delivered = false;
            }
        }

        if (delivered)
        {
            Say(FixMessage.System("→",
                "Enviado. Si el agente está a mitad de un paso, lo leerá al empezar el siguiente."));
            return;
        }

        lock (_queued)
        {
            _queued.Enqueue(text);
        }

        Say(FixMessage.System("→",
            "El agente está ocupado con este turno: tu mensaje se le entregará en cuanto lo termine."));
    }

    /// <summary>
    /// Qué se le manda al agente cuando su turno acaba sin haber cerrado. Es la vía por la que las
    /// órdenes encoladas llegan de verdad.
    /// </summary>
    private Task<string?> NextTurnAsync(CancellationToken ct)
    {
        if (_toolbox is { IsDone: true } || ct.IsCancellationRequested)
        {
            return Task.FromResult<string?>(null);
        }

        lock (_queued)
        {
            if (_queued.Count == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var lines = new List<string>();
            while (_queued.Count > 0)
            {
                lines.Add(_queued.Dequeue());
            }

            return Task.FromResult<string?>(
                "El usuario te ha escrito mientras trabajabas. Manda él:\n\n"
                + string.Join("\n", lines.Select(l => $"- {l}")));
        }
    }

    // ------------------------------------------------------------------ preguntas

    /// <inheritdoc />
    public Task<string?> AskAsync(
        string question, IReadOnlyList<string> choices, bool allowFreeform, CancellationToken ct)
    {
        // BUGFIX-LECTURA — la puerta por el otro lado. Una tarjeta que le pide al usuario que
        // pegue código lo convierte en la herramienta de lectura del agente, y el agente TIENE la
        // herramienta. Se le devuelve el no como decisión, sin tarjeta y sin molestar a nadie.
        if (FixAskGuard.AsksTheUserToPasteCode(question, choices))
        {
            Say(FixMessage.System("◆",
                "El agente ha pedido que le pegaras código. Se le ha devuelto a "
                + "read_file(startLine, endLine) sin molestarte: no hay nada que contestar."));
            return Task.FromResult<string?>(FixAskGuard.Refusal);
        }

        return AskCoreAsync(new FixQuestion
        {
            Text = string.IsNullOrWhiteSpace(question) ? "El agente necesita una respuesta." : question,
            Ask = FixAskKind.Decision,
            Choices = choices.Select(c => new FixChoice(c)).ToList(),
            AllowFreeform = allowFreeform || choices.Count == 0,
        });
    }

    /// <inheritdoc />
    public async Task<bool> ApproveFileAsync(string relativePath, string reason, CancellationToken ct)
    {
        string? answer = await AskCoreAsync(new FixQuestion
        {
            Text = $"El agente necesita modificar «{relativePath}» porque {reason}. ¿Lo autorizas?",
            Ask = FixAskKind.Autorizacion,
            Context = relativePath,
            Choices = new[] { new FixChoice(ApproveLabel), new FixChoice(DenyLabel) },
            AllowFreeform = false,
        });

        return string.Equals(answer, ApproveLabel, StringComparison.Ordinal);
    }

    /// <summary>Las dos únicas respuestas de una autorización. Constantes porque se comparan.</summary>
    public const string ApproveLabel = "Autorizar";

    public const string DenyLabel = "No lo toques";

    private Task<string?> AskCoreAsync(FixQuestion question)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[question] = completion;
        }

        OnUi(() =>
        {
            Conversation.Add(question);
            _currentAgentText = null;
        });

        StatusMessage = "El agente espera tu respuesta.";
        Changed?.Invoke();
        return completion.Task;
    }

    /// <summary>Responde una tarjeta. La llama la vista; es idempotente.</summary>
    public void Answer(FixQuestion? question, string? answer)
    {
        if (question is null || question.IsAnswered)
        {
            return;
        }

        string text = (answer ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        question.Answer = text;
        question.IsAnswered = true;

        TaskCompletionSource<string?>? completion;
        lock (_pending)
        {
            _pending.Remove(question, out completion);
        }

        completion?.TrySetResult(text);
        StatusMessage = "El agente está trabajando…";
        Changed?.Invoke();
    }

    /// <summary>
    /// Al detener o al reventar, las preguntas vivas se cierran sin respuesta. Sin esto el agente
    /// se quedaría bloqueado para siempre en su <c>ask_user</c> y la sesión no terminaría nunca.
    /// </summary>
    private void CancelPendingQuestions()
    {
        List<KeyValuePair<FixQuestion, TaskCompletionSource<string?>>> pending;
        lock (_pending)
        {
            pending = _pending.ToList();
            _pending.Clear();
        }

        foreach (var (question, completion) in pending)
        {
            OnUi(() =>
            {
                question.Answer = "(sesión detenida)";
                question.IsAnswered = true;
            });
            completion.TrySetResult(null);
        }
    }

    // ------------------------------------------------------------------ descartar

    /// <summary>
    /// Devuelve el clon a como estaba antes de la sesión. Si el agente sigue trabajando, primero
    /// se le detiene: restaurar debajo de alguien que está escribiendo dejaría un revoltijo que ya
    /// no es ni lo de antes ni lo de después.
    /// </summary>
    public FixRestoreReport DiscardAll()
    {
        if (_set is null)
        {
            return new FixRestoreReport(0, 0, Array.Empty<string>());
        }

        if (IsRunning)
        {
            Stop();
        }

        FixRestoreReport report = _snapshots.Restore(_set);
        OnUi(Files.Clear);
        Say(FixMessage.System("↺", report.Message));
        StatusMessage = report.Ok
            ? "Cambios descartados: tu clon vuelve a estar como estaba."
            : report.Message;
        Changed?.Invoke();
        return report;
    }

    /// <summary>El usuario da los cambios por buenos: el registro se cierra y deja de ofrecerse.</summary>
    public void AcceptChanges()
    {
        if (_set is not null)
        {
            _snapshots.Close(_set);
            Changed?.Invoke();
        }
    }

    // ------------------------------------------------------------------ «Me quedo los cambios»

    /// <summary>
    /// El commit del arreglo, si ya se hizo desde aquí (F32). Corto. <c>null</c> mientras no.
    /// <para>
    /// <b>Es un espejo del hub, no un estado suelto</b>: lo escribe
    /// <see cref="CommitChanges"/> y lo relee <see cref="RefreshCommitState"/> del
    /// <c>fixes/{ulid}.json</c>. Por eso volver por «Último arreglo» (D-572) reconstruye la
    /// pantalla commiteada sin que haya dos verdades que puedan desincronizarse.
    /// </para>
    /// </summary>
    [ObservableProperty] private string? _committedSha;

    /// <summary>Hay un commit en marcha: el botón se apaga mientras corre el <c>pre-commit</c>.</summary>
    [ObservableProperty] private bool _isCommitting;

    /// <summary>
    /// Relee del hub si este arreglo ya está commiteado. Lo llama la vista al entrar, que es el
    /// mismo camino por el que se vuelve desde el raíl: un solo camino, una sola pantalla (D-572).
    /// </summary>
    public void RefreshCommitState()
    {
        if (SessionId.Length == 0 || Slug.Length == 0)
        {
            return;
        }

        try
        {
            string? recorded = _hub.Store.ListFixes(Slug)
                .FirstOrDefault(f => string.Equals(f.Id.ToString(), SessionId, StringComparison.Ordinal))
                ?.CommitSha;

            // El commit MANDA sobre el registro. Si se hizo y la anotación no llegó a
            // escribirse, lo que hay que corregir es el registro, no olvidar el commit: por eso
            // la memoria de esta sesión gana cuando el hub no lo sabe (BUGFIX-F32).
            CommittedSha ??= recorded;

            // Y se REINTENTA lo que quedó sin anotar. Volver por «Último arreglo» es el mismo
            // camino (D-572), así que es también el momento natural de terminar lo que se quedó
            // a medias: silencioso, porque el usuario ya leyó el aviso cuando pasó.
            if (PendingStamp && CommittedSha is { Length: > 0 } sha)
            {
                RetryStamp(sha);
            }
        }
        catch (Exception)
        {
            // No poder leer el hub no puede tumbar la pantalla; el estado se queda como estaba.
        }
    }

    /// <summary>
    /// Termina lo que un fallo dejó sin anotar. Las tres piezas son idempotentes, así que
    /// reintentarlas todas es más simple —y más seguro— que llevar la cuenta de cuál falló.
    /// </summary>
    private void RetryStamp(string sha)
    {
        bool ok = true;
        foreach (Action stamp in new Action[]
                 {
                     () => StampRecord(sha), () => StampHistory(sha), () => StampReport(sha),
                 })
        {
            try
            {
                stamp();
            }
            catch (Exception)
            {
                ok = false;   // Se vuelve a intentar la próxima vez que se entre.
            }
        }

        if (!ok)
        {
            return;
        }

        PendingStamp = false;
        Publish(sha);
        Say(FixMessage.System("◆", $"Anotado el commit {sha} en el hub."));
        Changed?.Invoke();
    }

    /// <summary>
    /// <b>Commitea lo del arreglo, y solo lo del arreglo</b> (F32, que revoca D-556).
    /// <para>
    /// El clic del usuario ES la decisión que D-556 quería preservar: «Me quedo los cambios» no
    /// significaba nada más que cerrar un registro, y el trabajo se quedaba a medio camino con un
    /// mensaje ya redactado que había que copiar a mano. Lo que NO cambia: el agente sigue sin
    /// shell, sin git y sin red (D-543) — quien commitea es la aplicación, cuando lo pulsa una
    /// persona—, y el clon auditado <b>no se empuja nunca</b>.
    /// </para>
    /// <para>
    /// <b>Si falla, no se toca nada.</b> Ni el árbol, ni el registro de snapshots, ni el informe,
    /// ni el historial, ni la pantalla: se devuelve el motivo y se acabó. El orden de aquí abajo
    /// es exactamente eso — primero el commit, y solo si vuelve bien se escribe lo demás.
    /// </para>
    /// </summary>
    public const string PasoCommitear = "commitear";
    public const string PasoAnotar = "anotar";
    public const string PasoHistorial = "historial";
    public const string PasoInforme = "informe";
    public const string PasoPublicar = "publicar";

    /// <summary>
    /// <b>Los cinco pasos de quedarse los cambios</b>, y <b>ninguno se puede cancelar</b>
    /// (BUGFIX-F32, que corrige a D-1033).
    /// <para>
    /// D-1033 dijo «sin StepList: es una operación de una sola pieza». <b>Era falso</b>: son
    /// cinco, cuatro de ellos escriben en sitios distintos y cualquiera puede fallar por su
    /// cuenta — y el usuario no veía ninguno.
    /// </para>
    /// <para>
    /// <b>Y no hay «Cancelar», por el mismo criterio que la verificación</b> (D-1029): se
    /// ofrece mientras no se haya escrito nada, y aquí <b>lo primero que se hace es el
    /// commit</b>. Después de él no hay ningún punto en el que cancelar deje el clon como
    /// estaba, y antes de él no hay nada que esperar. Un botón que no puede cumplir lo que
    /// promete es peor que su ausencia.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StepSpec> CommitPlan { get; } = new[]
    {
        new StepSpec(PasoCommitear, "Commitear en tu clon"),
        new StepSpec(PasoAnotar, "Anotar el arreglo"),
        new StepSpec(PasoHistorial, "Anotar en el hallazgo"),
        new StepSpec(PasoInforme, "Reescribir el informe"),
        new StepSpec(PasoPublicar, "Publicar en el hub"),
    };

    /// <summary>La lista lista para colgarla de la barra del arreglo terminado.</summary>
    public static StepList NewCommitSteps(StepFlow flow = StepFlow.Vertical) => new(CommitPlan, flow);

    /// <param name="steps">
    /// Los pasos que se están enseñando (D-1029). Si no llega uno se crea aquí: el camino que
    /// ejecuta es el mismo se enseñe o no, porque dos caminos serían dos comportamientos.
    /// </param>
    public FixCommitResult CommitChanges(StepList? steps = null)
    {
        steps ??= NewCommitSteps();

        // Las guardas van ANTES de empezar ningún paso: son «no hay nada que hacer», no un
        // paso que falla, y pintar una lista en rojo para decirlo sería inventarse un intento.
        if (CommittedSha is { Length: > 0 } already)
        {
            return new FixCommitResult(false,
                Error: $"Este arreglo ya está commiteado en {already}.");
        }

        if (!HasFinished || Files.Count == 0)
        {
            return new FixCommitResult(false, Error: "No hay cambios del arreglo que commitear.");
        }

        List<string> paths = Files.Select(f => f.RelativePath).ToList();

        // ---- 1. Commitear. Es el único cuyo fallo lo deja TODO como estaba (D-1033).
        FixCommitResult result;
        try
        {
            result = steps.Run(PasoCommitear, () =>
            {
                FixCommitResult attempt = _committer.Commit(
                    _clonePath, paths, Commit.Title, Commit.Description);

                // El motivo viaja como excepción para que la línea del paso salga en rojo CON
                // él: `StepList` lee `ex.Message`. No es un error de programa —un `pre-commit`
                // que rechaza es un desenlace normal—, así que se recoge aquí mismo.
                return attempt.Ok
                    ? attempt
                    : throw new FixCommitRejected(attempt.Error ?? "no se pudo commitear");
            });
        }
        catch (FixCommitRejected rejected)
        {
            return new FixCommitResult(false, Error: rejected.Message);
        }

        string sha = result.Sha!;

        // El commit YA existe y no se deshace por nada de lo que venga (anti-objetivo
        // declarado). La pantalla pasa al estado commiteado AQUÍ, antes de anotar, porque es
        // lo que hay en el clon: si el registro no se puede escribir, lo que miente es el
        // registro, no el repositorio.
        AcceptChanges();
        CommittedSha = sha;
        StatusMessage = "Arreglo terminado. Los cambios están commiteados en tu clon.";
        Say(FixMessage.System("◆",
            $"Commiteado {sha} · {paths.Count} fichero(s). El push sigue siendo tuyo."));

        // ---- 2, 3 y 4. Cada uno falla por su cuenta y no se lleva a los siguientes: son tres
        // escrituras independientes, y que el informe no se pueda reescribir no es motivo para
        // dejar el hallazgo sin su evento.
        bool anotado = TryStamp(steps, PasoAnotar, () => StampRecord(sha));
        bool historial = TryStamp(steps, PasoHistorial, () => StampHistory(sha));
        bool informe = TryStamp(steps, PasoInforme, () => StampReport(sha));

        // ---- 5. Publicar. Que el hub no conteste no tumba nada: lo escrito está en disco y
        // sale con lo pendiente (D-1025).
        if (!steps.Run(PasoPublicar, () => Publish(sha)))
        {
            steps.Fail(PasoPublicar, StepList.PendingPublish);
        }

        // Lo que no se anotó se DICE, y se reintenta al volver por «Último arreglo».
        PendingStamp = !(anotado && historial && informe);
        if (PendingStamp)
        {
            Say(FixMessage.System("⚠",
                $"El commit {sha} está hecho, pero quedó sin anotar en el hub. Se reintenta al "
                + "volver a esta pantalla."));
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// Un paso de contabilidad: si falla, su línea queda en rojo con el motivo y la operación
    /// <b>sigue</b>. Devuelve si salió.
    /// </summary>
    private static bool TryStamp(StepList steps, string id, Action work)
    {
        try
        {
            steps.Run(id, work);
            return true;
        }
        catch (Exception)
        {
            // El paso ya está en rojo con `ex.Message` en su línea, que es donde hay que
            // leerlo. Aquí solo se decide que lo de después se intenta igual.
            return false;
        }
    }

    /// <summary>Quedó commiteado con algo sin anotar. Lo reintenta <see cref="RefreshCommitState"/>.</summary>
    [ObservableProperty] private bool _pendingStamp;

    /// <summary>
    /// <b>El registro del arreglo</b> (D-685): gana el hash y <b>conserva la huella</b>, que
    /// sigue siendo la prueba de atribución. Este hash es un atajo; sobrescribir la huella
    /// habría cambiado una certeza por una referencia que una enmienda posterior invalida.
    /// <para>Es idempotente: reintentarlo escribe lo mismo.</para>
    /// </summary>
    private void StampRecord(string sha)
    {
        FixRecord? record = _hub.Store.ListFixes(Slug)
            .FirstOrDefault(f => string.Equals(f.Id.ToString(), SessionId, StringComparison.Ordinal));
        if (record is null)
        {
            throw new InvalidOperationException(
                "no se encontró el registro de este arreglo en el hub");
        }

        record.CommitSha = sha;
        _hub.Store.WriteFix(record);
    }

    /// <summary>
    /// <b>El historial de la ficha</b>: un <c>FixCommitted</c> junto al <c>FixProposed</c>. El
    /// ESTADO del hallazgo no se toca (D-557 sigue en pie).
    /// <para>
    /// Idempotente <b>a propósito</b>: si el paso se reintenta al volver a la pantalla, el
    /// evento no se duplica. Un historial con dos veces el mismo commit se lee como dos
    /// commits.
    /// </para>
    /// </summary>
    private void StampHistory(string sha)
    {
        Finding? stored = _hub.Store.TryReadFinding(Slug, FindingId.ToString());
        if (stored is null)
        {
            throw new InvalidOperationException("el hallazgo ya no está en el hub");
        }

        if (stored.History.Any(h =>
                h.Event == FindingEvent.FixCommitted
                && (h.Detail ?? string.Empty).Contains(sha, StringComparison.Ordinal)))
        {
            return;
        }

        stored.Record(new HistoryEntry(
            DateTimeOffset.UtcNow, FindingEvent.FixCommitted, Environment.UserName,
            $"el usuario se quedó los cambios: commit {sha} "
            + $"({Files.Count} fichero(s)), sin publicar")
        {
            SessionId = SessionId,
        });
        _hub.Store.WriteFinding(Slug, stored);
    }

    /// <summary>
    /// <b>El informe del hub</b>, que se escribió al cerrar diciendo lo contrario. Se sustituye
    /// su párrafo de cabecera y nada más; <see cref="ReportBuilder.MarkFixCommitted"/> es
    /// idempotente, así que un reintento no lo estropea.
    /// </summary>
    private void StampReport(string sha)
    {
        if (ReportPath is not { Length: > 0 } || !File.Exists(ReportPath))
        {
            throw new InvalidOperationException("el informe de este arreglo no está en el hub");
        }

        string marked = ReportBuilder.MarkFixCommitted(File.ReadAllText(ReportPath), sha);
        _hub.Store.WriteReport(Slug, SessionId, marked);
    }

    /// <summary>
    /// <b>El hub, que es OTRO repositorio</b>: esto no empuja el clon auditado, jamás. Un push
    /// que no sale deja lo escrito «pendiente de publicar» (D-1025) y no tumba nada.
    /// </summary>
    private bool Publish(string sha)
        => _hub.Sync?.CommitAndPush($"fix: {FindingAlias} en {Slug} commiteado en {sha}") ?? true;

    /// <summary>
    /// Sesiones de arreglo anteriores cuyos cambios siguen en el clon sin cerrar (F6.9 §4). Cerrar
    /// la aplicación no puede llevarse el botón de descartar: el registro vive en
    /// <c>%LOCALAPPDATA%</c> y sobrevive al proceso.
    /// </summary>
    public IReadOnlyList<FixSnapshotSet> PendingFromPreviousSessions()
        => _snapshots.ListPending()
            .Where(s => !string.Equals(s.SessionId, SessionId, StringComparison.Ordinal))
            .ToList();

    /// <summary>Descarta los cambios de una sesión anterior.</summary>
    public FixRestoreReport DiscardPrevious(FixSnapshotSet set) => _snapshots.Restore(set);

    /// <summary>Cierra el registro de una sesión anterior sin tocar el clon.</summary>
    public void ClosePrevious(FixSnapshotSet set) => _snapshots.Close(set);

    // ------------------------------------------------------------------ eventos del toolbox

    private void Subscribe(FixToolbox toolbox)
    {
        toolbox.FileRead += OnFileRead;
        toolbox.Edited += OnEdited;
        toolbox.EditDenied += OnEditDenied;
        toolbox.BuildStarted += OnBuildStarted;
        toolbox.BuildFinished += OnBuildFinished;
        toolbox.Done += OnDone;
    }

    private void Unsubscribe(FixToolbox toolbox)
    {
        toolbox.FileRead -= OnFileRead;
        toolbox.Edited -= OnEdited;
        toolbox.EditDenied -= OnEditDenied;
        toolbox.BuildStarted -= OnBuildStarted;
        toolbox.BuildFinished -= OnBuildFinished;
        toolbox.Done -= OnDone;
    }

    private void OnFileRead(string path, bool found, string range)
        => Say(FixMessage.System("👁", found
            ? $"Ha leído {path}" + (range.Length > 0 ? $" ({range})" : string.Empty)
            : $"Buscó {path} y no está en el clon"));

    private void OnEdited(FixEditApplied edit) => OnUi(() =>
    {
        FixFileChange? file = Files.FirstOrDefault(
            f => string.Equals(f.RelativePath, edit.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            file = new FixFileChange
            {
                RelativePath = edit.RelativePath,
                Before = edit.Before,
                InScope = edit.InScope,
                Reason = edit.Reason,
            };
            Files.Add(file);
            if (Files.Count == 1)
            {
                file.IsSelected = true;
            }
        }
        else if (edit.Reason.Length > 0)
        {
            file.Reason = edit.Reason;
        }

        file.Refresh(edit.After);
        _currentAgentText = null;
        Conversation.Add(FixMessage.System("✎",
            $"Ha editado {edit.RelativePath} ({file.Tally})"
            + (edit.InScope ? string.Empty : " — fichero fuera del hallazgo, autorizado por ti")));
        OnPropertyChanged(nameof(TouchedCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        Changed?.Invoke();
    });

    private void OnEditDenied(string path, string reason)
        => Say(FixMessage.System("⛔", $"No autorizaste modificar {path}. El agente tendrá que replantearlo."));

    private void OnBuildStarted()
    {
        IsBuilding = true;
        Say(FixMessage.System("⚙", "Compilando y pasando los tests por encargo del agente…"));
        Changed?.Invoke();
    }

    private void OnBuildFinished(BuildVerdict verdict)
    {
        IsBuilding = false;
        HasBuildResult = true;
        LastVerdict = verdict;
        LastBuildOk = verdict.Ok;
        LastBuild = verdict.Summary;
        Say(FixMessage.System(verdict.Ok ? "✓" : "✗", Narrate(verdict)));
        Changed?.Invoke();
    }

    /// <summary>
    /// Lo que se dice en la conversación cuando termina una compilación. Un rojo solo se canta si
    /// es del cambio: los errores que ya estaban se nombran, pero no asustan (H9.1 §2).
    /// </summary>
    internal static string Narrate(BuildVerdict verdict)
    {
        if (verdict.TimedOut)
        {
            return "La compilación agotó el tiempo y se abortó.";
        }

        string what = verdict.TargetLabel.Length > 0 ? $"Compilado {verdict.TargetLabel}. " : string.Empty;
        string preexisting = verdict.PreexistingErrors > 0
            ? $" {verdict.PreexistingErrors} error(es) preexistente(s): ya fallaban antes del cambio."
            : string.Empty;
        string excluded = verdict.Excluded.Count > 0
            ? $" {verdict.Excluded.Count} proyecto(s) fuera del alcance de dotnet, sin contar."
            : string.Empty;

        if (verdict.NewErrors > 0)
        {
            return what + $"{verdict.NewErrors} error(es) NUEVO(S) que ha traído este cambio."
                   + preexisting + excluded;
        }

        string tests = verdict.TestsRun
            ? verdict.TestsOk ? " Los tests pasan." : " Los tests NO pasan."
            : " No hay tests que cubran lo tocado.";

        return what + "0 errores nuevos." + tests + preexisting + excluded;
    }

    private void OnDone(FixDoneArgs done)
    {
        _done = done;
        Say(FixMessage.System("◆", "El agente ha cerrado el arreglo."));
    }

    private void OnText(string chunk) => OnUi(() =>
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        if (_currentAgentText is null)
        {
            _currentAgentText = FixMessage.Agent(chunk);
            Conversation.Add(_currentAgentText);
            return;
        }

        _currentAgentText.Text += chunk;
    });

    /// <summary>Las tarifas del hub. Releídas, por lo mismo que en la sesión de auditoría.</summary>
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

    private void OnUsage(UsageSample sample) => OnUi(() =>
    {
        InputTokens += sample.InputTokens;
        OutputTokens += sample.OutputTokens;
        CacheReadTokens += sample.CacheReadTokens;
        CacheWriteTokens += sample.CacheWriteTokens;

        // F15 — el coste se DERIVA de los tokens con la tarifa del modelo, igual que en una sesión
        // de auditoría. El número que informa el proveedor está en peticiones premium, la unidad
        // que GitHub retiró: enseñarlo sería enseñar una moneda que ya no existe.
        //
        // F16-RETOQUE §1 — y si esta casa no factura a la organización, `Calculate` lo dice y no
        // hay número: el pie enseña llamadas y tokens, y el coste se lee «incluido en tu
        // suscripción de Claude».
        CostResult = CreditCalculator.Calculate(
            Model, Provider, InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens, ModelRates());
        Cost = CostResult.Credits;
        CostUnit = CostFormat.BillingUnit;

        // Y lo que el proveedor DECLARA, aparte y sin mezclarse con lo anterior: es un dato suyo,
        // no una cuenta nuestra, y solo vale para dejarlo escrito en el informe.
        if (sample.Cost is { } declared)
        {
            DeclaredCost = (DeclaredCost ?? 0m) + declared;
        }

        if (sample.CostUnit is { Length: > 0 } unit && string.IsNullOrEmpty(DeclaredCostUnit))
        {
            DeclaredCostUnit = unit;
        }

        // Cuántas LLAMADAS trae la muestra, no «una por muestra»: un proveedor puede mandar un
        // ajuste que corrige a las anteriores sin ser una llamada nueva (UsageSample.Calls).
        Calls += sample.Calls;
        Changed?.Invoke();
    });

    // ------------------------------------------------------------------ utilidades

    /// <summary>El código actual de cada ubicación, leído del clon (F6.7: nunca código viejo).</summary>
    private IReadOnlyList<FixCodeExcerpt> ReadCode(Finding finding)
    {
        var excerpts = new List<FixCodeExcerpt>();
        foreach (Location loc in finding.Locations.Take(3))
        {
            SnippetPanel panel = SnippetReader.Read(
                _clonePath, loc, finding.LastConfirmed.Commit,
                SymbolAnchor.Candidates(finding.Symbol, finding.Title));
            excerpts.Add(new FixCodeExcerpt(
                loc.Path,
                panel.Caption(loc.Path, panel.HighlightLine),
                panel.FirstLine,
                panel.Text,
                panel.Fact));
        }

        return excerpts;
    }

    /// <summary>
    /// Qué convenciones del proyecto le han llegado al agente, en una frase (F7). Se narra por lo
    /// mismo que se narran las referencias y la situación de tests: el usuario está decidiendo si
    /// se fía de este arreglo, y con qué criterio se le encargó es parte de esa decisión.
    /// </summary>
    private static string DirectiveLine(DirectiveBundle bundle)
    {
        string what = bundle.Included.Count == 0
            ? "Ninguna directiva cupo en el presupuesto"
            : $"El encargo lleva {bundle.Included.Count} directiva(s) del proyecto: "
              + string.Join(", ", bundle.Included.Select(d => d.Path));

        string budget = $" ({bundle.Tokens} de {bundle.Budget} tokens de presupuesto)";
        string omitted = bundle.Omitted.Count == 0
            ? string.Empty
            : $" Omitidas por presupuesto: {string.Join(", ", bundle.Omitted)} — el agente lo sabe.";
        string truncated = bundle.HasTruncation
            ? " Alguna viaja recortada por su principio, y el prompt lo dice."
            : string.Empty;

        return what + budget + "." + truncated + omitted;
    }

    private static string ReferenceLine(ReferenceReport refs)
    {
        if (!refs.Collected)
        {
            return $"El encargo va SIN la lista de llamadores: {refs.Unavailable}. "
                + "El agente lo sabe y tiene orden de no dar por hecho que el código no se usa.";
        }

        string how = refs.Precision == ReferencePrecision.Texto
            ? " (por búsqueda de texto: aproximadas)"
            : string.Empty;
        return refs.Total switch
        {
            0 => "No se encontraron llamadores en el clon" + how + ".",
            1 => "El encargo incluye 1 sitio de uso" + how + ".",
            _ => $"El encargo incluye {refs.Total} sitios de uso{how}.",
        };
    }

    private void Say(FixMessage message) => OnUi(() =>
    {
        Conversation.Add(message);
        _currentAgentText = null;
    });

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// Los eventos llegan de hilos de fondo; hay que marshalear antes de tocar la UI, y una
    /// escritura a la vez (BUGFIX-RELEASE §1). Las dos cosas las hace <see cref="ConversationWrites"/>,
    /// que desde F30 §3 es también el camino de la sesión en vivo.
    /// </summary>
    private void OnUi(Action action) => _writes.Send(action);

    private readonly ConversationWrites _writes = new();

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(HasSession));

    partial void OnHasFinishedChanged(bool value) => OnPropertyChanged(nameof(HasSession));

    partial void OnHasFailedChanged(bool value) => OnPropertyChanged(nameof(HasSession));
}
