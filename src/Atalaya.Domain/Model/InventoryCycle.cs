using System.Text.Json.Serialization;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>A unit inside a cycle inventory (§2). Unit state lives here — there is no LOTES.md.</summary>
public sealed class InventoryUnit
{
    public required string Path { get; set; }

    public required string Module { get; set; }

    public int Loc { get; set; }

    public string? ContentHash { get; set; }

    public UnitState State { get; set; } = UnitState.Pendiente;

    /// <summary>The session that last audited this unit, if any.</summary>
    public Ulid? AuditedInSession { get; set; }

    /// <summary>Stable identity across cycles/edits (§2). Not serialized (derivable).</summary>
    [JsonIgnore]
    public string UnitHash => HashUtil.UnitHash(Path);
}

/// <summary>
/// The inventory of a single cycle (§2), stored as <c>inventory/{cycleN}.json</c>.
/// A cycle closes when no <see cref="UnitState.Pendiente"/> units remain (large ones
/// do not block closure).
/// </summary>
public sealed class InventoryCycle
{
    public int SchemaVersion { get; set; } = 1;

    public int CycleN { get; set; }

    public List<InventoryUnit> Units { get; set; } = new();

    /// <summary>
    /// La lupa del ciclo (F17): qué busca el auditor mientras dura. Es política compartida —vive
    /// en el hub, con el ciclo— porque cambia el significado de «auditada» para todo el equipo.
    /// Los ficheros anteriores a F17 no traen la clave y se leen como General.
    /// </summary>
    [JsonPropertyName("tematica")]
    public AuditTheme Theme { get; set; } = AuditTheme.General;

    /// <summary>
    /// El proveedor y el modelo con los que el equipo PREFIERE auditar este ciclo (F17 §5). Es una
    /// preferencia, no una imposición: no todo el mundo tiene las dos casas, y la sesión registra
    /// el juez real. Null = sin preferencia declarada.
    /// </summary>
    public string? PreferredProvider { get; set; }

    /// <inheritdoc cref="PreferredProvider"/>
    public string? PreferredModel { get; set; }

    /// <summary>
    /// Cuándo se abrió este ciclo (F17). Lo escribe quien lo abre —el alta, el cierre del anterior,
    /// el reinicio—. Null en los ciclos anteriores a F17, y entonces la fecha se infiere de sus
    /// sesiones como siempre hizo <c>CycleSummary</c>; nunca se rellena.
    /// </summary>
    public DateTimeOffset? OpenedUtc { get; set; }

    /// <summary>
    /// El HISTORIAL de temáticas del ciclo (F17.1): con qué lupa se trabajó y desde cuándo, una
    /// entrada por periodo, la última abierta. Un cambio de temática a mitad de ciclo es una
    /// decisión de gobernanza con re-siembra detrás: los hallazgos de la lupa anterior existen y
    /// llevan su temática, así que pintar el ciclo entero con la última los negaría. Nada se borra;
    /// todo lleva historial.
    /// <para>
    /// Vacío en los ficheros anteriores a F17.1: entonces <see cref="Periods"/> deriva una sola
    /// entrada con la temática vigente desde la apertura, sin migrar nada — como se hizo con «lo
    /// anterior es General».
    /// </para>
    /// </summary>
    [JsonPropertyName("historialTematica")]
    public List<ThemePeriod> ThemeHistory { get; set; } = new();

    /// <summary>
    /// Los periodos de temática, siempre al menos uno: el historial escrito o, si no lo hay, la
    /// temática vigente desde la apertura (o desde «no se sabe», si tampoco hay apertura).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ThemePeriod> Periods
        => ThemeHistory.Count > 0
            ? ThemeHistory
            : new[] { new ThemePeriod(Theme, OpenedUtc, null, null) };

    /// <summary>
    /// Cambia la temática vigente dejando rastro: cierra el periodo abierto y abre otro con autor y
    /// fecha. Si el historial estaba vacío (ciclo anterior a F17.1), primero se materializa la
    /// entrada derivada, para que el cambio no borre de dónde venía.
    /// </summary>
    public void ChangeTheme(AuditTheme theme, DateTimeOffset atUtc, string? by)
    {
        if (ThemeHistory.Count == 0)
        {
            ThemeHistory.AddRange(Periods);
        }

        ThemePeriod last = ThemeHistory[^1];
        ThemeHistory[^1] = last with { ToUtc = atUtc };
        ThemeHistory.Add(new ThemePeriod(theme, atUtc, null, by));
        Theme = theme;
    }

    /// <summary>Abre el historial con la temática con la que nace el ciclo.</summary>
    public void OpenThemeHistory(AuditTheme theme, DateTimeOffset? atUtc, string? by)
    {
        ThemeHistory.Clear();
        ThemeHistory.Add(new ThemePeriod(theme, atUtc, null, by));
        Theme = theme;
    }

    /// <summary>La configuración del ciclo, como un solo valor comparable.</summary>
    [JsonIgnore]
    public CycleConfig Config
    {
        get => new(Theme, PreferredProvider, PreferredModel);
        set
        {
            Theme = value.Theme;
            PreferredProvider = value.PreferredProvider;
            PreferredModel = value.PreferredModel;
        }
    }

    /// <summary>The cycle can close when nothing is pending (§5.1). Large units don't block.</summary>
    public bool HasPending => Units.Any(u => u.State == UnitState.Pendiente);
}

/// <summary>
/// Lo que se configura de un ciclo (F17 §4): su lupa y el juez preferido. Es un valor —dos ciclos
/// con la misma configuración son intercambiables— y por eso la herencia al cerrar se expresa
/// copiándolo entero, y «cambió la temática» se pregunta comparando <see cref="Theme"/>.
/// </summary>
public sealed record CycleConfig(AuditTheme Theme, string? PreferredProvider, string? PreferredModel)
{
    /// <summary>General, sin preferencia de juez: el ciclo tal y como existía antes de F17.</summary>
    public static CycleConfig Default { get; } = new(AuditTheme.General, null, null);

    /// <summary>Si hay un modelo preferido declarado (con proveedor o sin él).</summary>
    public bool HasPreferredModel => !string.IsNullOrWhiteSpace(PreferredModel);
}

/// <summary>
/// Un periodo de temática dentro de un ciclo (F17.1): qué lupa, desde cuándo, hasta cuándo (null =
/// sigue vigente) y quién la puso (null en lo derivado y en lo abierto por el sistema).
/// </summary>
public sealed record ThemePeriod(AuditTheme Theme, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? By);
