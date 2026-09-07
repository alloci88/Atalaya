using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage;

namespace Atalaya.App.Services;

/// <summary>
/// El orden del Portafolio, en un solo sitio (F17 §6): primero las apps con críticas, luego las
/// que tienen deuda viva, luego las limpias; dentro de cada grupo, más críticas+altas antes; y
/// el nombre desempata. La cinta de ciclos de Métricas ordena sus bandas con ESTA misma regla,
/// para que «como el Portafolio» no sea una copia que se quede sin actualizar.
/// </summary>
public static class PortfolioOrder
{
    public static int Key(int critica, int activeTotal) => critica > 0 ? 0 : activeTotal > 0 ? 1 : 2;

    public static int Weight(int critica, int alta) => critica + alta;
}

/// <summary>A computed portfolio card for one app (§8 V1). Never stored — always derived.</summary>
/// <summary>
/// Una persona auditando una aplicación ahora mismo, con cuántas unidades tiene cogidas (F31 §4).
/// <para>
/// Sin servidor no hay presencia: la única señal de que alguien está trabajando es su reclamación
/// reciente. Por eso esto se deduce de los claims vivos y no de una lista de conectados que no
/// existe.
/// </para>
/// </summary>
public sealed record Auditor(string Name, int Units)
{
    /// <summary>«Daniel Rodríguez está auditando ahora · 3 unidades».</summary>
    public string Label => Units == 1
        ? $"{Name} está auditando ahora · 1 unidad"
        : $"{Name} está auditando ahora · {Units} unidades";
}

