using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Domain.Rules;

namespace Atalaya.App.Services;

/// <summary>
/// Cuánto sabemos de una unidad, que es lo que decide cómo se pinta (F10 §1, honestidad
/// estructural). No es el estado del inventario: <see cref="UnitState.Grande"/> es un motivo por
/// el que NO se auditó, no una forma de haberla auditado.
/// </summary>
public enum HeatKnowledge
{
    /// <summary>Alguien la barrió: su densidad es una medida.</summary>
    Auditada,

    /// <summary>
    /// Se auditó, pero su contenido ha cambiado desde entonces. La medida sigue existiendo y ya
    /// no es de este código.
    /// </summary>
    Cambiada,

    /// <summary>Nadie la ha barrido. Su densidad es DESCONOCIDA, que no es cero.</summary>
    NoAuditada,
}

/// <summary>Una unidad en el mapa: su tamaño, su deuda conocida y cuánto se sabe de ella.</summary>
/// <param name="KnownDebt">
/// Suma de pesos de sus hallazgos ACTIVOS. En una unidad no auditada esto es una cota inferior —
/// lo que se sabe—, nunca el total.
/// </param>
/// <param name="Density">
/// Deuda por KLOC, o <c>null</c> cuando no se puede afirmar: sin auditar, o sin LOC que dividir.
/// </param>
public sealed record HeatUnit(
    string Path,
    string Module,
    int Loc,
    UnitState State,
    HeatKnowledge Knowledge,
    SeverityChips Findings,
    int KnownDebt,
    double? Density)
{
    /// <summary>El nombre de fichero, que es lo que cabe en una celda.</summary>
    public string FileName => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;

    /// <summary>La densidad es una medida de ESTE código.</summary>
    public bool IsMeasured => Knowledge == HeatKnowledge.Auditada;

    /// <summary>
    /// El relleno no cuenta toda la verdad y la celda tiene que decirlo: o es gris teniendo deuda
    /// conocida (nadie la miró y aun así se le conocen hallazgos), o su medida es de otro código.
    /// </summary>
    public bool IsQualified => Knowledge == HeatKnowledge.Cambiada
                               || (Knowledge == HeatKnowledge.NoAuditada && KnownDebt > 0);

    /// <summary>El valor que pinta la celda con la métrica elegida, o null si no se puede afirmar.</summary>
    public double? Value(HeatMetric metric) => !IsMeasured
        ? null
        : metric == HeatMetric.Deuda ? KnownDebt : Density;
}

