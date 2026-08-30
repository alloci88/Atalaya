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
    /// Veredictos «arreglado» degradados a «presente» por falta de evidencia de que la unidad
    /// cambiara (F5.1b). Nunca se suman a <see cref="Resolved"/>: son lo contrario de una
    /// resolución. Si es &gt; 0, el informe nombra cuáles y por qué.
    /// </summary>
    public int ResolutionsRefused { get; set; }

    /// <summary>
    /// Hallazgos marcados como disputados en esta sesión (F5.1b): el auditor sostiene que nunca
    /// fueron un defecto. No resuelven ni desactivan nada; esperan decisión humana.
    /// </summary>
    public int Disputed { get; set; }

    /// <summary>
    /// Detecciones que el AUDITOR declaró haber suprimido por corresponder a un patrón silenciado
    /// de esta aplicación (F5.12), sumadas de lo que reporta en <c>unit_done</c>.
    /// <para>
    /// No son rechazos ni hallazgos: el auditor hizo su trabajo y se calló lo que la app le pidió
    /// que se callara. Van por su propio contador para que el informe pueda decir «suprimidos por
    /// patrón: N» sin pintar el ⚠ de un payload malformado, y para que quien lea el informe vea
    /// qué le está costando cada patrón. Ninguna supresión es invisible: el detalle por patrón
    /// vive en <see cref="AuditSession.SuppressionsByPattern"/>.
    /// </para>
    /// </summary>
    public int SuppressedByPattern { get; set; }

    /// <summary>
    /// Total payloads the toolbox validated and rebotó (F3.1 Bloque 0). Uno visible aquí evita
    /// que un "todo a 0" quede sin explicación: si <c>Rejected &gt; 0</c> el operador sabe que la
    /// causa está en los payloads del agente, no en la ausencia de hallazgos.
    /// </summary>
    public int Rejected { get; set; }
}

/// <summary>
/// Cuánto suprimió UN patrón silenciado durante una sesión (F5.12). El ejemplar viaja junto al id
/// —y no solo el id— porque un informe tiene que seguir explicándose solo cuando el patrón ya se
/// haya des-silenciado o reescrito.
/// </summary>
/// <param name="PatternId">El id corto que el auditor citó (<c>P-3</c>).</param>
/// <param name="Exemplar">
/// La frase del patrón, o una nota diciendo que el id no correspondía a ninguno vivo: un id
/// inventado por el modelo se registra igual, porque un dato que desaparece es un dato sin causa.
/// </param>
public sealed record PatternSuppressionTally(string PatternId, string Exemplar, int Count);

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

    /// <summary>
    /// Qué provocó la sesión (F9 §6). <see cref="SessionTrigger.Manual"/> en todo lo anterior a F9 y
    /// en todo lo que se elige a mano: el dato se guarda, no se interpreta todavía.
    /// </summary>
    public SessionTrigger Trigger { get; set; } = SessionTrigger.Manual;

    public required string By { get; set; }

    public required string Machine { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>Commit of the audited clone.</summary>
    public string? Commit { get; set; }

    public string? Model { get; set; }

    /// <summary>
    /// Tope de pasadas del barrido vigente cuando se ejecutó la sesión (F5.1). Es un ajuste de la
    /// máquina que lanza la auditoría, así que sin registrarlo aquí no habría forma de saber, al
    /// leer una sesión antigua, si «cobertura posiblemente incompleta» significa «el modelo no
    /// convergió» o «el tope estaba en 1». 0 en sesiones anteriores a F5.1.
    /// </summary>
    public int MaxPassesPerUnit { get; set; }

    /// <summary>
    /// La sesión se detuvo antes de cubrir todas sus unidades (F5.1b). Se registra igual: los
    /// hallazgos se persisten en vivo, así que una parada sin registro dejaba el hub mutado sin
    /// traza de quién lo hizo. False en sesiones anteriores a F5.1b y en las completas.
    /// </summary>
    public bool Interrupted { get; set; }

    public int CycleN { get; set; }

    public List<UnitVerdictRecord> Units { get; set; } = new();

    public SessionCounters Counters { get; set; } = new();

    public UsageTotals Usage { get; set; } = new();

    /// <summary>Per-unit token/cost breakdown (Hito 1a). Empty for legacy sessions.</summary>
    public List<UnitUsageBreakdown> UsageBreakdown { get; set; } = new();

    /// <summary>
    /// Qué patrón silenciado suprimió cuánto en esta sesión (F5.12). Vacía cuando la app no tiene
    /// patrones o el auditor no citó ninguno. Es lo que convierte «suprimidos por patrón: 7» en un
    /// número con causa, que es la única forma de decidir si un patrón sigue teniendo sentido.
    /// </summary>
    public List<PatternSuppressionTally> SuppressionsByPattern { get; set; } = new();

    /// <summary>
    /// Qué directivas del proyecto viajaron en los prompts de esta sesión, con el hash de su
    /// contenido (F7 §3). Vacía cuando la app no tiene ninguna activada.
    /// <para>
    /// Es la trazabilidad de CON QUÉ CRITERIO se auditó. Las directivas viven en el repo de la
    /// aplicación y cambian con él, así que sin el hash una sesión de hace dos meses sería
    /// imposible de releer: se sabría que hubo convenciones, no cuáles. Incluye las truncadas y
    /// las omitidas por presupuesto, marcadas como tales — un informe que solo nombre lo que entró
    /// deja fuera justo lo que explicaría por qué el auditor no vio algo.
    /// </para>
    /// </summary>
    public List<DirectiveRecord> Directives { get; set; } = new();

    /// <summary>
    /// El hallazgo que arregló una sesión <see cref="AuditMode.Fix"/> (H9.1). Null en todo lo
    /// demás y en las sesiones fix anteriores a H9.1, que solo lo nombraban dentro del texto.
    /// Es lo que permite volver del informe de un arreglo a la ficha de su hallazgo.
    /// </summary>
    public string? FixFindingId { get; set; }

    /// <summary>El identificador legible del hallazgo arreglado («OPT-0002»). Ver <see cref="FixFindingId"/>.</summary>
    public string? FixFindingAlias { get; set; }

    /// <summary>Free-text notes, e.g. "no signature extractor available for stack Go".</summary>
    public List<string> Notes { get; set; } = new();
}
