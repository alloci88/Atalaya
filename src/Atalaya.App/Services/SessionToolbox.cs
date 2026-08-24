using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// The tools the app hands the agent for one session (§6.2). It validates every payload against
/// the schema and the ruleId catalog, persists via <see cref="FindingIngestionService"/>, and
/// tracks which fingerprints were re-reported (for implicit resolution). The agent writes nothing.
/// </summary>
public sealed class SessionToolbox : IAuditToolbox
{
    private readonly string _slug;
    private readonly AuditMode _mode;
    private readonly DetectionStamp _stamp;
    private readonly FindingIngestionService _ingestion;
    private readonly string _clonePath;
    private readonly Action<Finding, IngestionKind>? _onFinding;

    public SessionToolbox(
        string slug, AuditMode mode, DetectionStamp stamp,
        FindingIngestionService ingestion, string clonePath,
        Action<Finding, IngestionKind>? onFinding = null)
    {
        _slug = slug;
        _mode = mode;
        _stamp = stamp;
        _ingestion = ingestion;
        _clonePath = clonePath;
        _onFinding = onFinding;
    }

    /// <summary>Fingerprints reported by the agent this session (drives implicit resolution).</summary>
    public HashSet<string> ReportedFingerprints { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Rejected payloads (F3 Hito 1c hotfix): every schema/catalog validation failure lands here
    /// with the reason, so no rejection is ever swallowed. The coordinator flushes this into
    /// <c>session.Notes</c> and it also feeds the log surface.
    /// </summary>
    public List<string> RejectedPayloads { get; } = new();

    /// <summary>
    /// Solo el motivo raíz de cada rechazo (F3.1 Bloque 0), para poder calcular el motivo
    /// dominante por unidad sin re-parsear <see cref="RejectedPayloads"/>.
    /// </summary>
    public List<string> RejectionReasons { get; } = new();

    /// <summary>
    /// Positive trace of every tool the agent invoked, in order, with the payload shape
    /// (F3 Hito 1c diagnóstico). This is the ONLY way to tell apart "the agent never called
    /// submit_findings" from "it called it but with an empty/broken payload" without a debugger.
    /// Format: <c>tool · detail</c>. Flushed to <c>session.Notes</c> by the coordinator.
    /// </summary>
    public List<string> ToolCallLog { get; } = new();

    /// <summary>How many <c>submit_finding(s)</c> invocations landed in this unit (F3 Hito 1c).</summary>
    public int SubmitInvocations { get; private set; }

    public SessionCounters Counters { get; } = new();

    public string? LastUnitSummary { get; private set; }

    /// <summary>
    /// Tool calls issued by the agent since the last <see cref="ResetToolCallCount"/> (Hito 1a).
    /// Feeds the per-unit token breakdown so we can see whether the bucle agéntico is spending
    /// tokens on many tiny turns or on a few big ones.
    /// </summary>
    public int ToolCallCount { get; private set; }

    public void ResetToolCallCount()
    {
        ToolCallCount = 0;
        SubmitInvocations = 0;
        ToolCallLog.Clear();
        RejectedPayloads.Clear();
        RejectionReasons.Clear();
    }

    public SubmitFindingResult SubmitFinding(SubmitFindingArgs args)
    {
        ToolCallCount++;
        SubmitInvocations++;
        ToolCallLog.Add($"submit_finding · title='{args?.Title ?? "(null)"}' locs={args?.Locations?.Length ?? 0}");
        return SubmitFindingCore(args!);
    }

    public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
    {
        // ONE tool call for the whole array (F3 Hito 1c) — but each item is validated & ingested
        // through the exact same path as the singular tool, so downstream invariants (fingerprint,
        // silences, implicit resolution) hold unchanged. Never swallow: an empty/null array is
        // recorded as a rejection so the operator can see the agent sent a malformed payload.
        ToolCallCount++;
        SubmitInvocations++;
        int count = findings?.Length ?? 0;
        ToolCallLog.Add($"submit_findings · items={count}");
        if (findings is null || findings.Length == 0)
        {
            string reason = "submit_findings recibido sin hallazgos (array nulo o vacío).";
            RejectedPayloads.Add(reason);
            RejectionReasons.Add(reason);
            Counters.Rejected++;
            return new SubmitFindingsResult(new[]
            {
                new SubmitFindingResult(false, Error: reason),
            });
        }

        var results = new List<SubmitFindingResult>(findings.Length);
        foreach (SubmitFindingArgs args in findings)
        {
            results.Add(SubmitFindingCore(args));
        }

        return new SubmitFindingsResult(results);
    }

    private SubmitFindingResult SubmitFindingCore(SubmitFindingArgs args)
    {
        if (args is null)
        {
            return Reject("submit_finding con args nulo", args: null);
        }

        // 1. ruleId must be a catalog rule or criterio.* (§6.2).
        if (!RuleCatalog.IsValid(args.RuleId))
        {
            return Reject($"ruleId desconocido '{args.RuleId}'. Usa el catálogo o criterio.<área>.", args);
        }

        if (!TryParsePillar(args.Pillar, out Pillar pillar))
        {
            return Reject($"pillar inválido '{args.Pillar}'.", args);
        }

        if (!TryParseSeverity(args.Severity, out Severity severity))
        {
            return Reject($"severity inválida '{args.Severity}'.", args);
        }

        // Tag ya no es parte de la tool (F3.1 Bloque 0): se infiere del ruleId.
        FindingTag tag = InferTag(args.RuleId);

        if (args.Locations is null || args.Locations.Length == 0)
        {
            return Reject("un hallazgo debe tener al menos una ubicación.", args);
        }

        var locations = args.Locations
            .Select(l => new Location(l.Path, l.Line, l.Snippet is null ? null : Fingerprint.ComputeSnippetHash(l.Snippet)))
            .ToList();

        var submitted = new SubmittedFinding(
            args.RuleId, pillar, tag, severity,
            args.Title, args.Description, args.Impact, args.Recommendation,
            locations, args.Symbol);

        string fingerprint = Fingerprint.Compute(submitted.RuleId, submitted.PrimaryPath, submitted.Symbol, submitted.Title);
        ReportedFingerprints.Add(fingerprint);

        IngestionOutcome outcome = _ingestion.Ingest(_slug, submitted, _mode, _stamp);
        Tally(outcome);

        if (outcome.Finding is not null)
        {
            _onFinding?.Invoke(outcome.Finding, outcome.Kind);
        }

        string? duplicateOf = outcome.Kind == IngestionKind.Reconfirmed ? outcome.Finding?.Id.ToString() : null;
        return new SubmitFindingResult(true, DuplicateOf: duplicateOf);
    }

    private SubmitFindingResult Reject(string reason, SubmitFindingArgs? args)
    {
        string title = args?.Title is { Length: > 0 } t ? t : "(sin título)";
        RejectedPayloads.Add($"{reason} · payload: {title}");
        RejectionReasons.Add(reason);
        Counters.Rejected++;
        return new SubmitFindingResult(false, Error: reason);
    }

    public void UnitDone(string unitPath, string summary)
    {
        ToolCallCount++;
        ToolCallLog.Add($"unit_done · unit='{unitPath}' summary='{Truncate(summary, 80)}'");
        LastUnitSummary = summary;
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s!.Length <= max ? s : s.Substring(0, max) + "…");

    /// <summary>
    /// The only extra code access allowed (§6.2): a lightweight signatures view of a dependency.
    /// Full Roslyn extraction for C# is a future enhancement; this heuristic works across stacks.
    /// </summary>
    public string ReadSignatures(string path)
    {
        ToolCallCount++;
        ToolCallLog.Add($"read_signatures · path='{path}'");
        try
        {
            string abs = Path.Combine(_clonePath, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                return $"(no encontrado: {path})";
            }

            var signatures = File.ReadLines(abs)
                .Select(l => l.Trim())
                .Where(LooksLikeSignature)
                .Take(80);
            return string.Join('\n', signatures);
        }
        catch (Exception ex)
        {
            return $"(error leyendo firmas: {ex.Message})";
        }
    }

    private static bool LooksLikeSignature(string line)
        => line.Contains('(') && (line.EndsWith(')') || line.EndsWith('{') || line.EndsWith(';'))
           && (line.StartsWith("public") || line.StartsWith("private") || line.StartsWith("internal")
               || line.StartsWith("protected") || line.StartsWith("def ") || line.StartsWith("func ")
               || line.StartsWith("fn ") || line.StartsWith("function"));

    private void Tally(IngestionOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case IngestionKind.New: Counters.New++; break;
            case IngestionKind.Reconfirmed: Counters.Confirmed++; break;
            case IngestionKind.Recurrence: Counters.New++; Counters.Recurrences++; break;
            case IngestionKind.SuppressedBySilence: Counters.SilencedRespected++; break;
        }
    }

    private static bool TryParsePillar(string s, out Pillar pillar)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "optimizacion": pillar = Pillar.Optimizacion; return true;
            case "mejoras": pillar = Pillar.Mejoras; return true;
            case "errores": pillar = Pillar.Errores; return true;
            default: pillar = default; return false;
        }
    }

    private static bool TryParseSeverity(string s, out Severity severity)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "critica": severity = Severity.Critica; return true;
            case "alta": severity = Severity.Alta; return true;
            case "media": severity = Severity.Media; return true;
            case "baja": severity = Severity.Baja; return true;
            default: severity = default; return false;
        }
    }

    /// <summary>
    /// Derives the tag from the ruleId (F3.1 Bloque 0): <c>criterio.&lt;área&gt;</c> → Criterio,
    /// cualquier otro ruleId del catálogo → Checklist. Determinista y alineado con §6.2, así que
    /// la app lo calcula sin pedírselo al modelo.
    /// </summary>
    private static FindingTag InferTag(string ruleId)
        => !string.IsNullOrEmpty(ruleId) && ruleId.StartsWith("criterio.", StringComparison.OrdinalIgnoreCase)
            ? FindingTag.Criterio
            : FindingTag.Checklist;
}