/// <summary>
/// Un módulo del mapa, con sus unidades y sus agregados.
/// <para>
/// <b>Dos denominadores distintos, a propósito.</b> <see cref="KnownDebt"/> suma TODA la deuda
/// conocida del módulo, la de las unidades auditadas y la de las que no. <see cref="Density"/>
/// divide solo lo auditado entre lo auditado. Meter las unidades sin auditar en el denominador
/// diluiría la densidad hacia abajo en proporción a lo poco que se ha mirado — un módulo con una
/// unidad podrida y noventa sin auditar saldría «casi limpio», que es exactamente al revés. Por
/// eso los dos números viajan siempre con <see cref="Coverage"/> pegado.
/// </para>
/// </summary>
public sealed record HeatModule(
    string Name,
    IReadOnlyList<HeatUnit> Units,
    int Loc,
    int AuditedLoc,
    int AuditedUnits,
    int KnownDebt,
    int AuditedDebt,
    double? Density)
{
    private SeverityChips? _severities;

    public int UnitCount => Units.Count;

    /// <summary>Unidades auditadas sobre el total. 0 no es «limpio», es «sin mirar».</summary>
    public double Coverage => UnitCount == 0 ? 0 : (double)AuditedUnits / UnitCount;

    /// <summary>
    /// Cobertura por LÍNEAS, que es otra pregunta: auditar la clase de 5.000 líneas y auditar un
    /// enum de 40 cuentan lo mismo por unidades y no cuentan lo mismo por código mirado. La usa la
    /// confianza del orden «Atención» (F10.2 §1); la barra de la tarjeta sigue contando unidades,
    /// que es la cobertura que enseña el resto de la aplicación.
    /// </summary>
    public double CoverageLoc => Loc == 0 ? 0 : (double)AuditedLoc / Loc;

    /// <summary>Los hallazgos activos del módulo, por severidad. Se calcula una vez.</summary>
    public SeverityChips Severities => _severities ??= new SeverityChips(
        Units.Sum(u => u.Findings.Critica),
        Units.Sum(u => u.Findings.Alta),
        Units.Sum(u => u.Findings.Media),
        Units.Sum(u => u.Findings.Baja));

    /// <summary>Ninguna unidad auditada: el módulo entero es desconocido.</summary>
    public bool IsMeasured => AuditedUnits > 0 && Density is not null;

    /// <summary>La deuda conocida que vive en unidades que nadie ha auditado.</summary>
    public int UnauditedDebt => KnownDebt - AuditedDebt;

    /// <summary>Ver <see cref="HeatUnit.IsQualified"/>, al nivel del módulo.</summary>
    public bool IsQualified => UnauditedDebt > 0 || Units.Any(u => u.Knowledge == HeatKnowledge.Cambiada);

    public double? Value(HeatMetric metric) => !IsMeasured
        ? null
        : metric == HeatMetric.Deuda ? AuditedDebt : Density;
}

/// <summary>El mapa de una aplicación, ya agregado. No se guarda: se calcula en cada carga.</summary>
public sealed record HeatmapView(
    string Slug,
    string AppName,
    int CycleN,
    IReadOnlyList<AppOption> AppOptions,
    IReadOnlyList<HeatModule> Modules)
{
    public static HeatmapView Empty { get; } =
        new(string.Empty, string.Empty, 0, Array.Empty<AppOption>(), Array.Empty<HeatModule>());

    public IEnumerable<HeatUnit> Units => Modules.SelectMany(m => m.Units);

    public int TotalUnits => Modules.Sum(m => m.UnitCount);

    public int AuditedUnits => Modules.Sum(m => m.AuditedUnits);

    public int TotalLoc => Modules.Sum(m => m.Loc);

    public int KnownDebt => Modules.Sum(m => m.KnownDebt);

    /// <summary>Sin inventario no hay mapa. No se dibuja un lienzo vacío: se dice por qué.</summary>
    public bool IsEmpty => Modules.Count == 0;

    /// <summary>Unidades auditadas sobre el total del ciclo.</summary>
    public double Coverage => TotalUnits == 0 ? 0 : (double)AuditedUnits / TotalUnits;

    /// <summary>La densidad de la aplicación: lo auditado entre lo auditado (ver <see cref="HeatModule"/>).</summary>
    public double? Density
    {
        get
        {
            int loc = Modules.Sum(m => m.AuditedLoc);
            return loc == 0 ? null : DebtWeights.Density(Modules.Sum(m => m.AuditedDebt), loc);
        }
    }
}

/// <summary>
/// Agrega el mapa de calor (F10 §1) desde los datos primarios del hub: el inventario del ciclo
/// vigente y los hallazgos de la aplicación. <b>No inventa nada</b>: no hay complejidad
/// ciclomática, ni acoplamiento, ni ninguna métrica que Atalaya no mida ya. Las LOC salen del
/// inventario y la deuda de los hallazgos, y eso es todo lo que hay.
/// </summary>
public sealed class HeatmapQuery
{
    private readonly HubContext _hub;

    public HeatmapQuery(HubContext hub) => _hub = hub;

