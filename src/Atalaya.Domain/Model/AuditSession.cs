using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>Per-unit verdict recorded in a session (§2).</summary>
/// <param name="Verdict"><c>auditada</c> | <c>incompleta</c> | <c>presupuesto-superado</c> | <c>no-localizado</c>.</param>
/// <param name="MissingVerdicts">
/// F4: hallazgos existentes de la unidad sobre los que el auditor NO se pronunció. &gt; 0 significa
/// unidad incompleta: esos hallazgos quedaron intactos (nada se resuelve por omisión).
/// </param>
public sealed record UnitVerdictRecord(
    string Unit,
    string Module,
    string Verdict,
    string? Summary,
    int RejectedPayloads = 0,
    string? DominantRejectionReason = null,
    int MissingVerdicts = 0,
    List<UnitPassRecord>? Passes = null,
    bool CoverageIncomplete = false);

/// <summary>
/// Una pasada del barrido de una unidad (F4.1). Las pasadas son internas: para el usuario una
/// auditoría es una unidad completa. Esto es el desglose que lo hace comprobable.
/// </summary>
/// <param name="Dry">
/// La pasada quedó SECA: 0 hallazgos nuevos y ningún veredicto distinto de «presente». Es la
/// condición de parada del barrido.
/// </param>
/// <param name="Summary">
/// La declaración de cobertura del auditor en esta pasada. Sabemos que es una afirmación y no
/// una prueba (2026-08-25: declaró revisar ConvertToDetId/ConvertToSeq y la pasada siguiente
/// encontró tres defectos ahí). Se conserva porque cuesta cero y, comparada entre pasadas,
/// enseña qué zonas revisita el modelo.
/// </param>
public sealed record UnitPassRecord(
    int Index,
    int New,
    int Confirmed,
    int Resolved,
    int NonVerifiable,
    int Rejected,
    bool Dry,
    string? Summary,
    int LocationsAdded = 0);

/// <summary>Session tallies (§2).</summary>
public sealed class SessionCounters
{
    public int New { get; set; }
    public int Confirmed { get; set; }
    public int Resolved { get; set; }
    public int SilencedRespected { get; set; }

    /// <summary>
    /// Veredictos <c>no-verificable</c> del auditor (F4): el hallazgo sigue activo pero marcado
    /// <c>needsReview</c>. Se cuenta aparte para que nunca se confunda con "resuelto".
    /// </summary>
    public int NoVerificables { get; set; }

    /// <summary>
    /// Ubicaciones añadidas a hallazgos existentes (F4.1). Un defecto sistémico es UN hallazgo con
    /// N ubicaciones; esto mide cuánta de esa cobertura aportó la sesión.
    /// </summary>
    public int LocationsAdded { get; set; }

    /// <summary>
    /// Total payloads the toolbox validated and rebotó (F3.1 Bloque 0). Uno visible aquí evita
    /// que un "todo a 0" quede sin explicación: si <c>Rejected &gt; 0</c> el operador sabe que la
    /// causa está en los payloads del agente, no en la ausencia de hallazgos.
    /// </summary>
    public int Rejected { get; set; }
}

/// <summary>Token/cost totals for a session (§6.3).</summary>
public sealed class UsageTotals
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Cached input tokens the SDK reports as reused between turns, when available.</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Input tokens the SDK reports as written to prompt cache, when available.</summary>
    public long CacheWriteTokens { get; set; }

    public decimal? Cost { get; set; }
    public string? Currency { get; set; }

    public void Add(long input, long output, decimal? cost)
        => Add(input, output, 0, 0, cost);

    public void Add(long input, long output, long cacheRead, long cacheWrite, decimal? cost)
    {
        InputTokens += input;
        OutputTokens += output;
        CacheReadTokens += cacheRead;
        CacheWriteTokens += cacheWrite;
        if (cost is not null)
        {
            Cost = (Cost ?? 0m) + cost.Value;
        }
    }
}

/// <summary>
/// One SDK usage event captured verbatim (Hito 1a): tells us how many turns the agent needed for
/// a single unit and where the tokens actually went, so optimisation (cache, batching, brief
/// trimming, per-unit budget) can be driven by evidence instead of guesswork.
/// </summary>
public sealed record CallSample(
    int Index,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal? Cost,
    string? Model);

/// <summary>Per-unit token/cost breakdown (Hito 1a).</summary>
public sealed class UnitUsageBreakdown
{
    public required string Unit { get; set; }

    /// <summary>Estimated tokens of the initial prompt (brief + unit + extras). Rough: chars/4.</summary>
    public int PromptTokensEstimate { get; set; }

    /// <summary>Number of tool calls the agent issued for this unit.</summary>
    public int ToolCalls { get; set; }

    /// <summary>Number of model calls (SDK usage events) observed for this unit.</summary>
    public int Calls { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public decimal? Cost { get; set; }

    public List<CallSample> Samples { get; set; } = new();
}

/// <summary>
/// An immutable, append-only audit-session event (§2), stored as <c>sessions/{ulid}.json</c>.
/// </summary>
public sealed class AuditSession
{
    public int SchemaVersion { get; set; } = 1;

    public Ulid Id { get; init; }

    public required string AppSlug { get; set; }

    public AuditMode Mode { get; set; }

    public required string By { get; set; }

    public required string Machine { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>Commit of the audited clone.</summary>
    public string? Commit { get; set; }

    public string? Model { get; set; }

    public int CycleN { get; set; }

    public List<UnitVerdictRecord> Units { get; set; } = new();

    public SessionCounters Counters { get; set; } = new();

    public UsageTotals Usage { get; set; } = new();

    /// <summary>Per-unit token/cost breakdown (Hito 1a). Empty for legacy sessions.</summary>
    public List<UnitUsageBreakdown> UsageBreakdown { get; set; } = new();

    /// <summary>Free-text notes, e.g. "no signature extractor available for stack Go".</summary>
    public List<string> Notes { get; set; } = new();
}
