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
    private readonly ICopilotAgent _agent;
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
    private readonly List<string> _new = new();
    private readonly List<string> _confirmed = new();
    private readonly List<string> _resolved = new();
    private readonly List<string> _disputed = new();
    private readonly List<string> _refused = new();
    private readonly List<string> _nonVerifiable = new();
    private readonly List<string> _silenced = new();
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
        Func<SessionCoordinator> coordinatorFactory, ICopilotAgent agent, OpenSessionStore marker,
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
    [ObservableProperty] private string _costUnit = "(unidad SDK)";
    [ObservableProperty] private int _calls;
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

    /// <summary>Línea de la barra de estado inferior mientras corre.</summary>
    public string ProgressLine => !IsRunning
        ? string.Empty
        : $"Auditando {AppSlug} · unidad {Math.Max(1, UnitIndex)}/{UnitCount}"
          + (CurrentPassNumber > 0 ? $" · pasada {CurrentPassNumber}" : "");

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
    private void Fail(string message, bool offersModelChange)
    {
        FailureMessage = message;
        FailureOffersModelChange = offersModelChange;
        HasFailed = true;
        StatusMessage = message;
        Changed?.Invoke();
        Failed?.Invoke(message);
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
        HeaderText = $"{request.Mode} · {request.Slug}";
        UnitCount = displayPaths.Count;
        UnitIndex = 0;
        CurrentPassNumber = 0;
        InputTokens = OutputTokens = CacheReadTokens = CacheWriteTokens = 0;
        Cost = null;
        Calls = 0;
        SessionId = string.Empty;
        ReportPath = string.Empty;
        HasFinished = false;
        HasFailed = false;
        FailureMessage = string.Empty;
        FailureOffersModelChange = false;
        EndedUtc = null;
        StartedUtc = DateTimeOffset.UtcNow;
        StatusMessage = "Comprobando Copilot…";
        Changed?.Invoke();
    }

    private async Task RunAsync(SessionRequest request)
    {
        SessionCoordinator coordinator = _coordinatorFactory();
        Subscribe(coordinator);
        try
        {
            AgentReadiness readiness = await _agent.CheckAsync(CancellationToken.None);
            if (!readiness.Ready)
            {
                Fail(readiness.Message, readiness.Problem == AgentProblem.ModelUnavailable);
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

            SessionId = result.SessionId.ToString();
            ReportPath = _hub is null ? string.Empty : _hub.HubPaths.ReportFile(AppSlug, SessionId);
            BuildSummary(result);
            StatusMessage = Describe(result);
            HasFinished = true;
            Completed?.Invoke(result);
        }
        catch (CopilotModelUnavailableException modelEx)
        {
            // F5.15: el fallo con remedio de un clic. Se nombra el modelo y se ofrece Ajustes.
            Fail(modelEx.Message, offersModelChange: true);
        }
        catch (CopilotAuthenticationException authEx)
        {
            Fail(authEx.Message, offersModelChange: false);   // §6.1: ayuda, no el error crudo del SDK
        }
        catch (Exception ex)
        {
            Fail($"La sesión se ha interrumpido por un error: {ex.Message}", offersModelChange: false);
        }
        finally
        {
            Unsubscribe(coordinator);
            // La marca de sesión abierta se retira SIEMPRE que el proceso siga vivo: si llegamos
            // aquí, esta sesión no necesita recuperación (D-110).
            _marker.Delete();
            _openMarker = null;
            EndedUtc = DateTimeOffset.UtcNow;
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            _busy.Exit(AgentWork.Auditoria);
            Changed?.Invoke();
        }
    }

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
    }

    private void OnStarted(SessionStarted started)
    {
        SessionId = started.Id.ToString();
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
            // Colapsa la unidad anterior y abre ésta: la actual siempre expandida.
            if (_currentUnit is not null && !ReferenceEquals(_currentUnit, unit))
            {
                _currentUnit.IsExpanded = false;
            }

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
        UnitProgress? unit = Units.FirstOrDefault(u => u.Path == path);
        if (unit is null)
        {
            return;
        }

        unit.CurrentPass = pass;
        CurrentPassNumber = pass;
        if (_currentPass is not null)
        {
            _currentPass.IsExpanded = false;
        }

        _currentPass = new PassProgress { Index = pass, Headline = $"Pasada {pass}" };
        unit.Passes.Add(_currentPass);
        _currentText = null;
        OnPropertyChanged(nameof(ProgressLine));
        Changed?.Invoke();
    });

    private void OnPassFinished(string path, UnitPassRecord record) => OnUi(() =>
    {
        PassProgress? pass = _currentPass;
        if (pass is null)
        {
            return;
        }

        string verdicts = record.Confirmed + record.Resolved + record.NonVerifiable > 0
            ? $" · veredictos: {record.Confirmed} presente"
              + (record.Resolved > 0 ? $" · {record.Resolved} arreglado" : "")
              + (record.NonVerifiable > 0 ? $" · {record.NonVerifiable} no-verificable" : "")
            : "";

        pass.Headline = record.Dry
            ? $"Pasada {record.Index} — seca"
            : $"Pasada {record.Index} — {record.New} nuevo(s)"
              + (record.LocationsAdded > 0 ? $", {record.LocationsAdded} ubicación(es)" : "");

        // Los veredictos van en las DOS ramas (F5.14). Estaban solo en la de la pasada con
        // aportación, así que el caso más común —una pasada seca en la que el auditor confirmó
        // cinco hallazgos como presentes— se narraba «seca — unidad completa» y nada más: se leía
        // como «aquí no ha pasado nada» cuando habían pasado cinco reconfirmaciones. «Presente» no
        // tiene línea propia por unidad a propósito (sería una fila por hallazgo en cada pasada),
        // pero contarlo agregado es la diferencia entre resumir y callar.
        Add(pass, ActivityEntry.Event(
            record.Dry ? "✓" : "↻",
            (record.Dry
                ? $"Pasada {record.Index} seca — unidad completa"
                : $"Pasada {record.Index}: {record.New} nuevo(s)"
                  + (record.LocationsAdded > 0 ? $", {record.LocationsAdded} ubicación(es) añadida(s)" : ""))
            + verdicts));
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
        unit.ResultLine = $"{unit.Findings} hallazgo(s) · {unit.PassCount} pasada(s)"
            + (usage.Cost is { } c ? $" · coste {c:0.##}" : $" · {unit.Tokens} tokens");

        if (verdict.Verdict is "presupuesto-superado" or "incompleta"
            || verdict.CoverageIncomplete || verdict.RejectedPayloads > 0)
        {
            _incidents.Add($"{path} — {verdict.Verdict}: {verdict.Summary}");
        }

        if (verdict.Verdict == "presupuesto-superado")
        {
            Add(_currentPass, ActivityEntry.Event("✂", $"Cortada por presupuesto — {verdict.Summary}"));
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
        string title = finding.Title;
        string alias = finding.DisplayId ?? finding.Id.ToString();

        switch (kind)
        {
            case "nuevo":
                Findings.Add(finding);
                _new.Add($"[{finding.Severity}] {title}");
                Add(_currentPass, ActivityEntry.Event("＋", $"Hallazgo: {title}", finding.Severity));
                break;

            case "ubicaciones":
                Add(_currentPass, ActivityEntry.Event("⊕", $"Ubicaciones añadidas a {alias}"));
                break;

            case "disputed":
                _disputed.Add($"{alias} «{title}» — el auditor sostiene que nunca fue un defecto");
                Add(_currentPass, ActivityEntry.Event("⚖", $"Disputado: {title}"));
                break;

            case "resolutionrefused":
                _refused.Add($"{alias} «{title}» — «arreglado» sin evidencia de cambio, degradado a presente");
                Add(_currentPass, ActivityEntry.Event("⚠", $"«Arreglado» sin evidencia de cambio: {title}"));
                break;

            case "resolved":
                _resolved.Add($"{alias} «{title}»");
                Add(_currentPass, ActivityEntry.Event("✔", $"Resuelto: {title}"));
                break;

            case "needsreview":
                _nonVerifiable.Add($"{alias} «{title}»");
                break;

            case "silencerespected":
                _silenced.Add($"{alias} «{title}»");
                break;

            case "reconfirmed":
                _confirmed.Add($"{alias} «{title}»");
                break;
        }
    });

    /// <summary>
    /// Texto del agente. Se acumula en la última entrada de texto de la pasada en curso: los
    /// deltas llegan en trozos de pocos caracteres y una fila por trozo haría inmanejable la lista.
    /// </summary>
    private void OnText(string chunk) => OnUi(() =>
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        if (_currentText is null)
        {
            _currentText = ActivityEntry.Text_(chunk);
            Add(_currentPass, _currentText);
            return;
        }

        _currentText.Text += chunk;
    });

    private void OnUsage(long input, long output, decimal? cost, string? costUnit) => OnUi(() =>
    {
        InputTokens = input;
        OutputTokens = output;
        Cost = cost;
        Calls++;
        CostUnit = string.IsNullOrWhiteSpace(costUnit) ? "(unidad SDK)" : costUnit!;
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

        Line("Nuevos", c.New, "Hallazgos que no estaban en el baseline de la unidad.", _new);
        Line("Confirmados", c.Confirmed, "El auditor los declaró presentes; sigue contando la máquina de confianza.", _confirmed);
        Line("Resueltos", c.Resolved,
            "Cerrados con evidencia de cambio: la unidad cambió desde la última vez que se vieron.", _resolved);
        Line("Disputados", c.Disputed,
            "El auditor sostiene que nunca fueron un defecto. NO están resueltos: los decides tú en Hallazgos.",
            _disputed, warning: true);
        Line("«Arreglado» sin evidencia", c.ResolutionsRefused,
            "El auditor los dio por arreglados, pero la unidad no había cambiado: degradados a presente.",
            _refused, warning: true);
        Line("No verificables", c.NoVerificables,
            "No se pueden determinar desde esta unidad; quedan marcados para revisión.", _nonVerifiable);
        Line("Silenciados detectados", c.SilencedRespected,
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

        if (result.Interrupted)
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
        return (result.Interrupted ? "Sesión detenida; lo auditado queda guardado." : "Sesión completada.")
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

    /// <summary>Los eventos del coordinador llegan de un hilo de fondo; hay que marshalear.</summary>
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
}