    /// <summary>Las aplicaciones del portafolio, para el selector. Nunca «todas»: el mapa es de UNA.</summary>
    public IReadOnlyList<AppOption> AppOptions()
        => _hub.Store.ListAppSlugs()
            .Select(s => new AppOption(s, _hub.Store.TryReadApp(s)?.Name ?? s))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// La densidad de cada unidad, indexada por su ruta (F10.1 §1). La usa el <b>inventario</b>
    /// para pintar la misma franja de color que el mapa: es el mismo dato, así que tiene que salir
    /// del mismo sitio — dos cálculos parecidos en dos vistas es cómo acaban discrepando.
    /// </summary>
    public IReadOnlyDictionary<string, HeatUnit> ByUnit(string? slug)
        => Build(slug).Units.ToDictionary(u => u.Path, u => u, StringComparer.Ordinal);

    /// <summary>
    /// El mapa de una aplicación. Devuelve <see cref="HeatmapView.Empty"/> con el selector puesto
    /// cuando la app no existe o no tiene inventario: la vista dice qué falta, no se cae.
    /// </summary>
    public HeatmapView Build(string? slug)
    {
        IReadOnlyList<AppOption> options = AppOptions();
        if (string.IsNullOrWhiteSpace(slug) || _hub.Store.TryReadApp(slug) is not { } app)
        {
            return HeatmapView.Empty with { AppOptions = options };
        }

        InventoryCycle? inventory = _hub.Store.TryReadInventory(app.Slug, app.CurrentCycle);
        var units = inventory?.Units ?? new List<InventoryUnit>();
        if (units.Count == 0)
        {
            return HeatmapView.Empty with
            {
                Slug = app.Slug,
                AppName = app.Name,
                CycleN = app.CurrentCycle,
                AppOptions = options,
            };
        }

        IReadOnlyDictionary<string, List<Severity>> byUnit = ActiveSeveritiesByUnit(app.Slug);
        IReadOnlySet<string> changed = ChangedSinceAudit(app.Slug, app.CurrentCycle, units);

        var modules = units
            .GroupBy(u => u.Module, StringComparer.Ordinal)
            .Select(g => Module(g.Key, g, byUnit, changed))
            .OrderByDescending(m => m.Loc)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HeatmapView(app.Slug, app.Name, app.CurrentCycle, options, modules);
    }

    private static HeatModule Module(
        string name,
        IEnumerable<InventoryUnit> raw,
        IReadOnlyDictionary<string, List<Severity>> byUnit,
        IReadOnlySet<string> changed)
    {
        var units = raw
            .Select(u => Unit(u, byUnit, changed))
            .OrderByDescending(u => u.Loc)
            .ThenBy(u => u.Path, StringComparer.Ordinal)
            .ToList();

        int auditedLoc = units.Where(u => u.IsMeasured).Sum(u => u.Loc);
        int auditedDebt = units.Where(u => u.IsMeasured).Sum(u => u.KnownDebt);

        return new HeatModule(
            name,
            units,
            units.Sum(u => u.Loc),
            auditedLoc,
            units.Count(u => u.IsMeasured),
            units.Sum(u => u.KnownDebt),
            auditedDebt,
            DebtWeights.Density(auditedDebt, auditedLoc));
    }

    private static HeatUnit Unit(
        InventoryUnit unit,
        IReadOnlyDictionary<string, List<Severity>> byUnit,
        IReadOnlySet<string> changed)
    {
        var severities = byUnit.TryGetValue(unit.Path, out List<Severity>? found)
            ? found
            : new List<Severity>();

        HeatKnowledge knowledge = unit.State != UnitState.Auditada
            ? HeatKnowledge.NoAuditada
            : changed.Contains(unit.Path) ? HeatKnowledge.Cambiada : HeatKnowledge.Auditada;

        int debt = DebtWeights.Sum(severities);
        var chips = new SeverityChips(
            severities.Count(s => s == Severity.Critica),
            severities.Count(s => s == Severity.Alta),
            severities.Count(s => s == Severity.Media),
            severities.Count(s => s == Severity.Baja));

        return new HeatUnit(
            unit.Path,
            unit.Module,
            unit.Loc,
            unit.State,
            knowledge,
            chips,
            debt,
            knowledge == HeatKnowledge.Auditada ? DebtWeights.Density(debt, unit.Loc) : null);
    }