public sealed record AppCard(
    string Slug,
    string Name,
    TechStack Stack,
    int CurrentCycle,
    int TotalUnits,
    int AuditedUnits,
    int LargeUnits,
    double Progress,
    int Critica,
    int Alta,
    int Media,
    int Baja,
    int ActiveTotal,
    string? LastSessionBy,
    DateTimeOffset? LastSessionUtc,
    bool AuditingNow,
    IReadOnlyList<int> Trend)
{
    /// <summary>
    /// <b>Quién</b> está auditando ahora mismo, y cuánto (F31 §4). Vacía cuando no hay nadie o
    /// cuando quien audita soy yo desde esta máquina.
    /// <para>
    /// Va aparte de <see cref="AuditingNow"/> y no lo sustituye porque las dos preguntas son
    /// distintas: la papelera solo necesita saber si hay ALGUIEN, y quien mira la tarjeta necesita
    /// saber QUIÉN. Un nombre es lo único que convierte «esta aplicación está ocupada» en «habla
    /// con Daniel antes de tocarla».
    /// </para>
    /// </summary>
    public IReadOnlyList<Auditor> Auditors { get; init; } = Array.Empty<Auditor>();

    /// <summary>Hay nombres que enseñar, que no es lo mismo que haber actividad.</summary>
    public bool HasAuditors => Auditors.Count > 0;

    /// <summary>
    /// Hay actividad pero sin nombre que ponerle: es mi propia sesión, recién lanzada y todavía
    /// sin reclamaciones publicadas. Se anuncia igual, sin fingir que sé de quién es.
    /// </summary>
    public bool AuditingWithoutName => AuditingNow && Auditors.Count == 0;

    private readonly CloneLink? _link;

    /// <summary>
    /// «Ciclo 2 · 0,2 % auditado» (BUGFIX-REDONDEO). Se calcula con los ENTEROS y no con
    /// <see cref="Progress"/>: los extremos tienen que decidirse contando unidades, no
    /// preguntándole a un <c>double</c> si 0,99925 «es uno». Sin auditable que repartir devuelve
    /// «—», que es lo honesto: sin denominador no hay proporción, y un 0 % ahí sería inventado.
    /// </summary>
    public string ProgressText => PercentText.Of(AuditedUnits, TotalUnits - LargeUnits);

    /// <summary>
    /// Si esta máquina tiene el clon de la app (F5.8 §1). No sale de la consulta —el hub no sabe
    /// nada de las rutas locales de nadie, y no debe (§4)—: lo pone el view-model del portafolio
    /// leyendo <c>machines.json</c>, que es por-máquina. Sin ponerlo, una tarjeta dice lo mismo
    /// que diría en una máquina recién instalada: sin vincular.
    /// </summary>
    public CloneLink Link
    {
        get => _link ?? CloneLink.Unknown(Slug);
        init => _link = value;
    }

    /// <summary>
    /// Cuántas unidades auditadas han cambiado desde su auditoría (F9 §5). No sale de la consulta
    /// —la deriva se deriva del clon LOCAL, y el hub no sabe nada de las rutas de nadie—: lo pone el
    /// view-model del portafolio, igual que <see cref="Link"/>. <c>null</c> mientras no se ha
    /// calculado o cuando no se puede: un cero ahí sería la mentira tranquilizadora.
    /// </summary>
    public int? ChangedUnits { get; init; }

    /// <summary>Arregladas desde Atalaya y sin verificar (F9 §2). Va SEPARADO: es otra acción.</summary>
    public int? FixedPendingVerify { get; init; }

    /// <summary>
    /// <b>Cuántas sesiones de esta aplicación se quedaron sin coste, y por qué</b> (F29 §1). No sale
    /// de la consulta —hace falta la tabla de tarifas del hub y las reconciliaciones ya escritas, y
    /// esta consulta solo mira lo primario de la app—: lo pone el view-model, igual que
    /// <see cref="Link"/> y que la deriva. Null mientras no se ha preguntado.
    /// </summary>
    public AppCostGap? CostGap { get; init; }

    /// <summary>
    /// La insignia solo aparece cuando hay algo que reconciliar. Sin sesiones sin coste no hay
    /// insignia: un ámbar permanente en cada tarjeta se aprende a no ver.
    /// </summary>
    public bool HasCostGap => CostGap is { Sessions: > 0 };

    /// <summary>«3 sesiones sin coste», junto a la línea de última sesión.</summary>
    public string CostGapLabel => CostGap?.Badge ?? string.Empty;

    /// <summary>El motivo resumido, y desde dónde se cierra.</summary>
    public string CostGapTooltip => CostGap is { Sessions: > 0 } gap
        ? $"{gap.Reason}. Se reconcilian desde el resumen del ciclo, en el inventario de esta aplicación."
        : string.Empty;

    /// <summary>La lupa del ciclo vigente (F17): un distintivo en la tarjeta, con su color.</summary>
    public AuditTheme Theme { get; init; } = AuditTheme.General;

    public string ThemeLabel => Copilot.ThemeCatalog.Display(Theme);

    public string ThemeTooltip => Theme == AuditTheme.General
        ? "Ciclo General: el criterio completo."
        : $"Ciclo temático de {Copilot.ThemeCatalog.Display(Theme)}: el auditor busca solo esa familia de defectos. "
          + "Un ciclo temático no sustituye a uno General.";

    /// <summary>
    /// Lo que se lee en la tarjeta. Es la frase que convierte esto en un hábito: cada mañana dice
    /// cuánta deuda nueva puede haber entrado sin que nadie lo busque.
    /// </summary>
    public string DriftLabel => ChangedUnits switch
    {
        null when !Link.CanAudit => "Vincula tu clon para ver la deriva",
        null => "Calculando la deriva…",
        0 when FixedPendingVerify is > 0 => FixedPendingVerify == 1
            ? "1 arreglada pendiente de verificar"
            : $"{FixedPendingVerify} arregladas pendientes de verificar",
        0 => "Nada ha cambiado desde su auditoría",
        1 => "1 clase cambiada desde su auditoría",
        _ => $"{ChangedUnits} clases cambiadas desde su auditoría",
    };

    /// <summary>El indicador solo LLEVA a algún sitio cuando hay algo que mirar.</summary>
    public bool DriftIsActionable => ChangedUnits is > 0 || FixedPendingVerify is > 0;

    public string DriftTooltip => ChangedUnits is null
        ? Link.CanAudit
            ? "La deriva se calcula del historial de tu clon local; todavía no ha terminado."
            : "La deriva se deriva del historial del clon local. Sin clon en esta máquina no hay "
              + "historial que comparar, y un cero aquí sería mentira."
        : $"{ChangedUnits} unidad(es) auditada(s) con cambios ajenos desde su auditoría"
          + (FixedPendingVerify is > 0
              ? $" · {FixedPendingVerify} arreglada(s) desde Atalaya, pendiente(s) de verificar (eso se verifica, no se re-audita)"
              : string.Empty)
          + ". Un clic abre el Inventario con el filtro puesto.";

    /// <summary>Apps with open critical findings sort first (§8 V1).</summary>
    public int SortKey => PortfolioOrder.Key(Critica, ActiveTotal);

    /// <summary>
    /// Borrar exige que NADIE esté auditando la app (F5.3 §4). Un hard-reset a mitad de sesión
    /// dejaría al auditor escribiendo hallazgos en una carpeta recién borrada y los claims vivos
    /// apuntando a una app que ya no existe.
    /// </summary>
    public bool CanDelete => !AuditingNow;

    public string DeleteTooltip => AuditingNow
        ? "Detén la sesión primero"
        : $"Eliminar «{Name}» y toda su traza en el hub";
}

