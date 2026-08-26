using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>What to audit (§5.1–5.3).</summary>
public sealed record SessionRequest(string Slug, AuditMode Mode, IReadOnlyList<string> UnitPaths);

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
    IReadOnlyList<string> Units);

/// <summary>Outcome of a session run.</summary>
public sealed record SessionResult(Ulid SessionId, SessionCounters Counters, bool ReachedZeroPending)
{
    /// <summary>True when this session actually triggered the cycle close (§5.1).</summary>
    public bool CycleClosed { get; init; }

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
    private readonly ICopilotAgent _agent;
    private readonly SettingsService _settings;
    private readonly CycleService? _cycles;
    private readonly StatusExporter? _statusExporter;
    private readonly DisplayIdService? _aliases;

    public SessionCoordinator(
        HubContext hub, FindingIngestionService ingestion, ReconciliationService reconciliation,
        MachineConfigStore machines, IUlidFactory ulids, ICopilotAgent agent,
        SettingsService settings,
        CycleService? cycles = null, StatusExporter? statusExporter = null,
        DisplayIdService? aliases = null)
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
    }

    public event Action<string, string>? UnitPhaseChanged;   // (path, phase)

    // ---- Superficie de OBSERVACIÓN (F5.2) ----
    // Eventos puramente aditivos: emiten datos que el coordinador ya calculaba y no cambian
    // ninguna decisión. Existen porque V5 necesita narrar el barrido pasada a pasada, y antes
    // esa información solo llegaba a las notas de la sesión cuando ya había terminado.

    /// <summary>La sesión arranca: identidad y unidades reclamadas. Sirve para la marca de sesión abierta.</summary>
    public event Action<SessionStarted>? Started;

    /// <summary>(unidad, número de pasada) al empezar cada pasada del barrido.</summary>
    public event Action<string, int>? PassStarted;

    /// <summary>(unidad, registro de la pasada) al cerrarla, con sus contadores y si quedó seca.</summary>
    public event Action<string, UnitPassRecord>? PassFinished;

    /// <summary>(unidad, veredicto, consumo) al cerrar una unidad entera.</summary>
    public event Action<string, UnitVerdictRecord, UnitUsageBreakdown>? UnitFinished;

    /// <summary>(hallazgo, qué le pasó: nuevo | reconfirmed | resolved | needsreview | silencerespected).</summary>
    public event Action<Finding, string>? FindingReported;
    public event Action<string>? TextStreamed;
    public event Action<long, long, decimal?, string?>? UsageUpdated;  // cumulative in/out/cost/costUnit

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
        string commit = GitInfo.HeadSha(clone);
        string by = _hub.ResolveIdentity().Name;
        Ulid sessionId = _ulids.NewUlid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var session = new AuditSession
        {
            Id = sessionId,
            AppSlug = request.Slug,
            Mode = request.Mode,
            By = by,
            Machine = Environment.MachineName,
            StartedUtc = now,
            Commit = commit,
            CycleN = app.CurrentCycle,
            Model = _agent.ModelName,
            MaxPassesPerUnit = Math.Max(1, _settings.Current.MaxPassesPerUnit),
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
            units.Select(u => u.Path).ToList()));

        bool stopped = ct.IsCancellationRequested;
        if (!stopped)
        {
            PublishClaims(request.Slug, units, inventory, by);
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
        long maxTokensPerUnit = Math.Max(0, app.Thresholds.MaxTokensPerUnit);
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
        void OnUsage(UsageSample u)
        {
            session.Usage.Add(u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.Cost);
            if (u.CostUnit is not null && string.IsNullOrEmpty(session.Usage.Currency))
            {
                session.Usage.Currency = u.CostUnit;
            }

            if (currentBreakdown is not null)
            {
                currentBreakdown.Calls++;
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
                if (maxTokensPerUnit > 0
                    && passInput + passOutput > maxTokensPerUnit
                    && !budgetTripped)
                {
                    budgetTripped = true;
                    try { unitCts?.Cancel(); } catch { /* already disposed */ }
                }
            }

            UsageUpdated?.Invoke(session.Usage.InputTokens, session.Usage.OutputTokens, session.Usage.Cost, session.Usage.Currency);
        }

        _agent.TextStreamed += OnText;
        _agent.UsageReported += OnUsage;

        // El modelo va en el sello (F5.1b): es quien hace la observación, y hace falta para poder
        // nombrar a quién discrepa cuando dos modelos se contradicen sobre el mismo hallazgo.
        var stamp = new DetectionStamp(now, request.Mode, commit, by, Model: _agent.ModelName);

        // Los tipos de problema silenciados en ESTA app (F5.12), congelados al arrancar: los
        // mismos en el prompt de todas las unidades y en la lectura de lo que el auditor declara.
        // Uno que caduque a mitad de sesión no cambia las reglas del juego a media partida.
        PatternSilenceSet patterns = PatternSilenceSet.From(_hub.Store.ListPatternSilences(request.Slug), now);

        // Cuánto ha suprimido cada patrón en ESTA sesión, para el informe y para el contador de
        // trabajo del propio patrón.
        var suppressionTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var suppressionExemplars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var toolbox = new SessionToolbox(
            request.Slug, request.Mode, stamp, _ingestion, _reconciliation, _hub.Store, clone!, OnFinding,
            patterns);
        var auditedPaths = new HashSet<string>(StringComparer.Ordinal);
        int incompleteUnits = 0;

        try
        {
            string brief = PillarBrief.For(app.Stack);
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

                // F4.1 — BARRIDO HASTA AGOTAR. Una pasada del auditor no cubre la unidad: declara
                // haberla cubierto y, al repetir, encuentra más (2026-08-25: la pasada 1 dijo haber
                // revisado ConvertToDetId/ConvertToSeq y la 2 halló tres defectos ahí). Así que la
                // app repite hasta que una pasada queda SECA. Las pasadas son internas: para el
                // usuario una auditoría sigue siendo una unidad completa.
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
                IReadOnlyList<Finding> withoutVerdict = Array.Empty<Finding>();
                toolbox.BeginUnitSweep(unit.Path, unitContentHash);
                int locationsInUnit = 0;

                for (int pass = 1; pass <= maxPasses && !dry && !overBudget; pass++)
                {
                    ct.ThrowIfCancellationRequested();

                    PassStarted?.Invoke(unit.Path, pass);
                    IReadOnlyList<Finding> existing = _reconciliation.ExistingForUnit(request.Slug, unit.Path);
                    toolbox.BeginPass(existing);
                    var listed = existing.Select(ToExisting).ToList();
                    string prompt = PromptComposer.ComposeUnitPrompt(
                        unit.Path, content, brief, request.Mode, listed, patterns);
                    breakdown.PromptTokensEstimate += EstimateTokens(prompt);

                    budgetTripped = false;
                    passInput = 0;
                    passOutput = 0;
                    unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    try
                    {
                        await _agent.AuditUnitAsync(
                            new AuditUnitRequest(unit.Path, content, prompt, app.Stack, request.Mode, listed, patterns),
                            toolbox, unitCts.Token);
                    }
                    catch (OperationCanceledException) when (budgetTripped && !ct.IsCancellationRequested)
                    {
                        overBudget = true;
                    }
                    finally
                    {
                        breakdown.ToolCalls += toolbox.ToolCallCount;
                        unitCts.Dispose();
                        unitCts = null;
                    }

                    dry = !overBudget && toolbox.PassIsDry;
                    withoutVerdict = toolbox.PendingVerdicts;
                    coverageSummary = toolbox.LastUnitSummary ?? coverageSummary;
                    locationsInUnit += toolbox.PassLocationsAdded;
                    var passRecord = new UnitPassRecord(
                        pass, toolbox.PassNew, toolbox.PassConfirmed, toolbox.PassResolved,
                        toolbox.PassNonVerifiable, toolbox.PassRejected, dry, toolbox.LastUnitSummary,
                        toolbox.PassLocationsAdded);
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

                    if (toolbox.SubmitInvocations == 0 && toolbox.PassNew == 0 && !dry)
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

                currentBreakdown = null;
                string? dominantReason = DominantReason(reasonsInUnit);
                session.Counters.Rejected += rejectedInUnit;

                if (overBudget)
                {
                    long spent = breakdown.InputTokens + breakdown.OutputTokens;
                    string summary = $"Cortada por presupuesto: {spent}/{maxTokensPerUnit} tokens"
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

                // Tope alcanzado sin secarse: el barrido no garantiza cobertura. Visible, nunca
                // silencioso — es justo el fallo que nos trajo hasta aquí.
                bool coverageIncomplete = !dry;
                if (coverageIncomplete)
                {
                    unitVerdict = "cobertura posiblemente incompleta";
                    unitSummary = $"Cobertura posiblemente incompleta: {passes.Count} pasada(s) sin llegar a seca "
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
        finally
        {
            _agent.TextStreamed -= OnText;
            _agent.UsageReported -= OnUsage;
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
        if (interrupted)
        {
            int pendientes = units.Count - session.Units.Count;
            session.Notes.Add(
                $"Sesión detenida por el usuario: {session.Units.Count} de {units.Count} unidad(es) "
                + $"procesadas, {pendientes} sin auditar. Lo hecho hasta aquí queda registrado.");
        }

        _hub.Store.WriteSession(session);

        // F5.12: cada patrón acumula lo que ha suprimido. Es «cuánto trabaja este patrón», el
        // único dato con el que se puede decidir si sigue mereciendo la pena o si se puso por un
        // susto puntual. Se escribe tras la sesión y nunca la tumba.
        AccumulatePatternWork(request.Slug, session.SuppressionsByPattern, patterns);

        int pending = inventory.Units.Count(u => u.State == UnitState.Pendiente);
        int large = inventory.Units.Count(u => u.State == UnitState.Grande);
        string report = ReportBuilder.BuildSessionReport(app, session, newFindings, pending, large);
        _hub.Store.WriteReport(request.Slug, sessionId.ToString(), report);

        _hub.Sync?.CommitAndPush(
            $"session: {request.Mode.ToString().ToLowerInvariant()} {request.Slug} {session.Units.Count} unidades"
            + (interrupted ? " (detenida)" : ""));

        // El alias legible se reparte TRAS el push (§2, D-228): numerar antes de publicar es lo
        // que hacía colisionar a dos máquinas que auditaban a la vez. Lo que se asigna aquí viaja
        // en el push de la siguiente acción — el alias no es identidad, así que no urge.
        AssignAliases(request.Slug);

        // Courtesy ESTADO.md export into the audited repo (§7).
        _statusExporter?.ExportIfEnabled(request.Slug);

        // If the cycle is now empty, attempt the close (only one user actually closes it).
        // Una sesion detenida NO cierra ciclo: no ha cubierto lo que decia cubrir.
        bool cycleClosed = false;
        if (!interrupted && pending == 0 && request.Mode is AuditMode.Lotes or AuditMode.Integral)
        {
            cycleClosed = _cycles?.TryCloseCycle(request.Slug, app.CurrentCycle) ?? false;
        }

        return new SessionResult(sessionId, session.Counters, ReachedZeroPending: pending == 0)
        {
            CycleClosed = cycleClosed,
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

    private static List<InventoryUnit> ResolveUnits(SessionRequest request, InventoryCycle inventory)
    {
        if (request.Mode == AuditMode.Integral)
        {
            return inventory.Units.Where(u => u.State != UnitState.Grande).ToList();
        }

        var wanted = new HashSet<string>(request.UnitPaths, StringComparer.Ordinal);
        return inventory.Units.Where(u => wanted.Contains(u.Path)).ToList();
    }

    private void PublishClaims(string slug, IReadOnlyList<InventoryUnit> units, InventoryCycle inv, string by)
    {
        foreach (InventoryUnit unit in units)
        {
            _hub.Store.WriteClaim(slug, new Claim
            {
                Unit = unit.Path,
                Module = unit.Module,
                By = by,
                Machine = Environment.MachineName,
                Utc = DateTimeOffset.UtcNow,
            });
        }

        _hub.Sync?.CommitAndPush($"claims: {by} {units.Count} unidades en {slug}");
    }

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
            f.Status == FindingStatus.Silenciado ? "silenciado" : "activo");
    }

    /// <summary>
    /// Rough token estimate for the initial prompt (Hito 1a). ~4 chars per token is the same
    /// heuristic OpenAI/Anthropic docs quote for English/code; good enough to spot a bloated brief
    /// against actual SDK <c>InputTokens</c> without adding a tokenizer dependency.
    /// </summary>
    private static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

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
