using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Copilot;

/// <summary>
/// The <c>submit_finding</c> payload as the agent delivers it (§6.2). All strings.
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

/// <summary>Result returned to the agent from <c>submit_finding</c> (§6.2).</summary>
public sealed record SubmitFindingResult(bool Accepted, string? DuplicateOf = null, string? Error = null);

/// <summary>
/// Batched variant of <see cref="SubmitFindingResult"/> for <c>submit_findings</c> (F3 Hito 1c):
/// per-item outcome so the agent knows exactly which ones the app persisted and which it rejected,
/// without paying an extra turn per finding.
/// </summary>
public sealed record SubmitFindingsResult(IReadOnlyList<SubmitFindingResult> Results);

/// <summary>Per-call token/cost sample from the SDK usage event (§6.3), isolated from the SDK types.</summary>
public sealed record UsageSample(
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Model,
    long CacheReadTokens = 0,
    long CacheWriteTokens = 0,
    string? CostUnit = null);

/// <summary>
/// Why the agent is not ready. Lets the UI show a *specific* remedy instead of one generic
/// "not authenticated" for three very different situations (F2.3 diagnostics).
/// </summary>
public enum AgentProblem
{
    None = 0,

    /// <summary>No usable credential: no account token and no CLI login either.</summary>
    NotAuthenticated,

    /// <summary>The token is valid but the account has no Copilot seat assigned.</summary>
    NoSeat,

    /// <summary>The credential was rejected (revoked / expired) — reconnect the account.</summary>
    TokenRejected,

    /// <summary>Could not reach GitHub.</summary>
    Offline,

    /// <summary>
    /// El modelo configurado no existe o la cuenta no puede usarlo (F5.15). No es un problema de
    /// credenciales ni de asiento: la sesión no arranca porque se le pidió al runtime un modelo que
    /// ya no sirve. Tiene remedio de un clic —elegir otro en Ajustes— y por eso se distingue.
    /// </summary>
    ModelUnavailable,

    /// <summary>
    /// La organización se ha quedado sin peticiones premium de Copilot (BUGFIX-CUOTA). No es un
    /// problema de la cuenta ni de la credencial: el asiento está, la licencia está, y lo que falta
    /// son peticiones. Tiene tipo propio porque tiene REMEDIO propio —esperar al reset, o bajar a un
    /// modelo con multiplicador menor—, y porque el remedio equivocado (reclamar un asiento que ya
    /// se tiene) hace perder el tiempo a dos personas.
    /// </summary>
    QuotaExhausted,

    Unknown,
}

/// <summary>Whether the agent can run, with a human-readable reason (§6.1 help screen).</summary>
/// <param name="Detail">
/// El error del proveedor tal cual —tipo, texto y Request ID—, para poder copiarlo y pegarlo
/// (BUGFIX-CUOTA). Va SEPARADO del mensaje: la frase es para decidir qué hacer, el crudo es para
/// que quien administre la organización pueda buscar la petición concreta. Vacío cuando no hay
/// excepción detrás, como en las comprobaciones que fallan por respuesta y no por error.
/// </param>
public sealed record AgentReadiness(
    bool Ready, string Message, AgentProblem Problem = AgentProblem.None, string? Detail = null);

/// <summary>
/// Un modelo disponible para la cuenta, tal y como lo lista el SDK (F5.1). Se pide siempre al
/// runtime (<c>CopilotClient.ListModelsAsync</c>): una lista escrita a mano caduca en cuanto
/// GitHub añade o retira un modelo, y el usuario acabaría eligiendo uno que su asiento no sirve.
/// </summary>
/// <param name="Id">Identificador que va a <c>SessionConfig.Model</c> (p. ej. <c>gpt-5</c>).</param>
/// <param name="Name">Nombre para mostrar; si el SDK no lo trae, cae al <paramref name="Id"/>.</param>
/// <param name="Multiplier">
/// Multiplicador de facturación relativo a la tarifa base, cuando el SDK lo publica
/// (<c>ModelInfo.Billing.Multiplier</c>). Null = el SDK no lo da; no se inventa.
/// </param>
public sealed record AgentModel(string Id, string Name, double? Multiplier = null);

/// <summary>
/// El proveedor ha rechazado la operación, y ya se sabe POR QUÉ (BUGFIX-CUOTA).
/// <para>
/// Existe porque «no se ha podido usar Copilot» tenía un solo tipo de excepción —la de
/// autenticación— y por debajo se colaban cuota, asiento, red y lo desconocido. Quien la captura
/// necesita las tres cosas que trae: el <see cref="Problem"/> para decidir qué ofrecer, el
/// <see cref="Exception.Message"/> para enseñarlo, y el <see cref="Detail"/> crudo para que se
/// pueda copiar.
/// </para>
/// </summary>
public class CopilotProviderException : Exception
{
    public CopilotProviderException(
        string message, AgentProblem problem, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Problem = problem;
        Detail = detail;
    }

