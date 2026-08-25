using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// The tools the app hands the agent for one session (§6.2). It validates every payload against
/// the schema and the ruleId catalog and persists. The agent writes nothing.
/// <para>
/// F4: además de <c>submit_finding(s)</c> expone <c>report_verdicts</c>, la reconciliación por el
/// auditor. La app aplica decisiones tipadas por ULID y NO deduce nada: lo que el auditor no
/// declara, no se toca (y la unidad queda incompleta).
/// </para>
/// </summary>
public sealed class SessionToolbox : IAuditToolbox
{
    private readonly string _slug;
    private readonly AuditMode _mode;
    private readonly DetectionStamp _stamp;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly Storage.HubStore _hub;
    private readonly string _clonePath;
    private readonly Action<Finding, string>? _onFinding;

    /// <summary>Hallazgos existentes mostrados al auditor en la unidad en curso, por ULID.</summary>
    private readonly Dictionary<string, Finding> _listed = new(StringComparer.Ordinal);

    /// <summary>ULIDs de la unidad en curso sobre los que el auditor YA se ha pronunciado.</summary>
    private readonly HashSet<string> _verdicted = new(StringComparer.Ordinal);

    /// <summary>
    /// Claves de los hallazgos nuevos ya admitidos en ESTA sesión. Única salvaguarda de duplicado
    /// que queda (F4): el mismo título normalizado en la misma ubicación no entra dos veces.
    /// </summary>
    private readonly HashSet<string> _submittedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Hallazgos creados durante el barrido de la unidad EN CURSO (F4.1). Entre pasadas de un
    /// mismo barrido el código no cambia, así que un veredicto «arreglado» sobre uno de éstos es
    /// una contradicción del modelo, no una resolución: se ignora y se registra.
    /// </summary>
    private readonly Dictionary<string, Finding> _createdInSweep = new(StringComparer.Ordinal);

    /// <summary>Ruta normalizada de la unidad en curso: acota dónde pueden caer las ubicaciones.</summary>
    private string _unitPath = string.Empty;

    public SessionToolbox(
        string slug, AuditMode mode, DetectionStamp stamp,
        FindingIngestionService ingestion, ReconciliationService reconciliation, Storage.HubStore hub,
        string clonePath, Action<Finding, string>? onFinding = null)
    {
        _hub = hub;
        _slug = slug;
        _mode = mode;
        _stamp = stamp;
        _ingestion = ingestion;
        _reconciliation = reconciliation;
        _clonePath = clonePath;
        _onFinding = onFinding;
    }

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
    /// Tool calls issued by the agent since the last <see cref="BeginUnit"/> (Hito 1a).
    /// Feeds the per-unit token breakdown so we can see whether the bucle agéntico is spending
    /// tokens on many tiny turns or on a few big ones.
    /// </summary>
    public int ToolCallCount { get; private set; }

    /// <summary>
    /// F4: hallazgos listados en la unidad en curso sobre los que el auditor NO se pronunció.
    /// Si no está vacío, la unidad se cierra como <c>incompleta</c> y esos hallazgos quedan
    /// intactos — nunca resueltos por omisión.
    /// </summary>
    public IReadOnlyList<Finding> PendingVerdicts
        => _listed.Where(kv => !_verdicted.Contains(kv.Key)).Select(kv => kv.Value).ToList();

    /// <summary>
    /// Arranca una unidad: fija los hallazgos existentes que se le han mostrado al auditor (los
    /// únicos ULIDs sobre los que puede pronunciarse) y reinicia los contadores por unidad.
    /// </summary>
    public void BeginUnit(IReadOnlyList<Finding> existing) => BeginPass(existing);

    /// <summary>Arranca el barrido de una unidad nueva: olvida lo creado en la unidad anterior.</summary>
    public void BeginUnitSweep(string unitPath = "")
    {
        _createdInSweep.Clear();
        _unitPath = CodeAnchor.NormalizePath(unitPath);
    }

    /// <summary>
    /// Arranca UNA pasada del barrido: fija los hallazgos existentes que se le muestran al auditor
    /// (los únicos ULIDs sobre los que puede pronunciarse) y pone a cero los contadores de pasada.
    /// </summary>
    public void BeginPass(IReadOnlyList<Finding> existing)
    {
        _listed.Clear();
        _verdicted.Clear();
        foreach (Finding f in existing)
        {
            _listed[f.Id.ToString()] = f;
        }

        ToolCallCount = 0;
        SubmitInvocations = 0;
        ToolCallLog.Clear();
        RejectedPayloads.Clear();
        RejectionReasons.Clear();
        LastUnitSummary = null;
        PassNew = PassConfirmed = PassResolved = PassNonVerifiable = PassRejected = 0;
        PassLocationsAdded = 0;
        PassHasNonPresentVerdict = false;
    }

