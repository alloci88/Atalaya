using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Atalaya.Copilot;
using Atalaya.Domain;
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
    private readonly ICopilotAgent _agent;
    private readonly MachineConfigStore _machines;
    private readonly IUlidFactory _ulids;
    private readonly SettingsService _settings;
    private readonly ReferenceCollector _references;
    private readonly FixSnapshotStore _snapshots;
    private readonly AssistedFixLauncher _launcher;
    private readonly AgentBusyGate _busy;
    private readonly BuildRunner _builds;
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
        ICopilotAgent agent,
        MachineConfigStore machines,
        IUlidFactory ulids,
        SettingsService settings,
        ReferenceCollector references,
        FixSnapshotStore snapshots,
        AssistedFixLauncher launcher,
        AgentBusyGate busy,
        BuildRunner? builds = null,
        ModelResolver? models = null,
        DirectiveService? directives = null)
    {
        _hub = hub;
        _agent = agent;
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
    }

    // ------------------------------------------------------------------ estado observable

    /// <summary>La conversación: narración del agente, tarjetas de pregunta y avisos de la app.</summary>
    public ObservableCollection<FixEntry> Conversation { get; } = new();

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
    [ObservableProperty] private decimal? _cost;
    [ObservableProperty] private string _costUnit = "(unidad SDK)";
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
        : $"Arreglando {FindingAlias} en {Slug}"
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
        Commit.Title = string.Empty;
        Commit.Description = string.Empty;
        InputTokens = OutputTokens = CacheReadTokens = 0;
        Cost = null;
        Calls = 0;
        EndedUtc = null;
        StartedUtc = DateTimeOffset.UtcNow;
        StatusMessage = "Comprobando el clon y Copilot…";
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

            AgentReadiness readiness = await _agent.CheckAsync(CancellationToken.None);
            if (!readiness.Ready)
            {
                Fail(readiness.Message, readiness.Problem == AgentProblem.ModelUnavailable);
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
                $"Arreglo asistido de {FindingAlias} sobre tu clon en {_clonePath}. El árbol estaba "
                + "limpio: cualquier cambio que veas a partir de aquí lo ha hecho el agente."));

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
        catch (CopilotModelUnavailableException modelEx)
        {
            Fail(modelEx.Message, offersModelChange: true);
        }
        catch (CopilotAuthenticationException authEx)
        {
            Fail(authEx.Message, offersModelChange: false);
        }
        catch (Exception ex)
        {
            Fail($"El arreglo se ha interrumpido por un error: {ex.Message}", offersModelChange: false);
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
            Model = _agent.ModelName,
            Interrupted = interrupted,
            // De qué hallazgo era este arreglo (H9.1 §1). Sin esto, el informe de una sesión fix
            // nombra el hallazgo en su texto pero nadie puede navegar de vuelta a su ficha.
            FixFindingId = finding.Id.ToString(),
            FixFindingAlias = FindingAlias,
            Directives = _directiveBundle.Records.ToList(),
        };
        session.Usage.Add(InputTokens, OutputTokens, CacheReadTokens, 0, Cost);
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
                _hub.OrganizationName, TestSituation);
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

            _hub.Sync?.CommitAndPush($"fix: {FindingAlias} en {Slug} ({Files.Count} fichero(s))");
        }
        catch (Exception ex)
        {
            Say(FixMessage.System("⚠", $"No se pudo registrar la sesión en el hub: {ex.Message}"));
        }

        HasFinished = true;
        StatusMessage = interrupted
            ? "Arreglo detenido. Lo aplicado sigue en tu clon, sin commitear."
            : "Arreglo terminado. Los cambios están en tu clon, sin commitear.";
        Changed?.Invoke();
        Completed?.Invoke(
            $"Arreglo asistido de {FindingAlias}: {Files.Count} fichero(s) tocado(s). "
            + "Los cambios están en tu clon sin commitear.");
    }

    private static string DefaultCommitTitle(Finding finding)
    {
        string alias = finding.DisplayId ?? finding.Id.ToString();
        string title = $"Arregla {finding.Title} ({alias})";
        return title.Length <= CommitSuggestion.MaxTitleLength
            ? title
            : title[..(CommitSuggestion.MaxTitleLength - 1)] + "…";
    }

    private void Fail(string message, bool offersModelChange)
    {
        FailureMessage = message;
        FailureOffersModelChange = offersModelChange;
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
        => AskCoreAsync(new FixQuestion
        {
            Text = string.IsNullOrWhiteSpace(question) ? "El agente necesita una respuesta." : question,
            Kind = FixAskKind.Decision,
            Choices = choices.Select(c => new FixChoice(c)).ToList(),
            AllowFreeform = allowFreeform || choices.Count == 0,
        });

    /// <inheritdoc />
    public async Task<bool> ApproveFileAsync(string relativePath, string reason, CancellationToken ct)
    {
        string? answer = await AskCoreAsync(new FixQuestion
        {
            Text = $"El agente necesita modificar «{relativePath}» porque {reason}. ¿Lo autorizas?",
            Kind = FixAskKind.Autorizacion,
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

    private void OnFileRead(string path, bool found)
        => Say(FixMessage.System("👁", found ? $"Ha leído {path}" : $"Buscó {path} y no está en el clon"));

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

    private void OnUsage(UsageSample sample) => OnUi(() =>
    {
        InputTokens += sample.InputTokens;
        OutputTokens += sample.OutputTokens;
        CacheReadTokens += sample.CacheReadTokens;
        if (sample.Cost is { } c)
        {
            Cost = (Cost ?? 0m) + c;
        }

        if (!string.IsNullOrWhiteSpace(sample.CostUnit))
        {
            CostUnit = sample.CostUnit!;
        }

        Calls++;
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

    /// <summary>Los eventos llegan de hilos de fondo; hay que marshalear antes de tocar la UI.</summary>
    private static void OnUi(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(HasSession));

    partial void OnHasFinishedChanged(bool value) => OnPropertyChanged(nameof(HasSession));

    partial void OnHasFailedChanged(bool value) => OnPropertyChanged(nameof(HasSession));
}