    public AgentProblem Problem { get; }

    /// <summary>El error del proveedor tal cual (tipo + texto + Request ID). Copiable.</summary>
    public string? Detail { get; }

    /// <summary>Reintentar solo tiene sentido si el problema es transitorio. La cuota NO lo es.</summary>
    public bool IsRetryable => CopilotFailure.IsRetryable(Problem);
}

/// <summary>
/// Thrown when a Copilot operation fails because the CLI is not authenticated (§6.1). Carries the
/// help text so the UI can show "ejecuta `copilot` y autentícate" instead of a raw SDK error.
/// <para>
/// Sigue existiendo —y sigue siendo lo que se lanza en los casos de credencial— para que los
/// <c>catch</c> que ya la nombraban no cambien de sentido. Lo que ha cambiado es que ahora es UNA
/// de las hijas de <see cref="CopilotProviderException"/> y no el cajón de todo.
/// </para>
/// </summary>
public sealed class CopilotAuthenticationException : CopilotProviderException
{
    public CopilotAuthenticationException(string message, Exception? inner = null)
        : base(message, AgentProblem.NotAuthenticated, null, inner) { }

    public CopilotAuthenticationException(
        string message, AgentProblem problem, string? detail, Exception? inner = null)
        : base(message, problem, detail, inner) { }
}

/// <summary>
/// La sesión no se pudo CREAR porque el modelo pedido no está disponible para esta cuenta (F5.15).
/// <para>
/// Nace del parte del 2026-08-26: <c>session.create</c> falló con «Model gpt-5 is not available» y
/// la aplicación se quedó muda —ni error, ni botón de parar, ni forma de volver a la vista—. Tiene
/// tipo propio porque tiene REMEDIO propio: elegir otro modelo en Ajustes. Un error genérico no
/// puede ofrecer ese enlace, y sin el enlace el usuario no sabe que la cura está a dos clics.
/// </para>
/// </summary>
public sealed class CopilotModelUnavailableException : CopilotProviderException
{
    public CopilotModelUnavailableException(string? modelId, Exception? inner = null)
        : base(CopilotHelp.ModelUnavailable(modelId), AgentProblem.ModelUnavailable,
               CopilotFailure.Raw(inner), inner)
        => ModelId = modelId;

    /// <summary>El id que se pidió, para poder nombrarlo. Null si no se había configurado ninguno.</summary>
    public string? ModelId { get; }
}

/// <summary>Canonical help texts for the not-ready cases (§6.1, F2.3).</summary>
public static class CopilotHelp
{
    /// <summary>
    /// The normal path since F2: Copilot authenticates with the account token obtained by the
    /// in-app GitHub login, so the remedy is a click in Atalaya, not a console.
    /// </summary>
    public const string NoAccount =
        "Copilot no está autenticado. Ve a Cuenta y pulsa «Conectar con GitHub»: "
        + "el mismo login habilita el hub y tu asiento de Copilot.";

    /// <summary>The account token was accepted but the account has no Copilot seat.</summary>
    public const string NoSeat =
        "Tu cuenta no tiene asiento de Copilot asignado; pídelo al administrador de la organización "
        + "(github.com/settings/copilot). Esto NO es un problema de autenticación.";

    /// <summary>The credential was rejected (revoked, expirado o SSO caducado).</summary>
    public const string TokenRejected =
        "GitHub ha rechazado tus credenciales (revocadas o caducadas). Ve a Cuenta y vuelve a conectar.";

    /// <summary>
    /// El modelo configurado no sirve. Dice QUÉ pasó, POR QUÉ no es culpa de la red ni de la cuenta,
    /// y DÓNDE se arregla — las tres cosas que faltaban cuando el fallo era mudo.
    /// </summary>
    public static string ModelUnavailable(string? modelId)
        => string.IsNullOrWhiteSpace(modelId)
            ? "No se pudo iniciar la sesión: el runtime rechazó el modelo configurado. "
              + "Elige otro en Ajustes."
            : $"No se pudo iniciar: el modelo «{modelId}» no está disponible para tu cuenta. "
              + "Elige otro en Ajustes.";

