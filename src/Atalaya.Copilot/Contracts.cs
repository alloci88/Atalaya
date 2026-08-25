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

    Unknown,
}

/// <summary>Whether the agent can run, with a human-readable reason (§6.1 help screen).</summary>
public sealed record AgentReadiness(bool Ready, string Message, AgentProblem Problem = AgentProblem.None);

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
/// Thrown when a Copilot operation fails because the CLI is not authenticated (§6.1). Carries the
/// help text so the UI can show "ejecuta `copilot` y autentícate" instead of a raw SDK error.
/// </summary>
public sealed class CopilotAuthenticationException : Exception
{
    public CopilotAuthenticationException(string message, Exception? inner = null) : base(message, inner) { }
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
public sealed record AuditUnitRequest(
    string UnitPath,
    string UnitContent,
    string Prompt,
    TechStack Stack,
    AuditMode Mode,
    IReadOnlyList<ExistingFinding> Existing);

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

/// <summary>A verify target (§5.4): a finding to re-check, re-anchored by snippet.</summary>
public sealed record VerifyTarget(string FindingUlid, string Path, int Line, string? Snippet, string Title, string Description);

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

    void UnitDone(string unitPath, string summary);

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
}
