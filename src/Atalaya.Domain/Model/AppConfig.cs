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
/// Los umbrales que la aplicación MIDE por su cuenta, sin preguntarle a nadie: cuándo una unidad
/// es demasiado grande para auditarla de una vez, y cuándo un hallazgo confirmado se ha quedado
/// viejo (§4, §8).
/// <para>
/// <b>Viven en <c>settings.json</c>, no en <c>app.json</c></b> (BUGFIX-AJUSTES). Son la misma clase
/// de preferencia que el tope de pasadas (D-097): quien audita decide con qué grano trabaja, y
/// subir el umbral desde una máquina no puede imponérselo al resto del equipo. Y por la misma
/// razón que allí, el campo <b>no se queda</b> en <see cref="Thresholds"/> «por compatibilidad»: un
/// valor que ya nadie lee, guardado junto a los que sí, es la invitación a leer el equivocado — que
/// es exactamente lo que había pasado.
/// </para>
/// </summary>
public sealed class MeasureThresholds
{
    /// <summary>A unit above this LOC (or <see cref="LargeUnitChars"/>) is "grande" (§4).</summary>
    public int LargeUnitLoc { get; set; } = 1500;

    public int LargeUnitChars { get; set; } = 60_000;

    /// <summary>Days since last confirmation before the freshness semaphore warns (§8, V3).</summary>
    public int FreshnessDays { get; set; } = 60;
}

/// <summary>
/// Per-app thresholds (§4), persisted in app.json y COMPARTIDOS con todo el equipo. Lo que es
/// preferencia de quien opera —el umbral de unidad grande, la frescura, el tope de pasadas— no
/// vive aquí: ver <see cref="MeasureThresholds"/> y D-097.
/// </summary>
public sealed class Thresholds
{
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