    /// <summary>
    /// La organización se ha quedado sin peticiones premium (BUGFIX-CUOTA). Dice las tres cosas que
    /// faltaban: QUÉ pasa (no es tu cuenta), que en Atalaya NO hay nada que tocar, y las dos únicas
    /// salidas reales. El periodo solo se nombra cuando el propio error lo dice; la fecha exacta del
    /// reset no la trae, así que no se inventa.
    /// </summary>
    public static string QuotaExhausted(string? period)
        => "La organización ha agotado sus peticiones premium de Copilot"
        + (period is { Length: > 0 } ? $" (el proveedor la describe como cuota {period})" : string.Empty)
        + ". No es tu asiento ni tus credenciales, y no hay nada que arreglar en Atalaya: hay que "
        + "esperar a que se renueve la cuota, o auditar con un modelo de multiplicador menor si "
        + "vuestro plan lo permite. Atalaya no reintenta sola: reintentar contra una cuota agotada "
        + "gasta las peticiones del reset siguiente.";

    /// <summary>Sin red o con el servicio caído: es transitorio y se puede reintentar.</summary>
    public const string Offline =
        "No hay conexión con GitHub (o su servicio no responde). Comprueba la red o el proxy y "
        + "reintenta: este fallo es transitorio.";

    /// <summary>
    /// Lo que no se ha sabido clasificar. Enseña el error del proveedor ÍNTEGRO en vez de proponer
    /// una causa: un mensaje bonito con la causa equivocada manda a alguien a arreglar lo que no
    /// está roto, que es justo lo que pasó con la cuota (N-2).
    /// </summary>
    public static string Unknown(string raw)
        => "Copilot ha rechazado la operación y Atalaya no reconoce el motivo, así que no se lo "
        + "inventa. Éste es el error tal cual lo ha devuelto el proveedor"
        + (string.IsNullOrWhiteSpace(raw) ? "." : $": {raw}");

    /// <summary>
    /// Legacy fallback text: used only when there is no account token and Atalaya falls back to
    /// the credentials the `copilot` CLI stored on this machine (pre-F2 setup, kept working).
    /// </summary>
    public const string NotAuthenticated =
        "Copilot no está autenticado en esta máquina. Instala el CLI (npm install -g @github/copilot), "
        + "ejecuta `copilot` en una terminal, usa /login una vez con tu cuenta con asiento de Copilot, y reintenta.";
}

/// <summary>
/// Un hallazgo YA EXISTENTE de la unidad, tal y como se le presenta al auditor (F4). Son pocos
/// por unidad, así que van íntegros en el prompt: es lo que permite que la identidad la decida
/// el LLM en el momento de auditar en vez de un hash calculado a posteriori.
/// </summary>
/// <param name="FindingId">El ULID. Es el identificador que el auditor DEBE devolver.</param>
/// <param name="DisplayId">Alias legible (BUG-0042) si lo tiene; solo contexto.</param>
/// <param name="State">Estado visible: <c>activo</c> o <c>silenciado</c>.</param>
public sealed record ExistingFinding(
    string FindingId,
    string? DisplayId,
    string Title,
    string Severity,
    string Location,
    string State);

/// <summary>What the app hands the agent to audit one unit (§5.1.3).</summary>
/// <param name="Patterns">
/// Los tipos de problema silenciados en esta app (F5.12), ya escritos en <paramref name="Prompt"/>.
/// Viajan también aquí porque el agente falso los necesita para poder simular una supresión, que es
/// como se prueba el circuito entero sin un asiento de Copilot.
/// </param>
public sealed record AuditUnitRequest(
    string UnitPath,
    string UnitContent,
    string Prompt,
    TechStack Stack,
    AuditMode Mode,
    IReadOnlyList<ExistingFinding> Existing,
    PatternSilenceSet? Patterns = null);

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
/// Un veredicto de reconciliación tal y como lo entrega el agente (F4, tool
/// <c>report_verdicts</c>). Todo strings: la app parsea y valida.
/// </summary>
/// <param name="FindingId">ULID de un hallazgo de la lista de existentes de esta unidad.</param>
/// <param name="Verdict"><c>presente</c> | <c>arreglado</c> | <c>no-verificable</c>.</param>
/// <param name="Evidence">Por qué. Obligatoria: una resolución sin evidencia no es una resolución.</param>
public sealed record VerdictArgs(string FindingId, string Verdict, string Evidence);

/// <summary>Resultado por veredicto devuelto al agente.</summary>
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
/// <param name="Member">El miembro al que se re-ancló, cuando se re-ancló. Solo para nombrarlo.</param>
/// <param name="Recommendation">
/// Lo que el hallazgo pedía hacer. Es el criterio contra el que se juzga si el código de ahora lo
/// cumple; sin ella el verificador tiene que adivinar qué contaba como arreglo.
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
    string Recommendation = "");

public sealed record VerifyRequest(string Prompt, IReadOnlyList<VerifyTarget> Targets);

