using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Agents;

/// <summary>
/// The <c>submit_finding</c> payload as the auditor delivers it (§6.2). All strings.
/// <para>
/// F4: <c>submit_finding(s)</c> queda SOLO para hallazgos genuinamente nuevos. Si el problema ya
/// figura en la lista de existentes de la unidad, el auditor debe referenciarlo por ULID en
/// <c>report_verdicts</c>, no re-reportarlo aquí.
/// </para>
/// <para>
/// <b>Tag</b> is intentionally NOT part of this payload (F3.1 Bloque 0): it is derivable from
/// <c>RuleId</c> (<c>criterio.*</c> → Criterio, resto → Checklist), y pedirla al modelo generaba
/// rechazos por variantes inventadas (pilot 2026-08-24: 25 rechazos por <c>tag</c> inválido en una
/// sola unidad). Menos superficie de tool, menos tokens, cero oportunidad de que el modelo la
/// rellene mal. La ingestión sigue tolerando payloads legados que la traigan (se ignora).
/// </para>
/// </summary>
public sealed record SubmitFindingArgs(
    string RuleId,
    string Pillar,
    string Severity,
    string Title,
    string Description,
    string Impact,
    string Recommendation,
    SubmitLocation[] Locations,
    string? Symbol);

public sealed record SubmitLocation(string Path, int Line, string? Snippet);

/// <summary>
/// Payload de <c>add_locations</c> (F4.1): extiende un hallazgo YA existente con ubicaciones
/// nuevas dentro de la misma unidad.
/// <para>
/// Es la pieza de vocabulario que faltaba. Sin ella, la única forma que tenía el auditor de decir
/// "este mismo defecto también pasa en la línea 105" era reportar OTRO hallazgo — y por eso un
/// defecto sistémico se fragmentaba en uno por miembro y el barrido no convergía nunca
/// (2026-08-25: 5 hallazgos distintos para "no valida argumentos nulos"). Ver D-090.
/// </para>
/// </summary>
public sealed record AddLocationsArgs(string FindingId, SubmitLocation[] Locations);

/// <summary>Resultado de <c>add_locations</c>: cuántas ubicaciones nuevas se añadieron.</summary>
public sealed record AddLocationsResult(bool Accepted, int Added = 0, string? Error = null);

/// <summary>Result returned to the auditor from <c>submit_finding</c> (§6.2).</summary>
public sealed record SubmitFindingResult(bool Accepted, string? DuplicateOf = null, string? Error = null);

/// <summary>
/// Batched variant of <see cref="SubmitFindingResult"/> for <c>submit_findings</c> (F3 Hito 1c):
/// per-item outcome so the auditor knows exactly which ones the app persisted and which it
/// rejected, without paying an extra turn per finding.
/// </summary>
public sealed record SubmitFindingsResult(IReadOnlyList<SubmitFindingResult> Results);

/// <summary>
/// Per-call token/cost sample reported by the provider (§6.3), isolated from any provider SDK type.
/// </summary>
/// <param name="CostUnit">
/// En qué unidad está <paramref name="Cost"/>, y por eso importa (F14). Copilot cuenta peticiones
/// premium; Claude Code informa dólares de <b>tarifa de lista</b> que su suscripción no factura por
/// llamada. Son dos magnitudes distintas y sumarlas daría un número que no significa nada. Sin este
/// campo nada lo impediría. Null = el proveedor no declaró unidad.
/// </param>
/// <param name="Calls">
/// Cuántas llamadas al modelo cuenta esta muestra. Normalmente <b>una</b> — de ahí el valor por
/// defecto—, y <b>cero</b> cuando la muestra es un AJUSTE y no una llamada nueva.
/// <para>
/// Existe porque «llamadas» se contaba como «cuántas muestras han llegado», y en cuanto un
/// proveedor manda una muestra que corrige a las anteriores —el CLI de Claude Code lo hace al
/// cerrar cada turno, ver <c>ClaudeStreamReader</c>— esa cuenta empieza a inventarse una llamada
/// de más por turno. Un número que se enseña al usuario no puede depender de cuántos mensajes
/// hizo falta para decirlo.
/// </para>
/// </param>
/// <param name="ReasoningTokens">
/// Cuántos de los <paramref name="OutputTokens"/> fueron RAZONAMIENTO, cuando el proveedor lo
/// desglosa (Claude Code lo publica como <c>thinking_tokens</c>; el SDK de Copilot, como
/// <c>ReasoningTokens</c>). Es un <b>diagnóstico</b> y no entra en ninguna suma de coste: la salida
/// ya está contada entera en <paramref name="OutputTokens"/>. Existe porque lo que la conversación
/// vuelve a escribir en caché en la vuelta siguiente es, sobre todo, esto (F21 §3), y sin el número
/// esa frase no se puede sostener. 0 = el proveedor no lo dice.
/// </param>
/// <param name="Reconciliation">
/// La muestra es el <b>CUADRE del final</b>, no consumo de una llamada concreta: la diferencia
/// entre lo que el proveedor declara haber gastado en total y lo que ya se había reportado sobre la
/// marcha. Se marca porque desde F21 hay dos muestras por llamada —el anticipo y el definitivo— y
/// las tres llevan <c>Calls = 0</c>; sin distinguirlas, un desglose por llamada le carga a la
/// última todo lo que el proveedor gastó por su cuenta (el modelo auxiliar del CLI), que no es de
/// ninguna llamada del auditor. No cambia ninguna suma: lo que agrega, agrega igual.
/// </param>
public sealed record UsageSample(
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Model,
    long CacheReadTokens = 0,
    long CacheWriteTokens = 0,
    string? CostUnit = null,
    int Calls = 1,
    bool Reconciliation = false,
    long ReasoningTokens = 0);