    /// <summary>
    /// Las severidades de los hallazgos <b>activos</b> de cada unidad. Los resueltos y los
    /// silenciados no son deuda —uno se arregló y del otro se decidió que no se arregla—, así que
    /// sumarlos pintaría de oscuro el trabajo ya hecho.
    /// <para>
    /// Un hallazgo con varias ubicaciones (un defecto sistémico, F4.1) pesa <b>una vez en cada
    /// unidad donde está</b>, y nunca dos veces en la misma. Es lo correcto para una vista por
    /// unidad: quien vaya a arreglar esa unidad tiene ese problema delante, esté también en otras.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<string, List<Severity>> ActiveSeveritiesByUnit(string slug)
    {
        var map = new Dictionary<string, List<Severity>>(StringComparer.Ordinal);
        foreach (Finding finding in _hub.Store.ListFindings(slug))
        {
            if (finding.Status != FindingStatus.Activo)
            {
                continue;
            }

            foreach (string path in finding.Locations
                         .Select(l => l.Path)
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.Ordinal))
            {
                if (!map.TryGetValue(path, out List<Severity>? list))
                {
                    list = new List<Severity>();
                    map[path] = list;
                }

                list.Add(finding.Severity);
            }
        }

        return map;
    }

    /// <summary>
    /// Qué unidades auditadas ya no tienen el contenido que tenían cuando se auditaron.
    /// <para>
    /// Sale de comparar el <c>contentHash</c> de hoy con el que esa misma ruta tenía en el
    /// inventario del ciclo en que se auditó (<see cref="InventoryUnit.AuditedInSession"/> dice en
    /// qué sesión, y la sesión en qué ciclo). Es lo que Atalaya ya guarda; no se abre ni un fichero
    /// del clon.
    /// </para>
    /// <para>
    /// Cuando no se puede afirmar —sin sesión registrada, sin inventario de aquel ciclo, sin hash,
    /// o auditada en el ciclo vigente— la unidad NO se marca. «Cambiada» es una afirmación, y solo
    /// se hace con el dato delante.
    /// </para>
    /// </summary>
    private IReadOnlySet<string> ChangedSinceAudit(
        string slug, int currentCycle, IReadOnlyList<InventoryUnit> units)
    {
        var audited = units
            .Where(u => u.State == UnitState.Auditada
                        && u.AuditedInSession is not null
                        && u.ContentHash is not null)
            .ToList();

        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (audited.Count == 0)
        {
            return changed;
        }

        var cycleOf = _hub.Store.ListSessions(slug)
            .GroupBy(s => s.Id.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().CycleN, StringComparer.Ordinal);
        var older = new Dictionary<int, Dictionary<string, string?>>();

        foreach (InventoryUnit unit in audited)
        {
            if (!cycleOf.TryGetValue(unit.AuditedInSession!.Value.ToString(), out int cycle)
                || cycle >= currentCycle)
            {
                continue;
            }

            if (!older.TryGetValue(cycle, out Dictionary<string, string?>? snapshot))
            {
                snapshot = _hub.Store.TryReadInventory(slug, cycle)?.Units
                               .GroupBy(u => u.Path, StringComparer.Ordinal)
                               .ToDictionary(g => g.Key, g => g.First().ContentHash, StringComparer.Ordinal)
                           ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                older[cycle] = snapshot;
            }

            if (snapshot.TryGetValue(unit.Path, out string? then)
                && then is not null
                && !string.Equals(then, unit.ContentHash, StringComparison.Ordinal))
            {
                changed.Add(unit.Path);
            }
        }

        return changed;
    }
}
