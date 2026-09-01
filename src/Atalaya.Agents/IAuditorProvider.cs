namespace Atalaya.Agents;

/// <summary>
/// The tools the app exposes to the auditor for an audit (§6.2). The app validates and persists;
/// the auditor only reports. <see cref="ReadSignatures"/> is the only extra code access allowed.
/// </summary>
public interface IAuditToolbox
{
    SubmitFindingResult SubmitFinding(SubmitFindingArgs args);

    /// <summary>
    /// Batched variant (F3 Hito 1c): submit an array of findings in a single tool call. Reduces
    /// auditor turns dramatically when the model complies. Same validation as singular; per-item
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
/// <b>Quién razona.</b> La costura detrás de la que se sienta un auditor — GitHub Copilot, Claude
/// Code o el agente falso de los tests— sin que el resto de la aplicación se entere de cuál es
/// (§11, F14).
/// <para>
/// <b>Lo que NO está aquí es el punto.</b> Reconciliar, decidir veredictos, guardar la evidencia,
/// calcular la huella, escribir la sesión y redactar el informe son del pipeline, y el pipeline
/// vive por ENCIMA de esta interfaz. Un proveedor no elige qué es un duplicado ni qué se resuelve:
/// reporta por el toolbox y la aplicación juzga. Por eso añadir una casa nueva no puede cambiar
/// resultados — solo cambia quién los propone.
/// </para>
/// <para>
/// El pipeline entero se conduce por aquí, así que los tests lo ejercitan de punta a punta sin
/// asiento ni suscripción de nadie.
/// </para>
/// </summary>
public interface IAuditorProvider
{
    /// <summary>
    /// Identificador estable del proveedor (<c>copilot</c>, <c>claude-code</c>). Es lo que se
    /// GUARDA en la sesión, en el hallazgo y en el informe, así que no puede cambiar nunca: un
    /// nombre para mostrar se puede reescribir, un dato que ya está en el hub no.
    /// <para>
    /// <b>Tiene valor por defecto, y es el nombre del tipo.</b> Los dos proveedores de verdad lo
    /// declaran; lo que hay repartido por los tests son dobles minúsculos que existen para
    /// ejercitar UN camino (un agente que revienta, uno que se cuelga, uno que agota el
    /// presupuesto). Obligarlos a inventarse un identificador de proveedor no probaría nada y
    /// serían treinta copias de lo mismo esperando a quedarse desfasadas. El nombre del tipo dice
    /// la verdad —<c>AgenteQueRevienta</c>— y no se confunde con ninguna casa real.
    /// </para>
    /// </summary>
    string ProviderId => GetType().Name;

    /// <summary>Cómo se llama para una persona: «GitHub Copilot», «Claude Code».</summary>
    string ProviderName => ProviderId;

    /// <summary>
    /// Este proveedor es un EXTRA: no se le exige a nadie (F14, adenda).
    /// <para>
    /// Copilot es el requisito del equipo y el proveedor por defecto; Claude Code es opcional
    /// SIEMPRE. La diferencia no es cosmética, es una regla de producto: quien no lo tenga
    /// instalado <b>no ve ningún aviso, ninguna exigencia y ninguna merma</b>. La aplicación se
    /// comporta exactamente igual que antes de que existiera.
    /// </para>
    /// <para>
    /// Un proveedor opcional que no está en la máquina no es un fallo que reportar: es una
    /// capacidad que no se ha activado. Pintarlo en rojo convertiría en deuda de cada usuario algo
    /// que nadie le ha pedido.
    /// </para>
    /// </summary>
    bool IsOptional => false;

    /// <summary>
    /// ¿Está en esta máquina? Comprobación <b>barata</b>: sin red, sin credenciales y sin gastar
    /// cuota — un vistazo al PATH, o nada en el caso de quien viaja dentro de la aplicación.
    /// <para>
    /// Es distinta de <see cref="CheckAsync"/>, que además pregunta por la sesión iniciada y lanza
    /// un proceso. Ésta se puede llamar al pintar una pantalla; aquélla no.
    /// </para>
    /// </summary>
    bool IsPresent => true;

    /// <summary>Model in use, if known (for session records).</summary>
    string? ModelName { get; }

    /// <summary>Streamed assistant text, for the live session view (V5).</summary>
    event Action<string>? TextStreamed;

    /// <summary>Per-call usage, accumulated by the session (§6.3).</summary>
    event Action<UsageSample>? UsageReported;

    /// <summary>Verifies the provider can run; false means auth is needed (§6.1 help screen).</summary>
    Task<bool> EnsureReadyAsync(CancellationToken ct);

    /// <summary>Detailed readiness check (auth state + reason) for the Cuenta page.</summary>
    Task<AgentReadiness> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Los modelos que la cuenta puede usar, para poblar el selector de Ajustes (F5.1). Lanza si
    /// no se puede preguntar al proveedor (sin red, sin credencial): Ajustes lo captura y enseña el
    /// modelo configurado con un aviso, en vez de romperse o de inventar una lista.
    /// </summary>
    Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct);

    /// <summary>Audits one unit, reporting via <paramref name="toolbox"/> and ending on unit_done.</summary>
    Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct);

    /// <summary>Re-verifies findings, reporting verdicts via <paramref name="toolbox"/>.</summary>
    Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct);

    /// <summary>
    /// De qué se ha quejado ESTE proveedor (F14). Cada casa nombra sus fallos a su manera —Copilot
    /// clasifica errores del SDK, Claude Code lee el evento final de su CLI—, así que la traducción
    /// a la taxonomía común es responsabilidad de quien conoce el dialecto.
    /// <para>
    /// Existe porque el pipeline necesita clasificar fallos que él mismo no provocó (por ejemplo,
    /// que la lista de modelos no se pueda pedir) y no puede tener un <c>if</c> por proveedor: eso
    /// es exactamente lo que la interfaz viene a eliminar.
    /// </para>
    /// <para>
    /// El valor por defecto no adivina: si la excepción YA viene clasificada por un proveedor, la
    /// respeta; si no, la declara desconocida y enseña el texto crudo. Inventar una causa es
    /// exactamente el fallo que costó descubrir la cuota (BUGFIX-CUOTA, N-2).
    /// </para>
    /// </summary>
    AgentReadiness Diagnose(Exception ex)
        => ex is AuditorProviderException known
            ? new AgentReadiness(false, known.Message, known.Problem, known.Detail)
            : new AgentReadiness(false, ex.Message, AgentProblem.Unknown, ex.ToString());
}
