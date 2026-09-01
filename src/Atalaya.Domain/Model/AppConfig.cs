namespace Atalaya.Domain.Model;

/// <summary>Detected/declared technology stack of an audited app (§4).</summary>
public enum TechStack
{
    Unknown,
    DotNet,
    JavaScript,
    TypeScript,
    Python,
    Go,
    Java,
    Rust,
    CCpp,
}

/// <summary>
/// Per-app thresholds (§4), persisted in app.json y COMPARTIDOS con todo el equipo.
/// <para>
/// <b>La regla (F13):</b> lo que escribe estado compartido se gobierna con ajuste compartido; lo
/// personal solo gobierna lo local. El umbral de «unidad grande» clasifica el inventario y crea
/// hallazgos que viven en el hub, así que es <b>política de la aplicación</b> y vive aquí: una
/// sola verdad para todo el equipo, versionada en git — el commit del hub dice quién la cambió y
/// cuándo. El tope de pasadas y el modelo, que solo gastan la cuota de quien lanza la sesión, no
/// están aquí y no deben estarlo (D-097).
/// </para>
/// </summary>
public sealed class Thresholds
{
    /// <summary>
    /// A partir de cuántas LOC (o de cuántos caracteres, <see cref="LargeUnitChars"/>) una unidad
    /// es «grande» (§4). Política de la aplicación: la editan «Umbrales · Gestionar» en el panel
    /// del Inventario, y la lee quien clasifica —escaneo, re-escaneo, siembra y reinicio de ciclo—
    /// en el momento de clasificar.
    /// </summary>
    public int LargeUnitLoc { get; set; } = 1500;

    /// <summary>El mismo umbral por peso: una unidad corta pero enorme tampoco cabe de una vez.</summary>
    public int LargeUnitChars { get; set; } = 60_000;

    /// <summary>
    /// TTL por defecto de un claim, en minutos (§2). Lo aplica <c>SessionCoordinator</c> al
    /// publicarlos: es propiedad de la app —cuánto tarda el equipo en dar por muerta una sesión
    /// ajena— y por eso sí es compartida.
    /// </summary>
    public int ClaimTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Hard ceiling on in+out tokens spent auditing a single unit (F3 Hito 1c). When exceeded
    /// the app aborts THAT unit (verdict <c>presupuesto-superado</c>, sibling of <c>grande</c>)
    /// and continues with the next one, so a run-away agent loop can never spend without a cap.
    /// Default 300 000, chosen against the pilot baseline (37 turns / 1,26 M input on one class):
    /// well above a healthy batched run, well below a pathological one.
    /// </summary>
    public long MaxTokensPerUnit { get; set; } = 300_000;

    /// <summary>
    /// A partir de cuántas unidades seleccionadas el lanzamiento pide confirmación (F5.6 §4).
    /// Por debajo se lanza directo: el coste de un clic de más solo se justifica cuando el gasto
    /// es relevante, y una tanda de dos unidades no lo es. Por defecto 3.
    /// </summary>
    public int ConfirmLaunchUnits { get; set; } = 3;

    /// <summary>
    /// Tope de tokens que las directivas del proyecto pueden ocupar en UN prompt (F7 §2).
    /// <para>
    /// Sin techo no hay funcionalidad: una colección de skills puede pesar más que el código que
    /// se está auditando, y un prompt que crece sin límite no falla con un error — falla gastando.
    /// Por defecto 8.000, que da de sobra para un AGENTS.md, un puñado de ADRs y las reglas de la
    /// casa, y sigue siendo pequeño frente al presupuesto por unidad
    /// (<see cref="MaxTokensPerUnit"/>).
    /// </para>
    /// <para>
    /// <b>0 apaga las directivas</b> en esta aplicación: no viaja ninguna y no se escribe la
    /// sección. Es el interruptor de quien no las quiera sin tener que desactivarlas una a una.
    /// </para>
    /// </summary>
    public int DirectiveTokenBudget { get; set; } = 8_000;
}

/// <summary>
/// App configuration (§2), stored as <c>apps/{appSlug}/app.json</c>. Note: the local clone
/// path is per-machine and lives in machines.json, NEVER here (§4).
/// </summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    public required string Slug { get; set; }

    public required string Name { get; set; }

    public required string RepoUrl { get; set; }

    public TechStack Stack { get; set; } = TechStack.Unknown;

    public Thresholds Thresholds { get; set; } = new();

    /// <summary>Glob-ish exclusion patterns, editable per app (§4).</summary>
    public List<string> Exclusions { get; set; } = new();

    /// <summary>Courtesy ESTADO.md export into the audited repo (§7).</summary>
    public bool ExportStatusMd { get; set; }

    public int CurrentCycle { get; set; } = 1;

    /// <summary>
    /// Per-pillar display-id counters (OPT/MEJ/BUG). Advanced only on successful push (§2),
    /// so a concurrent collision only renumbers an as-yet-unpublished alias.
    /// </summary>
    public Dictionary<string, int> DisplayIdCounters { get; set; } = new();
}