/// <summary>
/// Un hallazgo YA EXISTENTE de la unidad, tal y como se le presenta al auditor (F4). Son pocos
/// por unidad, así que van íntegros en el prompt: es lo que permite que la identidad la decida
/// el LLM en el momento de auditar en vez de un hash calculado a posteriori.
/// </summary>
/// <param name="FindingId">El ULID. Es el identificador que el auditor DEBE devolver.</param>
/// <param name="DisplayId">Alias legible (BUG-0042) si lo tiene; solo contexto.</param>
/// <param name="State">Estado visible: <c>activo</c> o <c>silenciado</c>.</param>
/// <param name="Theme">
/// La temática con la que se detectó (F17), escrita. Null en lo que no la distingue (el
/// verificador, los dobles de prueba); el prompt de un ciclo temático la enseña para que el
/// auditor vea por qué un hallazgo está en la lista de «no los juzgues».
/// </param>
public sealed record ExistingFinding(
    string FindingId,
    string? DisplayId,
    string Title,
    string Severity,
    string Location,
    string State,
    string? Theme = null);

/// <summary>What the app hands the auditor to audit one unit (§5.1.3).</summary>
/// <param name="Patterns">
/// Los tipos de problema silenciados en esta app (F5.12), ya escritos en <paramref name="Prompt"/>.
/// Viajan también aquí porque el agente falso los necesita para poder simular una supresión, que es
/// como se prueba el circuito entero sin asiento de ningún proveedor.
/// </param>
/// <param name="StablePrefix">
/// El tramo del <paramref name="Prompt"/> que es IDÉNTICO en todas las unidades y en todas las
/// pasadas de la sesión (F18 §2): reglas, rúbrica, catálogo, temática, directivas y patrones.
/// <para>
/// Viaja aparte para que un proveedor que sepa marcar un prefijo cacheable lo marque, y no como
/// una segunda versión del prompt: <b>la app garantiza que
/// <c>Prompt == StablePrefix + UnitPart</c></b>, byte a byte. Quien no sepa partirlo manda
/// <paramref name="Prompt"/> y no se entera de nada. Null en las llamadas que no lo declaran (los
/// dobles de prueba), y entonces solo hay prompt.
/// </para>
/// </param>
/// <param name="UnitPart">Lo que cambia con la unidad: sus hallazgos conocidos y su código.</param>
public sealed record AuditUnitRequest(
    string UnitPath,
    string UnitContent,
    string Prompt,
    TechStack Stack,
    AuditMode Mode,
    IReadOnlyList<ExistingFinding> Existing,
    PatternSilenceSet? Patterns = null,
    string? StablePrefix = null,
    string? UnitPart = null)
{
    /// <summary>
    /// El prompt se puede partir y las dos piezas cuadran con el entero. Se COMPRUEBA aquí y no se
    /// supone: partirlo mal mandaría al modelo un prompt distinto del que la app cree que mandó, y
    /// eso no da un error — da una auditoría peor sin causa visible.
    /// </summary>
    public bool CanSplit =>
        StablePrefix is { Length: > 0 } && UnitPart is not null && StablePrefix + UnitPart == Prompt;
}