    /// <summary>Hallazgos nuevos admitidos en la pasada en curso.</summary>
    public int PassNew { get; private set; }

    public int PassConfirmed { get; private set; }

    public int PassResolved { get; private set; }

    public int PassNonVerifiable { get; private set; }

    public int PassRejected { get; private set; }

    /// <summary>
    /// Ubicaciones añadidas a hallazgos existentes en la pasada (F4.1). Cuentan como rendimiento:
    /// extender un defecto sistémico a un sitio nuevo ES cobertura, aunque no cree un hallazgo.
    /// </summary>
    public int PassLocationsAdded { get; private set; }

    /// <summary>Algún veredicto de la pasada no fue «presente» (ni silencio respetado).</summary>
    public bool PassHasNonPresentVerdict { get; private set; }

    /// <summary>
    /// La pasada queda SECA cuando no aportó nada nuevo y todos sus veredictos fueron «presente».
    /// Es la condición de parada del barrido (F4.1).
    /// </summary>
    public bool PassIsDry => PassNew == 0 && PassLocationsAdded == 0 && !PassHasNonPresentVerdict;

    // ---------- F4.1 · extensión de ubicaciones ----------

    /// <summary>
    /// Añade ubicaciones a un hallazgo ya existente. Sin esta tool, la única manera que tenía el
    /// auditor de decir "el mismo defecto también está en la línea 105" era crear otro hallazgo,
    /// y un defecto sistémico se fragmentaba en uno por miembro (D-090).
    /// </summary>
    public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
    {
        ToolCallCount++;
        int count = locations?.Length ?? 0;
        ToolCallLog.Add($"add_locations · id='{findingId}' locs={count}");

        if (string.IsNullOrWhiteSpace(findingId))
        {
            return RejectExtension("add_locations sin findingId.");
        }

        string id = findingId.Trim();

        // Solo lo que el auditor tiene delante: la lista de la unidad, o lo que él mismo ha
        // reportado en este barrido. Cualquier otro ULID no toca nada.
        if (!_listed.TryGetValue(id, out Finding? finding) && !_createdInSweep.TryGetValue(id, out finding))
        {
            return RejectExtension(
                $"findingId desconocido '{id}': solo puedes extender hallazgos listados en esta unidad "
                + "o que hayas reportado en ella.");
        }

        if (locations is null || locations.Length == 0)
        {
            return RejectExtension($"add_locations sobre {id} sin ubicaciones.");
        }

        var existing = new HashSet<string>(
            finding.Locations.Select(l => $"{CodeAnchor.NormalizePath(l.Path)}:{l.Line}"), StringComparer.Ordinal);

        int added = 0;
        foreach (SubmitLocation l in locations)
        {
            string path = CodeAnchor.NormalizePath(l.Path ?? string.Empty);

            // Fuera de la unidad no: el auditor solo ha visto esta unidad, así que no puede
            // afirmar nada sobre otro fichero.
            if (_unitPath.Length > 0 && !string.Equals(path, _unitPath, StringComparison.Ordinal))
            {
                RejectExtension($"ubicación fuera de la unidad en {id}: '{l.Path}' (unidad: {_unitPath}).");
                continue;
            }

            if (!existing.Add($"{path}:{l.Line}"))
            {
                continue; // ya la tenía: no es un fallo, simplemente no aporta
            }

            finding.Locations.Add(new Location(
                l.Path!, l.Line, l.Snippet is null ? null : CodeAnchor.ComputeSnippetHash(l.Snippet)));
            added++;
        }

        if (added == 0)
        {
            return new AddLocationsResult(true, 0);
        }

        finding.History.Add(new HistoryEntry(_stamp.Utc, FindingEvent.Confirmed, _stamp.By,
            $"auditor: {added} ubicación(es) añadidas — mismo defecto en otros puntos de la unidad"));
        _hub.WriteFinding(_slug, finding);
        PassLocationsAdded += added;
        Counters.LocationsAdded += added;
        _onFinding?.Invoke(finding, "ubicaciones");
        return new AddLocationsResult(true, added);
    }

    private AddLocationsResult RejectExtension(string reason)
    {
        RejectedPayloads.Add($"add_locations rechazado · {reason}");
        RejectionReasons.Add(reason);
        Counters.Rejected++;
        PassRejected++;
        return new AddLocationsResult(false, 0, reason);
    }

    // ---------- F4 · reconciliación ----------

    public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
    {
        ToolCallCount++;
        int count = verdicts?.Length ?? 0;
        ToolCallLog.Add($"report_verdicts · items={count}");
        if (verdicts is null || verdicts.Length == 0)
        {
            string reason = "report_verdicts recibido sin veredictos (array nulo o vacío).";
            RejectedPayloads.Add(reason);
            RejectionReasons.Add(reason);
            Counters.Rejected++;
            PassRejected++;
            return new ReportVerdictsResult(new[] { new ReportVerdictResult(false, reason) });
        }

        var results = new List<ReportVerdictResult>(verdicts.Length);
        foreach (VerdictArgs v in verdicts)
        {
            results.Add(ApplyVerdict(v));
        }

        return new ReportVerdictsResult(results);
    }

