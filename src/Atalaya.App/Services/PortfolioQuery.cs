using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage;

namespace Atalaya.App.Services;

/// <summary>A computed portfolio card for one app (§8 V1). Never stored — always derived.</summary>
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
    public int SortKey => Critica > 0 ? 0 : ActiveTotal > 0 ? 1 : 2;

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
            .ThenByDescending(c => c.Critica + c.Alta)
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
        bool auditingNow = _store.ListClaims(slug).Any(c => c.AnnouncesActivityAt(now));

        return new AppCard(
            slug, app.Name, app.Stack, app.CurrentCycle,
            total, audited, large, progress,
            Count(Severity.Critica), Count(Severity.Alta), Count(Severity.Media), Count(Severity.Baja),
            active.Count,
            last?.By, last?.StartedUtc,
            auditingNow,
            BuildTrend(active, now));
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