/// <summary>
/// Lo que el auditor declara haberse callado por un patrón silenciado (F5.12), en
/// <c>unit_done</c>.
/// <para>
/// Es el ÚNICO canal por el que una supresión por patrón entra en los contadores: no hay filtro
/// programático que la detecte, y no lo habrá (anti-objetivo de F5.12). Si el modelo no lo declara,
/// la supresión existió pero no se contó — coste asumido y documentado, muy por debajo del de
/// mantener una taxonomía a mano.
/// </para>
/// </summary>
/// <param name="PatternId">El id corto del patrón tal y como aparece en el prompt (<c>P-2</c>).</param>
/// <param name="Count">Cuántas detecciones se calló por él en esta unidad.</param>
public sealed record SuppressedByPatternArgs(string PatternId, int Count);

/// <summary>
/// Un veredicto de reconciliación tal y como lo entrega el auditor (F4, tool
/// <c>report_verdicts</c>). Todo strings: la app parsea y valida.
/// </summary>
/// <param name="FindingId">ULID de un hallazgo de la lista de existentes de esta unidad.</param>
/// <param name="Verdict"><c>presente</c> | <c>arreglado</c> | <c>no-verificable</c>.</param>
/// <param name="Evidence">Por qué. Obligatoria: una resolución sin evidencia no es una resolución.</param>
public sealed record VerdictArgs(string FindingId, string Verdict, string Evidence);

/// <summary>Resultado por veredicto devuelto al auditor.</summary>
public sealed record ReportVerdictResult(bool Accepted, string? Error = null);

/// <summary>Resultado batched de <c>report_verdicts</c>, en el mismo orden que la entrada.</summary>
public sealed record ReportVerdictsResult(IReadOnlyList<ReportVerdictResult> Results);

/// <summary>De dónde sale el código que se le enseña al verificador (F6.6).</summary>
public enum VerifyBasis
{
    /// <summary>El ancla exacta sigue casando: el fragmento es literalmente el que se auditó.</summary>
    Anclado,

    /// <summary>
    /// El ancla exacta ya no está y el fragmento es el <b>código actual del miembro</b> que el
    /// hallazgo nombra. Es el caso normal de un arreglo, no el de un rastro perdido.
    /// </summary>
    Simbolo,

    /// <summary>Ni el ancla ni el símbolo: se juzga sobre la unidad entera, que sí cambió.</summary>
    Unidad,
}

/// <summary>A verify target (§5.4): a finding to re-check, re-anchored by snippet.</summary>
/// <param name="Basis">
/// Qué es <paramref name="Snippet"/> (F6.6). Sin este dato el prompt no puede distinguir «este es
/// el código que se auditó» de «el código que se auditó ya no existe y este es el que hay ahora»,
/// y son preguntas distintas: la segunda es la que se contesta «arreglado».
/// </param>
/// <param name="Member">
/// El miembro que se enseña: aquel al que se re-ancló, o —desde F12 §B— el que CONTIENE el ancla
/// cuando el ancla sigue casando. Null cuando no se pudo resolver ninguno y lo que va debajo es un
/// margen de líneas.
/// </param>
/// <param name="Recommendation">
/// Lo que el hallazgo pedía hacer. Es el criterio contra el que se juzga si el código de ahora lo
/// cumple; sin ella el verificador tiene que adivinar qué contaba como arreglo.
/// </param>
/// <param name="AnchoredSnippet">
/// La línea exacta que el ancla casó, cuando casó (F12 §B). <paramref name="Snippet"/> es el
/// símbolo entero, así que hace falta decir CUÁL de sus líneas es la que se auditó: sin ella el
/// verificador tendría el contexto pero no el punto.
/// </param>
public sealed record VerifyTarget(
    string FindingUlid,
    string Path,
    int Line,
    string? Snippet,
    string Title,
    string Description,
    VerifyBasis Basis = VerifyBasis.Anclado,
    string? Member = null,
    string Recommendation = "",
    string? AnchoredSnippet = null);

public sealed record VerifyRequest(string Prompt, IReadOnlyList<VerifyTarget> Targets);
