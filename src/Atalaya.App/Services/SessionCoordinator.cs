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

        PublishClaims(request.Slug, units, inventory, by);

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

                // F4: la lista de hallazgos existentes de la unidad viaja EN EL PROMPT y acota los
                // ULIDs sobre los que el auditor puede pronunciarse. Es lo que sustituye a toda la
                // maquinaria de fingerprints: la identidad la decide quien sabe decidirla.
                IReadOnlyList<Finding> existing = _reconciliation.ExistingForUnit(request.Slug, unit.Path);
                toolbox.BeginUnit(existing);
                var listed = existing.Select(ToExisting).ToList();
                string prompt = PromptComposer.ComposeUnitPrompt(unit.Path, content, brief, request.Mode, listed);

                var breakdown = new UnitUsageBreakdown
                {
                    Unit = unit.Path,
                    PromptTokensEstimate = EstimateTokens(prompt),
                };
                session.UsageBreakdown.Add(breakdown);
                currentBreakdown = breakdown;

                budgetTripped = false;
                unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bool overBudget = false;
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
                    breakdown.ToolCalls = toolbox.ToolCallCount;
                    currentBreakdown = null;
                    unitCts.Dispose();
                    unitCts = null;
                }

                // Snapshot de rechazos ANTES de volcarlos: la moda alimenta el UnitVerdictRecord
                // para que el corte sea auto-descriptivo ("Cortada por presupuesto · 25 rechazos:
                // tag inválido") en informe y UI (F3.1 Bloque 0).
                int rejectedInUnit = toolbox.RejectedPayloads.Count;
                string? dominantReason = DominantReason(toolbox.RejectionReasons);
                session.Counters.Rejected += rejectedInUnit;

                if (rejectedInUnit > 0)
                {
                    // Never swallow: surface every rejected payload in the session notes so an
                    // operator can see why "9 tool calls, 0 findings" happened.
                    foreach (string r in toolbox.RejectedPayloads)
                    {
                        session.Notes.Add($"{unit.Path}: rechazo · {r}");
                    }

                    toolbox.RejectedPayloads.Clear();
                    toolbox.RejectionReasons.Clear();
                }

                // Positive trace: log every tool the agent invoked in this unit. This is the
                // evidence that lets us tell apart "no invocations at all" from "invoked but
                // rejected" without a debugger.
                foreach (string entry in toolbox.ToolCallLog)
                {
                    session.Notes.Add($"{unit.Path}: tool · {entry}");
                }

                if (toolbox.SubmitInvocations == 0)
                {
                    session.Notes.Add(
                        $"{unit.Path}: sin invocaciones a submit_finding(s) — el agente terminó sin reportar hallazgos por tool.");
                }

                toolbox.ToolCallLog.Clear();

                if (overBudget)
                {
                    long spent = breakdown.InputTokens + breakdown.OutputTokens;
                    string summary = $"Cortada por presupuesto: {spent}/{maxTokensPerUnit} tokens"
                        + (rejectedInUnit > 0
                            ? $" · {rejectedInUnit} rechazos" + (dominantReason is null ? "" : $": {dominantReason}")
                            : "");
                    session.Units.Add(new UnitVerdictRecord(
                        unit.Path, unit.Module, "presupuesto-superado", summary,
                        rejectedInUnit, dominantReason));
                    session.Notes.Add($"{unit.Path}: {summary}");
                    UnitPhaseChanged?.Invoke(unit.Path, "over-budget");
                    continue;
                }

                // F4: sin veredicto no se toca nada. La unidad se marca incompleta y se nombra a
                // los hallazgos huérfanos — visible, pero no bloquea la sesión.
                IReadOnlyList<Finding> withoutVerdict = toolbox.PendingVerdicts;
                string unitVerdict = "auditada";
                string? unitSummary = toolbox.LastUnitSummary;
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
                    rejectedInUnit, dominantReason, withoutVerdict.Count));
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
