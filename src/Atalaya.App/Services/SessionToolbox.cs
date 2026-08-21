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

    public SessionCounters Counters { get; } = new();

    public string? LastUnitSummary { get; private set; }

    public SubmitFindingResult SubmitFinding(SubmitFindingArgs args)
    {
        // 1. ruleId must be a catalog rule or criterio.* (§6.2).
        if (!RuleCatalog.IsValid(args.RuleId))
        {
            return new SubmitFindingResult(false, Error: $"ruleId desconocido '{args.RuleId}'. Usa el catálogo o criterio.<área>.");
        }

        if (!TryParsePillar(args.Pillar, out Pillar pillar))
        {
            return new SubmitFindingResult(false, Error: $"pillar inválido '{args.Pillar}'.");
        }

        if (!TryParseSeverity(args.Severity, out Severity severity))
        {
            return new SubmitFindingResult(false, Error: $"severity inválida '{args.Severity}'.");
        }

        if (!TryParseTag(args.Tag, out FindingTag tag))
        {
            return new SubmitFindingResult(false, Error: $"tag inválido '{args.Tag}'.");
        }

        if (args.Locations.Count == 0)
        {
            return new SubmitFindingResult(false, Error: "un hallazgo debe tener al menos una ubicación.");
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

    public void UnitDone(string unitPath, string summary) => LastUnitSummary = summary;

    /// <summary>
    /// The only extra code access allowed (§6.2): a lightweight signatures view of a dependency.
    /// Full Roslyn extraction for C# is a future enhancement; this heuristic works across stacks.
    /// </summary>
    public string ReadSignatures(string path)
    {
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

    private static bool TryParseTag(string s, out FindingTag tag)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "checklist": tag = FindingTag.Checklist; return true;
            case "criterio": tag = FindingTag.Criterio; return true;
            default: tag = default; return false;
        }
    }
}
