using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
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
}

/// <summary>
/// Orchestrates a full audit session end-to-end (§5.1): claims → per-unit agent audit with live
/// ingestion → implicit resolution → inventory update → session + report → commit/push. UI-agnostic;
/// V5 subscribes to its events. Superficial never resolves (anti-degradation, §5.3).
/// </summary>
public sealed class SessionCoordinator
{
    private readonly HubContext _hub;
    private readonly FindingIngestionService _ingestion;
    private readonly MachineConfigStore _machines;
    private readonly IUlidFactory _ulids;
    private readonly ICopilotAgent _agent;
    private readonly CycleService? _cycles;
    private readonly StatusExporter? _statusExporter;

    public SessionCoordinator(
        HubContext hub, FindingIngestionService ingestion, MachineConfigStore machines,
        IUlidFactory ulids, ICopilotAgent agent,
        CycleService? cycles = null, StatusExporter? statusExporter = null)
    {
        _hub = hub;
        _ingestion = ingestion;
        _machines = machines;
        _ulids = ulids;
        _agent = agent;
        _cycles = cycles;
        _statusExporter = statusExporter;
    }

    public event Action<string, string>? UnitPhaseChanged;   // (path, phase)
    public event Action<Finding, IngestionKind>? FindingReported;
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
        void OnFinding(Finding f, IngestionKind kind)
        {
            if (kind is IngestionKind.New or IngestionKind.Recurrence)
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
        var toolbox = new SessionToolbox(request.Slug, request.Mode, stamp, _ingestion, clone!, OnFinding);
        var auditedPaths = new HashSet<string>(StringComparer.Ordinal);

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
                string prompt = PromptComposer.ComposeUnitPrompt(unit.Path, content, brief, request.Mode);

                var breakdown = new UnitUsageBreakdown
                {
                    Unit = unit.Path,
                    PromptTokensEstimate = EstimateTokens(prompt),
                };
                session.UsageBreakdown.Add(breakdown);
                currentBreakdown = breakdown;
                toolbox.ResetToolCallCount();

                budgetTripped = false;
                unitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bool overBudget = false;
                try
                {
                    await _agent.AuditUnitAsync(
                        new AuditUnitRequest(unit.Path, content, prompt, app.Stack, request.Mode), toolbox, unitCts.Token);
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

                if (overBudget)
                {
                    string note = $"presupuesto superado ({breakdown.InputTokens + breakdown.OutputTokens} > {maxTokensPerUnit} tokens)";
                    session.Units.Add(new UnitVerdictRecord(unit.Path, unit.Module, "presupuesto-superado", note));
                    session.Notes.Add($"{unit.Path}: {note}");
                    UnitPhaseChanged?.Invoke(unit.Path, "over-budget");
                    continue;
                }

                session.Units.Add(new UnitVerdictRecord(unit.Path, unit.Module, "auditada", toolbox.LastUnitSummary));
                auditedPaths.Add(Fingerprint.NormalizePath(unit.Path));
                UnitPhaseChanged?.Invoke(unit.Path, "done");
            }
        }
        finally
        {
            _agent.TextStreamed -= OnText;
            _agent.UsageReported -= OnUsage;
        }

        int resolved = ApplyImplicitResolution(request, auditedPaths, toolbox.ReportedFingerprints, commit, by, now);

        MarkAuditedInInventory(inventory, auditedPaths, sessionId);
        _hub.Store.WriteInventory(request.Slug, inventory);

        ReleaseClaims(request.Slug, units);

        session.EndedUtc = DateTimeOffset.UtcNow;
        session.Counters = toolbox.Counters;
        session.Counters.Resolved += resolved;
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

    // §0 implicit resolution: in lotes/integral, an active finding whose locations all fall in
    // audited units and that was NOT re-reported becomes resolved. Superficial/verify never do this.
    private int ApplyImplicitResolution(
        SessionRequest request, HashSet<string> auditedPaths, HashSet<string> reportedFingerprints,
        string commit, string by, DateTimeOffset now)
    {
        if (request.Mode is not (AuditMode.Lotes or AuditMode.Integral))
        {
            return 0;
        }

        int resolved = 0;
        foreach (Finding f in _hub.Store.ListFindings(request.Slug).Where(f => f.Status == FindingStatus.Activo))
        {
            bool allCovered = f.Locations.Count > 0
                && f.Locations.All(l => auditedPaths.Contains(Fingerprint.NormalizePath(l.Path)));
            if (allCovered && !reportedFingerprints.Contains(f.Fingerprint))
            {
                f.Resolve(new ResolutionStamp(now, ResolutionVia.Implicita, request.Mode, commit, by,
                    "cubierta por la sesión y no re-reportada"));
                _hub.Store.WriteFinding(request.Slug, f);
                resolved++;
            }
        }

        return resolved;
    }

    private static void MarkAuditedInInventory(InventoryCycle inventory, HashSet<string> auditedPaths, Ulid sessionId)
    {
        foreach (InventoryUnit u in inventory.Units)
        {
            if (auditedPaths.Contains(Fingerprint.NormalizePath(u.Path)) && u.State != UnitState.Grande)
            {
                u.State = UnitState.Auditada;
                u.AuditedInSession = sessionId;
            }
        }
    }

    /// <summary>
    /// Rough token estimate for the initial prompt (Hito 1a). ~4 chars per token is the same
    /// heuristic OpenAI/Anthropic docs quote for English/code; good enough to spot a bloated brief
    /// against actual SDK <c>InputTokens</c> without adding a tokenizer dependency.
    /// </summary>
    private static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
