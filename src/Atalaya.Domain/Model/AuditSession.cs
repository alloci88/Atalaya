using System.Text.Json.Serialization;
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
/// auditoría es una unidad barrida entera. Esto es el desglose que lo hace comprobable.
/// <para>
/// «Barrida» no es «sin defectos» (F12 §E): es que el auditor no saca más de esta unidad con este
/// criterio. Dos pasadas secas seguidas son la señal de que ha convergido, no un certificado.
/// </para>
/// </summary>
/// <param name="Dry">
/// La pasada quedó SECA: 0 hallazgos nuevos y ningún veredicto distinto de «presente». DOS secas
/// seguidas son la condición de parada del barrido (F12 §E); una sola no lo es.
/// </param>
/// <param name="Summary">
/// La declaración de cobertura del auditor en esta pasada. Sabemos que es una afirmación y no
/// una prueba (2026-08-25: declaró revisar ConvertToDetId/ConvertToSeq y la pasada siguiente
/// encontró tres defectos ahí). Se conserva porque cuesta cero y, comparada entre pasadas,
/// enseña qué zonas revisita el modelo.
/// </param>
/// <param name="Disputed">
/// Veredictos «no es defecto» de esta pasada (F12 §H.2). Se contaban en la sesión y no en la
/// pasada, así que la sesión en vivo no podía decirlos mientras pasaban — que es justo cuando
/// importan.
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
    int LocationsAdded = 0,
    int Disputed = 0,
    int VariantsRejected = 0,
    int VariantsInsisted = 0);

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

    /// <summary>
    /// Hallazgos rebotados en la puerta por parecerse a uno que ya existía (F24): misma unidad,
    /// misma regla, mismo símbolo y a cinco líneas o menos. Es un subconjunto de
    /// <see cref="Rejected"/> —la app devolvió un error tipado y no guardó nada— contado aparte
    /// porque su causa no es un payload malformado sino un defecto ya reportado con otras palabras,
    /// y el remedio es otro.
    /// <para>
    /// Es el número que dice si el contrato del prompt está funcionando: si baja solo, el auditor ha
    /// dejado de reformular; si sube, la variante se está reportando igual y se está pagando.
    /// </para>
    /// </summary>
    public int VariantsRejected { get; set; }

    /// <summary>
    /// Variantes que el auditor reenvió con <c>distinctFrom</c> sosteniendo que eran otro defecto, y
    /// que por eso entraron (F24). No son rechazos: están guardadas y cuentan como
    /// <see cref="New"/>. Se cuentan aparte porque son el coste declarado del filtro —una llamada de
    /// más— y porque el informe las marca como posible duplicado para que decida una persona.
    /// </summary>
    public int VariantsInsisted { get; set; }
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

    /// <summary>
    /// Llamadas al modelo de toda la sesión. Es un dato PRIMARIO —lo cuenta el proveedor, no se
    /// deriva de nada— y va aquí para que un informe pueda decirlo sin tener que reconstruirlo del
    /// desglose por unidad, que en una sesión de arreglo o de verificación no existe.
    /// </summary>
    public int Calls { get; set; }

    public void Add(long input, long output, decimal? cost)
        => Add(input, output, 0, 0, cost);

    public void Add(long input, long output, long cacheRead, long cacheWrite, decimal? cost, int calls = 0)
    {
        InputTokens += input;
        OutputTokens += output;
        CacheReadTokens += cacheRead;
        CacheWriteTokens += cacheWrite;
        Calls += calls;
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

/// <summary>
/// De qué está hecho UN prompt de unidad, bloque a bloque, en tokens estimados (F18 §1).
/// <para>
/// <b>Por qué existe.</b> Hasta F18 se sabía cuánto costaba una sesión y nada más. La pregunta que
/// no se podía contestar es la única que sirve para decidir: <i>¿qué parte de lo que se paga es el
/// código que se está auditando, y qué parte es andamiaje?</i> En la línea base de F18 —una clase
/// de 40 líneas— el código eran ~600 tokens dentro de ~28.500. Sin este desglose eso hay que
/// calcularlo a mano leyendo un informe.
/// </para>
/// <para>
/// <b>No pretende exactitud al token</b> y no la necesita: se estima con
/// <c>PromptTokens.Estimate</c>, la misma regla que gobierna el presupuesto de directivas, para
/// que el panel y el prompt no digan cosas distintas del mismo texto. Lo que se decide con esto
/// —«el andamiaje es el 97 %»— no cambia porque la cuenta se desvíe un 20 %.
/// </para>
/// <para>
/// <b>El corte estable/variable es el de la caché.</b> <see cref="Estable"/> es exactamente el
/// prefijo que no cambia entre unidades ni entre pasadas de una misma sesión, y
/// <see cref="Variable"/> lo que sí. Ese corte no es decorativo: es el que decide si la caché del
/// proveedor sirve para algo (F18 §2).
/// </para>
/// </summary>
/// <param name="Reglas">Las reglas del auditor y la línea de MODO: constantes del programa.</param>
/// <param name="Rubrica">La rúbrica de severidad, citada del fichero versionado.</param>
/// <param name="Catalogo">Los pilares del catálogo, las áreas de criterio y las notas del stack.</param>
/// <param name="Tematica">El bloque de enfoque del ciclo temático. 0 en un ciclo General.</param>
/// <param name="Directivas">Las convenciones del proyecto, ya recortadas a su presupuesto (F7).</param>
/// <param name="Patrones">Los tipos de problema silenciados de la aplicación (F5.12).</param>
/// <param name="Existentes">Los hallazgos ya conocidos de la unidad, los de su temática y los de otras.</param>
/// <param name="Unidad">El código de la unidad. <b>Es lo único que se está auditando.</b></param>
public sealed record PromptComposition(
    int Reglas = 0,
    int Rubrica = 0,
    int Catalogo = 0,
    int Tematica = 0,
    int Directivas = 0,
    int Patrones = 0,
    int Existentes = 0,
    int Unidad = 0)
{
    /// <summary>El prefijo que NO cambia entre unidades ni entre pasadas: lo cacheable.</summary>
    public int Estable => Reglas + Rubrica + Catalogo + Tematica + Directivas + Patrones;

    /// <summary>Lo que cambia con la unidad: sus hallazgos conocidos y su código.</summary>
    public int Variable => Existentes + Unidad;

    public int Total => Estable + Variable;

    /// <summary>Todo lo que no es el código auditado: el andamiaje que pone Atalaya.</summary>
    public int Andamiaje => Total - Unidad;

    public static PromptComposition operator +(PromptComposition a, PromptComposition b)
        => new(
            a.Reglas + b.Reglas,
            a.Rubrica + b.Rubrica,
            a.Catalogo + b.Catalogo,
            a.Tematica + b.Tematica,
            a.Directivas + b.Directivas,
            a.Patrones + b.Patrones,
            a.Existentes + b.Existentes,
            a.Unidad + b.Unidad);

    /// <summary>El mismo desglose multiplicado por N. Sirve para pesar un prompt por sus llamadas.</summary>
    public PromptComposition Times(int n)
        => new(Reglas * n, Rubrica * n, Catalogo * n, Tematica * n,
               Directivas * n, Patrones * n, Existentes * n, Unidad * n);
}

/// <summary>
/// El consumo de UNA pasada del barrido (F18 §1). El desglose por unidad existía desde Hito 1a,
/// pero una unidad son N pasadas y cada una manda su propio prompt: sin este nivel no se puede
/// saber si el gasto está en abrir la unidad o en insistir sobre ella.
/// </summary>
/// <param name="Composition">De qué estaba hecho el prompt de esta pasada. Null en lo legado.</param>
/// <param name="DurationMs">Lo que tardó la pasada, de pared. 0 cuando no se midió.</param>
public sealed record PassUsage(
    int Pass,
    int Calls = 0,
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheWriteTokens = 0,
    PromptComposition? Composition = null,
    long DurationMs = 0);

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

    /// <summary>
    /// El consumo pasada a pasada (F18 §1). Vacía en las sesiones anteriores a F18: el dato no
    /// existía y no se inventa a posteriori repartiendo el total de la unidad entre sus pasadas.
    /// </summary>
    public List<PassUsage> Passes { get; set; } = new();

    /// <summary>Lo que tardó la unidad entera, de pared. 0 en lo legado.</summary>
    public long DurationMs { get; set; }
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
    /// Con qué proveedor se auditó (F14): <c>copilot</c>, <c>claude-code</c>. Se guarda junto al
    /// modelo porque un id de modelo no dice de quién es, y porque el COSTE de esta sesión está en
    /// la unidad de ESTE proveedor —peticiones premium en uno, dólares de tarifa de lista en el
    /// otro— y sumarlos daría una cifra sin significado. Métricas lo usa para no mezclarlos.
    /// <para>
    /// Null en toda sesión anterior a F14, y eso se lee como Copilot: era el único que había.
    /// </para>
    /// </summary>
    public string? Provider { get; set; }

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

    /// <summary>
    /// La temática del ciclo en que corrió la sesión (F17): con qué lupa se auditó. Va en la
    /// sesión y en su informe por la misma razón que el tope de pasadas: sin ella, «esta unidad
    /// salió limpia» no se puede interpretar dentro de un mes. General en todo lo anterior a F17.
    /// </summary>
    [JsonPropertyName("tematica")]
    public AuditTheme Theme { get; set; } = AuditTheme.General;

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