    private ReportVerdictResult ApplyVerdict(VerdictArgs? v)
    {
        if (v is null || string.IsNullOrWhiteSpace(v.FindingId))
        {
            return RejectVerdict("veredicto sin findingId.");
        }

        string id = v.FindingId.Trim();

        // Error tipado: el auditor solo puede pronunciarse sobre lo que se le ha listado. Un ULID
        // inventado o de otra unidad NO toca nada — se le devuelve el motivo para que se corrija.
        if (!_listed.TryGetValue(id, out Finding? finding))
        {
            return RejectVerdict(
                $"findingId desconocido '{id}': no está en la lista de hallazgos existentes de esta unidad. "
                + "Usa solo los ULID listados; si el problema no está en la lista, repórtalo con submit_findings.");
        }

        if (!ReconciliationService.TryParseVerdict(v.Verdict, out ReconcileVerdict verdict))
        {
            return RejectVerdict($"verdict inválido '{v.Verdict}' para {id}. Usa presente | arreglado | no-verificable.");
        }

        if (!_verdicted.Add(id))
        {
            return RejectVerdict($"veredicto duplicado sobre {id} en la misma unidad.");
        }

        // Guarda de coherencia (F4.1): el código no cambia entre pasadas del mismo barrido, así
        // que declarar «arreglado» un hallazgo que el propio barrido acaba de crear es una
        // contradicción del modelo. Se degrada a «presente» y se deja constancia: «arreglado»
        // solo tiene sentido entre auditorías distintas, con código cambiado de por medio.
        if (verdict == ReconcileVerdict.Arreglado && _createdInSweep.ContainsKey(id))
        {
            string note = $"veredicto 'arreglado' ignorado sobre {id}: lo reportó este mismo "
                + "barrido y el código no ha cambiado entre pasadas. Se mantiene presente.";
            RejectedPayloads.Add(note);
            RejectionReasons.Add("arreglado incoherente dentro del mismo barrido");
            verdict = ReconcileVerdict.Presente;
        }

        ReconcileOutcome outcome = _reconciliation.Apply(_slug, finding, verdict, v.Evidence, _mode, _stamp);
        switch (outcome)
        {
            case ReconcileOutcome.Reconfirmed: Counters.Confirmed++; PassConfirmed++; break;
            case ReconcileOutcome.Resolved: Counters.Resolved++; PassResolved++; PassHasNonPresentVerdict = true; break;
            case ReconcileOutcome.NeedsReview: Counters.NoVerificables++; PassNonVerifiable++; PassHasNonPresentVerdict = true; break;
            case ReconcileOutcome.SilenceRespected: Counters.SilencedRespected++; break;
        }

        _onFinding?.Invoke(finding, outcome.ToString().ToLowerInvariant());
        return new ReportVerdictResult(true);
    }

    private ReportVerdictResult RejectVerdict(string reason)
    {
        RejectedPayloads.Add($"veredicto rechazado · {reason}");
        RejectionReasons.Add(reason);
        Counters.Rejected++;
        PassRejected++;
        return new ReportVerdictResult(false, reason);
    }

    // ---------- hallazgos nuevos ----------

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
        // through the exact same path as the singular tool. Never swallow: an empty/null array is
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
            PassRejected++;
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
            .Select(l => new Location(l.Path, l.Line, l.Snippet is null ? null : CodeAnchor.ComputeSnippetHash(l.Snippet)))
            .ToList();

        var submitted = new SubmittedFinding(
            args.RuleId, pillar, tag, severity,
            args.Title, args.Description, args.Impact, args.Recommendation,
            locations, args.Symbol);

        // F4: la ÚNICA deduplicación superviviente — el mismo payload dos veces en la misma
        // sesión. Contra el histórico no se compara nada: eso es trabajo del auditor.
        if (!_submittedKeys.Add(submitted.SessionDuplicateKey))
        {
            return Reject("duplicado exacto dentro de esta sesión (mismo título y misma ubicación).", args);
        }

        Finding created = _ingestion.Create(submitted, _slug, _mode, _stamp);
        _createdInSweep[created.Id.ToString()] = created;
        Counters.New++;
        PassNew++;
        _onFinding?.Invoke(created, "nuevo");
        return new SubmitFindingResult(true);
    }

    private SubmitFindingResult Reject(string reason, SubmitFindingArgs? args)
    {
        string title = args?.Title is { Length: > 0 } t ? t : "(sin título)";
        RejectedPayloads.Add($"{reason} · payload: {title}");
        RejectionReasons.Add(reason);
        Counters.Rejected++;
        PassRejected++;
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