/// <summary>
/// Computes the portfolio dashboards from primary hub data (mejora 8: dashboards are always
/// calculated; only primary data lives on disk).
/// </summary>
public sealed class PortfolioQuery
{
    private readonly HubStore _store;
    private readonly TimeProvider _time;

    public PortfolioQuery(HubStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<AppCard> BuildAll()
        => _store.ListAppSlugs()
            .Select(Build)
            .Where(c => c is not null)
            .Select(c => c!)
            .OrderBy(c => c.SortKey)
            .ThenByDescending(c => PortfolioOrder.Weight(c.Critica, c.Alta))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public AppCard? Build(string slug)
    {
        AppConfig? app = _store.TryReadApp(slug);
        if (app is null)
        {
            return null;
        }

        InventoryCycle? inv = _store.TryReadInventory(slug, app.CurrentCycle);
        int total = inv?.Units.Count ?? 0;
        int audited = inv?.Units.Count(u => u.State == UnitState.Auditada) ?? 0;
        int large = inv?.Units.Count(u => u.State == UnitState.Grande) ?? 0;
        int auditable = Math.Max(1, total - large);
        double progress = total == 0 ? 0 : Math.Clamp((double)audited / auditable, 0, 1);

        var findings = _store.ListFindings(slug);
        var active = findings.Where(f => f.Status == FindingStatus.Activo).ToList();
        int Count(Severity s) => active.Count(f => f.Severity == s);

        AuditSession? last = _store.ListSessions(slug)
            .OrderByDescending(s => s.StartedUtc)
            .FirstOrDefault();

        DateTimeOffset now = _time.GetUtcNow();
        // BUGFIX-ACTIVIDAD: se pregunta si el claim puede ANUNCIARSE, no solo si no ha caducado.
        // El margen lo pone quien lee (ClaimRules.MaxSilence), no quien escribió el claim: un
        // portátil cerrado a mitad de sesión no puede tener al equipo entero viendo «auditando
        // ahora» hasta que a su TTL le dé la gana.
        var vivos = _store.ListClaims(slug).Where(c => c.AnnouncesActivityAt(now)).ToList();
        bool auditingNow = vivos.Count > 0;

        // TODAS las personas, no la primera: si coinciden tres, la tarjeta las dice las tres. Y
        // con cuántas unidades lleva cada una, que es lo que separa «acaba de empezar» de «lleva
        // media aplicación».
        var auditores = vivos
            .GroupBy(c => c.By, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Auditor(g.Key, g.Count()))
            .OrderByDescending(a => a.Units)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new AppCard(
            slug, app.Name, app.Stack, app.CurrentCycle,
            total, audited, large, progress,
            Count(Severity.Critica), Count(Severity.Alta), Count(Severity.Media), Count(Severity.Baja),
            active.Count,
            last?.By, last?.StartedUtc,
            auditingNow,
            BuildTrend(active, now))
        {
            Theme = inv?.Theme ?? AuditTheme.General,
            Auditors = auditores,
        };
    }

    /// <summary>A 30-day sparkline of active findings first detected per day.</summary>
    private static IReadOnlyList<int> BuildTrend(IReadOnlyList<Finding> active, DateTimeOffset now)
    {
        const int days = 30;
        var buckets = new int[days];
        DateTimeOffset start = now.Date.AddDays(-(days - 1));
        foreach (Finding f in active)
        {
            int idx = (int)(f.FirstDetected.Utc.Date - start).TotalDays;
            if (idx is >= 0 and < days)
            {
                buckets[idx]++;
            }
        }

        return buckets;
    }
}
