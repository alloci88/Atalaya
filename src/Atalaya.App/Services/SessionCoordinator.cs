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
    private readonly CycleService? _cycles;
    private readonly StatusExporter? _statusExporter;

    public SessionCoordinator(
        HubContext hub, FindingIngestionService ingestion, ReconciliationService reconciliation,
        MachineConfigStore machines, IUlidFactory ulids, ICopilotAgent agent,
        CycleService? cycles = null, StatusExporter? statusExporter = null)
    {
        _hub = hub;
        _ingestion = ingestion;
        _reconciliation = reconciliation;
        _machines = machines;
        _ulids = ulids;
        _agent = agent;
        _cycles = cycles;
        _statusExporter = statusExporter;
    }

    public event Action<string, string>? UnitPhaseChanged;   // (path, phase)

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
        };

        // «Detener» solo podia actuar dentro del bucle de unidades: todo lo previo (publicar
        // claims, que hace commit+push) es incancelable, asi que pulsar Detener durante esa fase
        // no hacia nada visible. Estos dos cortes hacen que la sesion aborte en cuanto la fase
        // termina, en vez de seguir y auditar la unidad igualmente.
        ct.ThrowIfCancellationRequested();
        PublishClaims(request.Slug, units, inventory, by);
        ct.ThrowIfCancellationRequested();

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
        int maxPasses = Math.Max(1, app.Thresholds.MaxPassesPerUnit);
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

                if (maxTokensPerUnit > 0
                    && currentBreakdown.InputTokens + currentBreakdown.OutputTokens > maxTokensPerUnit
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

        var stamp = new DetectionStamp(now, request.Mode, commit, by);
        var toolbox = new SessionToolbox(
            request.Slug, request.Mode, stamp, _ingestion, _reconciliation, clone!, OnFinding);
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
                    session.Units.Add(new UnitVerdictRecord(unit.Path, unit.Module, "no-localizado", null));
                    UnitPhaseChanged?.Invoke(unit.Path, "missing");
                    continue;
                }

                string content = await File.ReadAllTextAsync(abs, ct);

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
                toolbox.BeginUnitSweep();

                for (int pass = 1; pass <= maxPasses && !dry && !overBudget; pass++)
                {
                    ct.ThrowIfCancellationRequested();

                    IReadOnlyList<Finding> existing = _reconciliation.ExistingForUnit(request.Slug, unit.Path);
                    toolbox.BeginPass(existing);
                    var listed = existing.Select(ToExisting).ToList();
                    string prompt = PromptComposer.ComposeUnitPrompt(unit.Path, content, brief, request.Mode, listed);
                    breakdown.PromptTokensEstimate += EstimateTokens(prompt);

                    budgetTripped = false;
                    unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    try
                    {
                        await _agent.AuditUnitAsync(
                            new AuditUnitRequest(unit.Path, content, prompt, app.Stack, request.Mode, listed),
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
                    passes.Add(new UnitPassRecord(
                        pass, toolbox.PassNew, toolbox.PassConfirmed, toolbox.PassResolved,
                        toolbox.PassNonVerifiable, toolbox.PassRejected, dry, toolbox.LastUnitSummary));

                    // Nunca se traga un rechazo: cada pasada vuelca los suyos, etiquetados.
                    rejectedInUnit += toolbox.RejectedPayloads.Count;
                    reasonsInUnit.AddRange(toolbox.RejectionReasons);
                    foreach (string r in toolbox.RejectedPayloads)
                    {
                        session.Notes.Add($"{unit.Path} (pasada {pass}): rechazo · {r}");
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
                    session.Units.Add(new UnitVerdictRecord(
                        unit.Path, unit.Module, "presupuesto-superado", summary,
                        rejectedInUnit, dominantReason, Passes: passes));
                    session.Notes.Add($"{unit.Path}: {summary}");
                    UnitPhaseChanged?.Invoke(unit.Path, "over-budget");
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
                        + $"(la última aportó {passes[^1].New} nuevo(s))"
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

                session.Units.Add(new UnitVerdictRecord(
                    unit.Path, unit.Module, unitVerdict, unitSummary,
                    rejectedInUnit, dominantReason, withoutVerdict.Count, passes, coverageIncomplete));
                auditedPaths.Add(CodeAnchor.NormalizePath(unit.Path));
                UnitPhaseChanged?.Invoke(unit.Path, withoutVerdict.Count > 0 ? "incomplete" : "done");
            }
        }
        finally
        {
            _agent.TextStreamed -= OnText;
            _agent.UsageReported -= OnUsage;
        }

        MarkAuditedInInventory(inventory, auditedPaths, sessionId);
        _hub.Store.WriteInventory(request.Slug, inventory);

        ReleaseClaims(request.Slug, units);

        session.EndedUtc = DateTimeOffset.UtcNow;
        session.Counters = toolbox.Counters;
        _hub.Store.WriteSession(session);

        int pending = inventory.Units.Count(u => u.State == UnitState.Pendiente);
        int large = inventory.Units.Count(u => u.State == UnitState.Grande);
        string report = ReportBuilder.BuildSessionReport(app, session, newFindings, pending, large);
        _hub.Store.WriteReport(request.Slug, sessionId.ToString(), report);

        _hub.Sync?.CommitAndPush($"session: {request.Mode.ToString().ToLowerInvariant()} {request.Slug} {session.Units.Count} unidades");

        // Courtesy ESTADO.md export into the audited repo (§7).
        _statusExporter?.ExportIfEnabled(request.Slug);

        // If the cycle is now empty, attempt the close (only one user actually closes it).
        bool cycleClosed = false;
        if (pending == 0 && request.Mode is AuditMode.Lotes or AuditMode.Integral)
        {
            cycleClosed = _cycles?.TryCloseCycle(request.Slug, app.CurrentCycle) ?? false;
        }

        return new SessionResult(sessionId, session.Counters, ReachedZeroPending: pending == 0)
        {
            CycleClosed = cycleClosed,
            IncompleteUnits = incompleteUnits,
        };
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