/// <summary>
/// The tools the app exposes to the agent for an audit (§6.2). The app validates and persists;
/// the agent only reports. <see cref="ReadSignatures"/> is the only extra code access allowed.
/// </summary>
public interface IAuditToolbox
{
    SubmitFindingResult SubmitFinding(SubmitFindingArgs args);

    /// <summary>
    /// Batched variant (F3 Hito 1c): submit an array of findings in a single tool call. Reduces
    /// agent turns dramatically when the model complies. Same validation as singular; per-item
    /// outcome returned in order.
    /// </summary>
    SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings);

    /// <summary>
    /// Reconciliación por el auditor (F4): el veredicto sobre CADA hallazgo existente listado en
    /// el prompt de la unidad. Es el ÚNICO camino por el que un hallazgo previo cambia de estado
    /// durante una auditoría — no existe la resolución implícita. Un <c>findingId</c> que no esté
    /// en la lista se rechaza con un error tipado y no toca nada.
    /// </summary>
    ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts);

    /// <summary>
    /// Extiende un hallazgo existente con ubicaciones nuevas (F4.1). El <paramref name="findingId"/>
    /// debe estar en la lista de existentes de la unidad o haberse creado en este mismo barrido, y
    /// las ubicaciones deben caer dentro de la unidad que se está auditando.
    /// </summary>
    AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations);

    /// <summary>
    /// Cierra la unidad. <paramref name="suppressedByPattern"/> es lo que el auditor declara
    /// haberse callado por los patrones silenciados de la app (F5.12); null o vacío significa que
    /// no se calló nada, que es lo normal cuando la app no tiene patrones.
    /// </summary>
    void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null);

    string ReadSignatures(string path);
}

/// <summary>The single verify tool (§6.2). Uses the ULID, never the mutable displayId.</summary>
public interface IVerifyToolbox
{
    void SubmitVerdict(string findingUlid, string verdict, string evidence);
}

/// <summary>
/// The audit agent abstraction (§11): the seam behind which either the real Copilot SDK or the
/// injectable fake sits. The whole session pipeline is driven through this, so tests exercise it
/// end-to-end without a Copilot seat.
/// </summary>
public interface ICopilotAgent
{
    /// <summary>Model in use, if known (for session records).</summary>
    string? ModelName { get; }

    /// <summary>Streamed assistant text, for the live session view (V5).</summary>
    event Action<string>? TextStreamed;

    /// <summary>Per-call usage, accumulated by the session (§6.3).</summary>
    event Action<UsageSample>? UsageReported;

    /// <summary>Verifies the agent can run; false means auth is needed (§6.1 help screen).</summary>
    Task<bool> EnsureReadyAsync(CancellationToken ct);

    /// <summary>Detailed readiness check (auth state + reason) for the "Comprobar Copilot" action.</summary>
    Task<AgentReadiness> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Los modelos que la cuenta puede usar, para poblar el selector de Ajustes (F5.1). Lanza si
    /// no se puede preguntar al runtime (sin red, sin credencial): Ajustes lo captura y enseña el
    /// modelo configurado con un aviso, en vez de romperse o de inventar una lista.
    /// </summary>
    Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct);

    /// <summary>Audits one unit, reporting via <paramref name="toolbox"/> and ending on unit_done.</summary>
    Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct);

    /// <summary>Re-verifies findings, reporting verdicts via <paramref name="toolbox"/>.</summary>
    Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct);

    /// <summary>
    /// Arregla UN hallazgo sobre el clon local, de forma interactiva (F6.9). A diferencia de
    /// auditar y verificar, aquí la sesión es una CONVERSACIÓN: dura varios turnos, el agente
    /// pregunta y el usuario puede dirigirla mientras corre.
    /// <para>
    /// La sesión se cierra cuando el agente llama a <c>fix_done</c> (tool terminal) o cuando
    /// <see cref="FixConversation.NextTurn"/> devuelve <c>null</c>. El agente edita SOLO por
    /// <c>apply_edit</c>: no tiene shell, ni git, ni red.
    /// </para>
    /// <para>
    /// <b>Tiene implementación por defecto, y lanza.</b> Los dos agentes de verdad —el del SDK y
    /// el falso— la implementan; lo que hay repartido por los tests son dobles minúsculos que
    /// existen para ejercitar UN camino de auditoría (un agente que revienta, uno que se cuelga,
    /// uno que agota el presupuesto). Obligarlos a llevar un <c>FixAsync</c> vacío no probaría
    /// nada y sería siete copias de lo mismo esperando a quedarse desfasadas. Lanzar dice la
    /// verdad: ese agente no sabe arreglar.
    /// </para>
    /// </summary>
    Task FixAsync(FixRequest request, FixConversation conversation, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} no implementa el arreglo asistido.");
}
