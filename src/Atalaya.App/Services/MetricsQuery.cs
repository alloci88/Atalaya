using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>El rango temporal del panel (F5.9 §3). Los filtros afectan a TODO el panel.</summary>
public enum MetricsRange
{
    Weeks4,
    Weeks8,
    Weeks26,
    All,
}

/// <summary>A qué se agrega el eje X. Sale del rango: corto, diario; muy largo, mensual.</summary>
public enum MetricsGranularity
{
    Diaria,
    Semanal,
    Mensual,
}

/// <summary>Lo que el usuario ha elegido en la fila de filtros.</summary>
public sealed record MetricsFilter(string? Slug, MetricsRange Range)
{
    /// <summary>Al abrir: todas las apps, ocho semanas.</summary>
    public static MetricsFilter Default { get; } = new(null, MetricsRange.Weeks8);
}

/// <summary>El desglose por severidad de los hallazgos activos (tile 1).</summary>
public sealed record SeverityChips(int Critica, int Alta, int Media, int Baja);

/// <summary>
/// Un punto del eje X: su etiqueta y lo que aportó cada app en el.
/// <para>
/// El punto NO sabe qué mide. Nació para el coste y hoy lo comparten la gráfica de coste y la de
/// resoluciones (F6.1): las dos son «línea por aplicación sobre el mismo eje temporal», y la
/// única diferencia entre ellas es de dónde sale el número de cada cubo. Duplicar el tipo habría
/// duplicado también los cubos, el reparto de «Otras» y el acumulado.
/// </para>
/// </summary>
public sealed record SeriesPoint(DateTimeOffset From, string Label, IReadOnlyDictionary<string, decimal> ByApp)
{
    public decimal Of(string slug) => ByApp.TryGetValue(slug, out decimal v) ? v : 0m;
}

/// <summary>Un rosco: lo auditado, lo pendiente y lo excluido por tamaño del ciclo vigente.</summary>
public sealed record CoverageDonut(string Slug, string Name, int Cycle, int Audited, int Pending, int Large)
{
    /// <summary>Total del inventario, grandes incluidas: es lo que suma el anillo.</summary>
    public int Total => Audited + Pending + Large;

    /// <summary>
    /// Lo auditado sobre lo AUDITABLE. Las unidades grandes están excluidas por definición
    /// (nadie las va a auditar en este ciclo), así que contarlas en el denominador daría una app
    /// «al 70 %» que en realidad ya no tiene nada pendiente. Es la misma cuenta que el progreso
    /// de la tarjeta del portafolio: la misma app no puede enseñar dos porcentajes distintos.
    /// </summary>
    public double Pct => Audited + Pending == 0 ? 0 : (double)Audited / (Audited + Pending);

    /// <summary>Un inventario vacío no tiene rosco que pintar.</summary>
    public bool HasData => Total > 0;
}

/// <summary>Un cubo del flujo: lo que entró, lo que salió y la deuda viva al cerrarlo.</summary>
public sealed record FlowBucket(string Label, int New, int Resolved, int ActiveAtEnd);

/// <summary>Una línea del registro de operaciones (gráfica 4).</summary>
public sealed record SessionRow(
    string SessionId,
    string Slug,
    string AppName,
    DateTimeOffset When,
    string By,
    AuditMode Mode,
    int Units,
    int New,
    int Resolved,
    decimal? Cost,
    string CostUnit);

/// <summary>Una opción del selector de aplicación.</summary>
public sealed record AppOption(string? Slug, string Name)
{
    public bool IsAll => Slug is null;
}

/// <summary>
/// Todo lo que el panel de métricas enseña, ya agregado (F5.9 §3). Nada de esto se guarda: se
/// calcula de los datos primarios en cada carga (norma: en el hub solo datos primarios).
/// </summary>
public sealed record MetricsDashboard(
    MetricsFilter Filter,
    DateTimeOffset From,
    DateTimeOffset To,
    MetricsGranularity Granularity,
    IReadOnlyList<AppOption> AppOptions,
    IReadOnlyDictionary<string, int> Palette,
    int ActiveTotal,
    SeverityChips Active,
    int ResolvedInPeriod,
    int ResolvedPreviousPeriod,
    decimal? CostInPeriod,
    string CostUnit,
    decimal? CostPerAuditedUnit,
    int UnitsAuditedInPeriod,
    int CycleAudited,
    int CyclePending,
    int CycleLarge,
    IReadOnlyList<string> CostSeries,
    bool CostSeriesHasOthers,
    IReadOnlyDictionary<string, string> AppNames,
    IReadOnlyList<SeriesPoint> Cost,
    IReadOnlyList<string> ResolutionSeries,
    bool ResolutionSeriesHasOthers,
    IReadOnlyList<SeriesPoint> Resolutions,
    IReadOnlyList<CoverageDonut> Coverage,
    IReadOnlyList<FlowBucket> Flow,
    IReadOnlyList<SessionRow> Sessions)
{
    /// <summary>La clave con la que se agrupa lo que no tiene color propio.</summary>
    public const string OthersSlug = " otras";

    /// <summary>Cómo se escribe esa clave en la leyenda y en los tooltips.</summary>
    public const string OthersLabel = "Otras";

    /// <summary>
    /// No hay NADA registrado: ni un hallazgo, ni una sesión, ni un inventario. Es el único caso
    /// en que el panel entero dice «sin datos» en vez de enseñar ceros.
    /// </summary>
    public bool IsEmpty => ActiveTotal == 0
                           && ResolvedInPeriod == 0
                           && Sessions.Count == 0
                           && Coverage.All(c => !c.HasData);

    /// <summary>La cobertura agregada del ciclo vigente (tile 4), sobre lo auditable.</summary>
    public double CyclePct => CycleAudited + CyclePending == 0
        ? 0
        : (double)CycleAudited / (CycleAudited + CyclePending);

    /// <summary>Sin inventario detrás, el tile no enseña «0 %»: dice que no lo sabe.</summary>
    public bool HasCycleData => CycleAudited + CyclePending + CycleLarge > 0;

    /// <summary>
    /// Hay coste medido en el periodo. Ninguna sesión está obligada a traerlo (el SDK puede no
    /// declararlo), y sin ella la gráfica de coste no puede dibujar nada honesto.
    /// </summary>
    public bool HasCost => CostInPeriod is not null;

    /// <summary>El delta del tile de resueltos: positivo = mejor que el periodo anterior.</summary>
    public int ResolvedDelta => ResolvedInPeriod - ResolvedPreviousPeriod;

    /// <summary>
    /// Hubo alguna resolución en el periodo. Sin ninguna, la gráfica no se dibuja vacía: lo dice
    /// (F6.1). Se mira el DATO agregado y no el tile de resueltos, porque el tile cuenta el estado
    /// de hoy y la gráfica cuenta eventos: un hallazgo resuelto y luego reabierto no aparece en el
    /// tile y sí aporta su punto a la gráfica.
    /// </summary>
    public bool HasResolutions => ResolutionSeries.Count > 0;

    /// <summary>El nombre legible de una serie, incluida la agrupada.</summary>
    public string NameOf(string slug) => slug == OthersSlug
        ? OthersLabel
        : AppNames.TryGetValue(slug, out string? n) ? n : slug;
}

/// <summary>
/// Agrega el panel de métricas (F5.9) desde los datos primarios del hub.
/// <para>
/// <b>La caché.</b> Cada render del panel tocaría todos los hallazgos, todas las sesiones y todos
/// los inventarios de todas las apps, y el panel se re-agrega con cada cambio de filtro, que es un
/// gesto de un clic. Se cachea la LECTURA (el volcado de ficheros por app) en memoria, nunca en
/// disco: la norma dice que en el hub solo hay datos primarios, así que un fichero de agregados
/// sería exactamente lo que no se puede añadir. La caché se invalida con el evento de sync del
/// hub, que es el único momento en que esos ficheros cambian por debajo.
/// </para>
/// </summary>
public sealed class MetricsQuery
{
    /// <summary>Cuántas sesiones caben en el registro sin convertirlo en otra lista infinita.</summary>
    public const int MaxSessionRows = 25;

    private readonly HubContext _hub;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private IReadOnlyList<AppData>? _cache;

    public MetricsQuery(HubContext hub, TimeProvider? time = null)
    {
        _hub = hub;
        _time = time ?? TimeProvider.System;
        _hub.SyncStateChanged += Invalidate;
        _hub.Changed += _ => Invalidate();
    }

    /// <summary>Todo lo leído de una app. Es lo que se cachea; los agregados salen de aquí.</summary>
    private sealed record AppData(
        string Slug,
        string Name,
        int CurrentCycle,
        IReadOnlyList<Finding> Findings,
        IReadOnlyList<AuditSession> Sessions,
        InventoryCycle? Inventory);

    /// <summary>Tira la caché. La llama el evento de sync; también sirve a los tests.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    /// <summary>Las apps del portafolio, para el selector, sin agregar nada más.</summary>
    public IReadOnlyList<AppOption> AppOptions() => BuildOptions(Snapshot());

    /// <summary>
    /// Agrega el panel entero. No escribe nada y no lanza: una app ilegible se salta (el
    /// <c>HubStore</c> ya registra por que) en vez de tumbar la vista.
    /// </summary>
    public MetricsDashboard Build(MetricsFilter filter)
    {
        IReadOnlyList<AppData> all = Snapshot();
        var scope = filter.Slug is { Length: > 0 } slug
            ? all.Where(a => string.Equals(a.Slug, slug, StringComparison.OrdinalIgnoreCase)).ToList()
            : all.ToList();

        DateTimeOffset now = _time.GetUtcNow();
        (DateTimeOffset from, DateTimeOffset to) = Period(filter.Range, scope, now);
        MetricsGranularity granularity = GranularityFor(filter.Range, from, to);
        var buckets = Buckets(from, to, granularity);

        var findings = scope.SelectMany(a => a.Findings).ToList();
        var sessions = scope.SelectMany(a => a.Sessions).ToList();
        var inPeriod = sessions.Where(s => s.StartedUtc >= from && s.StartedUtc < to).ToList();

        var active = findings.Where(f => f.Status == FindingStatus.Activo).ToList();
        int Sev(Severity s) => active.Count(f => f.Severity == s);

        int resolvedNow = findings.Count(f => ResolvedIn(f, from, to));
        TimeSpan span = to - from;
        int resolvedBefore = findings.Count(f => ResolvedIn(f, from - span, from));

        decimal? cost = inPeriod.Any(s => s.Usage.Cost is not null)
            ? inPeriod.Sum(s => s.Usage.Cost ?? 0m)
            : null;
        int unitsAudited = inPeriod.Sum(s => s.Units.Count);
        string costUnit = inPeriod
            .Select(s => s.Usage.Currency)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? CostEstimator.DefaultCostUnit;

        int cycleAudited = 0;
        int cyclePending = 0;
        int cycleLarge = 0;
        var donuts = new List<CoverageDonut>();
        foreach (AppData app in scope)
        {
            IReadOnlyList<InventoryUnit> units =
                app.Inventory?.Units ?? (IReadOnlyList<InventoryUnit>)Array.Empty<InventoryUnit>();
            int audited = units.Count(u => u.State == UnitState.Auditada);
            int large = units.Count(u => u.State == UnitState.Grande);
            int pending = units.Count - audited - large;
            cycleAudited += audited;
            cyclePending += pending;
            cycleLarge += large;
            donuts.Add(new CoverageDonut(app.Slug, app.Name, app.CurrentCycle, audited, pending, large));
        }

        (IReadOnlyList<string> series, bool hasOthers) = TopSeries(scope, a => a.Sessions
            .Where(s => s.StartedUtc >= from && s.StartedUtc < to)
            .Sum(s => s.Usage.Cost ?? 0m));

        (IReadOnlyList<string> resSeries, bool resHasOthers) = TopSeries(scope, a => a.Findings
            .SelectMany(ResolutionEvents)
            .Count(utc => utc >= from && utc < to));

        return new MetricsDashboard(
            filter,
            from,
            to,
            granularity,
            BuildOptions(all),
            SeriesPalette.Assign(all.Select(a => a.Slug)),
            active.Count,
            new SeverityChips(Sev(Severity.Critica), Sev(Severity.Alta), Sev(Severity.Media), Sev(Severity.Baja)),
            resolvedNow,
            resolvedBefore,
            cost,
            costUnit,
            cost is { } c && unitsAudited > 0 ? c / unitsAudited : null,
            unitsAudited,
            cycleAudited,
            cyclePending,
            cycleLarge,
            series,
            hasOthers,
            all.ToDictionary(a => a.Slug, a => a.Name, StringComparer.OrdinalIgnoreCase),
            Points(scope, buckets, series, hasOthers, (app, bFrom, bTo) => app.Sessions
                .Where(s => s.StartedUtc >= bFrom && s.StartedUtc < bTo && s.Usage.Cost is not null)
                .Sum(s => s.Usage.Cost ?? 0m)),
            resSeries,
            resHasOthers,
            Points(scope, buckets, resSeries, resHasOthers, (app, bFrom, bTo) => app.Findings
                .SelectMany(ResolutionEvents)
                .Count(utc => utc >= bFrom && utc < bTo)),
            donuts.OrderByDescending(d => d.Total).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            FlowBuckets(findings, buckets),
            SessionRows(scope, inPeriod));
    }

    // ---------- El periodo y sus cubos ----------

    /// <summary>
    /// De cuándo a cuándo. «Todo» empieza en el dato más antiguo que haya, no en una fecha
    /// inventada; si no hay ninguno se comporta como ocho semanas para no dibujar un eje
    /// degenerado.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset To) Period(
        MetricsRange range, IReadOnlyList<AppData> scope, DateTimeOffset now)
    {
        DateTimeOffset to = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
        int weeks = range switch
        {
            MetricsRange.Weeks4 => 4,
            MetricsRange.Weeks26 => 26,
            _ => 8,
        };

        if (range != MetricsRange.All)
        {
            return (to.AddDays(-7 * weeks), to);
        }

        var stamps = scope
            .SelectMany(a => a.Findings.Select(f => f.FirstDetected.Utc)
                .Concat(a.Sessions.Select(s => s.StartedUtc)))
            .ToList();

        DateTimeOffset start = stamps.Count == 0 ? to.AddDays(-56) : stamps.Min();
        start = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
        return (start >= to ? to.AddDays(-56) : start, to);
    }

    /// <summary>
    /// Rango corto, un punto por día; rango largo, por semana; «todo» de más de un año, por mes.
    /// Un eje con doscientas marcas no es más información: es menos.
    /// </summary>
    internal static MetricsGranularity GranularityFor(MetricsRange range, DateTimeOffset from, DateTimeOffset to)
    {
        if (range == MetricsRange.Weeks4)
        {
            return MetricsGranularity.Diaria;
        }

        return (to - from).TotalDays > 371 ? MetricsGranularity.Mensual : MetricsGranularity.Semanal;
    }

    /// <summary>Los cubos del eje X, del más antiguo al más reciente.</summary>
    internal static IReadOnlyList<(DateTimeOffset From, DateTimeOffset To, string Label)> Buckets(
        DateTimeOffset from, DateTimeOffset to, MetricsGranularity granularity)
    {
        var result = new List<(DateTimeOffset, DateTimeOffset, string)>();
        DateTimeOffset cursor = from;
        while (cursor < to && result.Count < 400)
        {
            DateTimeOffset next = granularity switch
            {
                MetricsGranularity.Diaria => cursor.AddDays(1),
                MetricsGranularity.Mensual => cursor.AddMonths(1),
                _ => cursor.AddDays(7),
            };

            string label = granularity == MetricsGranularity.Mensual
                ? cursor.ToLocalTime().ToString("MMM yy")
                : cursor.ToLocalTime().ToString("d MMM");

            result.Add((cursor, next > to ? to : next, label));
            cursor = next;
        }

        return result;
    }

    // ---------- Gráficas 1 y 2: línea por aplicación sobre el eje temporal ----------

    /// <summary>
    /// Qué aplicaciones se dibujan con nombre propio: las seis que más aportaron en el periodo.
    /// El resto se suma en «Otras». La ELECCIÓN depende del periodo, pero el COLOR de cada app no
    /// (sale del reparto del portafolio), así que ninguna app cambia de color al mover un filtro
    /// — ni al pasar de una gráfica a la otra.
    /// <para>
    /// El total de cada app lo aporta quien llama: para el coste es lo consumido, para las
    /// resoluciones es cuántas hubo. El criterio de «quién sale con nombre» es el mismo, así que
    /// las dos gráficas no pueden discrepar en cómo agrupan.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<string> Series, bool HasOthers) TopSeries(
        IReadOnlyList<AppData> scope, Func<AppData, decimal> totalOf)
    {
        var totals = scope
            .Select(a => (a.Slug, Total: totalOf(a)))
            .Where(t => t.Total > 0)
            .OrderByDescending(t => t.Total)
            .ThenBy(t => t.Slug, StringComparer.Ordinal)
            .ToList();

        var named = totals.Take(SeriesPalette.MaxNamedSeries).Select(t => t.Slug).ToList();
        bool others = totals.Count > SeriesPalette.MaxNamedSeries;
        if (others)
        {
            named.Add(MetricsDashboard.OthersSlug);
        }

        return (named, others);
    }

    /// <summary>
    /// Reparte por cubo y por serie lo que <paramref name="valueOf"/> mida en cada tramo. Es la
    /// mecánica compartida de las dos gráficas de línea: los cubos, el agrupado en «Otras» y el
    /// descarte de lo que no cabe en ninguna serie se hacen UNA vez.
    /// </summary>
    private static IReadOnlyList<SeriesPoint> Points(
        IReadOnlyList<AppData> scope,
        IReadOnlyList<(DateTimeOffset From, DateTimeOffset To, string Label)> buckets,
        IReadOnlyList<string> series,
        bool hasOthers,
        Func<AppData, DateTimeOffset, DateTimeOffset, decimal> valueOf)
    {
        var named = series
            .Where(s => s != MetricsDashboard.OthersSlug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var points = new List<SeriesPoint>(buckets.Count);

        foreach ((DateTimeOffset bFrom, DateTimeOffset bTo, string label) in buckets)
        {
            var byApp = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (AppData app in scope)
            {
                decimal value = valueOf(app, bFrom, bTo);
                if (value == 0m)
                {
                    continue;
                }

                bool isNamed = named.Contains(app.Slug);
                if (!isNamed && !hasOthers)
                {
                    continue;
                }

                string key = isNamed ? app.Slug : MetricsDashboard.OthersSlug;
                byApp[key] = byApp.TryGetValue(key, out decimal had) ? had + value : value;
            }

            points.Add(new SeriesPoint(bFrom, label, byApp));
        }

        return points;
    }

    /// <summary>
    /// Las fechas en que este hallazgo pasó a <c>Resuelto</c>. Pueden ser VARIAS: un hallazgo que
    /// se resolvió, se reabrió y se volvió a resolver saldó deuda dos veces, y la gráfica de
    /// resoluciones cuenta eventos, no el neto (el neto ya lo da el burndown del flujo). Por eso
    /// tampoco se filtra por vía: el veredicto del auditor, la resolución manual y la medida
    /// cuentan igual — es deuda saldada, venga de donde venga.
    /// <para>
    /// La fuente es el HISTORIAL, que es lo único que conserva las resoluciones anteriores a una
    /// reapertura (<see cref="Finding.Resolved"/> se pone a null al reabrir). El sello se usa solo
    /// de reserva, para hallazgos importados sin historial: sin esa reserva, un hub traído de V4
    /// dibujaría una gráfica vacía teniendo resoluciones.
    /// </para>
    /// </summary>
    internal static IEnumerable<DateTimeOffset> ResolutionEvents(Finding finding)
    {
        bool any = false;
        foreach (HistoryEntry entry in finding.History)
        {
            if (entry.Event == FindingEvent.Resolved)
            {
                any = true;
                yield return entry.Utc;
            }
        }

        if (!any && finding.Resolved is { } stamp)
        {
            yield return stamp.Utc;
        }
    }

    // ---------- Gráfica 4: flujo de hallazgos ----------

    /// <summary>
    /// El burndown de verdad: lo que entró, lo que se cerró, y cuantos quedaban vivos al final de
    /// cada cubo. Los activos NO son «el estado de hoy repetido»: se reconstruyen a esa fecha
    /// (creado antes del corte y no resuelto todavia), que es lo único que responde a «la deuda,
    /// baja o sube?».
    /// </summary>
    internal static IReadOnlyList<FlowBucket> FlowBuckets(
        IReadOnlyList<Finding> findings,
        IReadOnlyList<(DateTimeOffset From, DateTimeOffset To, string Label)> buckets)
    {
        var result = new List<FlowBucket>(buckets.Count);
        foreach ((DateTimeOffset from, DateTimeOffset to, string label) in buckets)
        {
            int created = findings.Count(f => f.FirstDetected.Utc >= from && f.FirstDetected.Utc < to);
            int closed = findings.Count(f => ResolvedIn(f, from, to));
            int alive = findings.Count(f => f.FirstDetected.Utc < to
                                            && (f.Resolved is null || f.Resolved.Utc >= to));
            result.Add(new FlowBucket(label, created, closed, alive));
        }

        return result;
    }

    private static bool ResolvedIn(Finding f, DateTimeOffset from, DateTimeOffset to)
        => f.Resolved is { } r && r.Utc >= from && r.Utc < to;

    // ---------- Gráfica 5: actividad de sesiones ----------

    private static IReadOnlyList<SessionRow> SessionRows(
        IReadOnlyList<AppData> scope, IReadOnlyList<AuditSession> inPeriod)
    {
        var names = scope.ToDictionary(a => a.Slug, a => a.Name, StringComparer.OrdinalIgnoreCase);
        return inPeriod
            .OrderByDescending(s => s.StartedUtc)
            .Take(MaxSessionRows)
            .Select(s => new SessionRow(
                s.Id.ToString(),
                s.AppSlug,
                names.TryGetValue(s.AppSlug, out string? n) ? n : s.AppSlug,
                s.StartedUtc,
                s.By,
                s.Mode,
                s.Units.Count,
                s.Counters.New,
                s.Counters.Resolved,
                s.Usage.Cost,
                string.IsNullOrWhiteSpace(s.Usage.Currency) ? CostEstimator.DefaultCostUnit : s.Usage.Currency!))
            .ToList();
    }

    // ---------- Lectura y caché ----------

    private static IReadOnlyList<AppOption> BuildOptions(IReadOnlyList<AppData> all)
    {
        var options = new List<AppOption> { new(null, "Todas las aplicaciones") };
        options.AddRange(all
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AppOption(a.Slug, a.Name)));
        return options;
    }

    private IReadOnlyList<AppData> Snapshot()
    {
        lock (_gate)
        {
            if (_cache is not null)
            {
                return _cache;
            }
        }

        var read = new List<AppData>();
        foreach (string slug in _hub.Store.ListAppSlugs())
        {
            AppConfig? app = _hub.Store.TryReadApp(slug);
            if (app is null)
            {
                continue;
            }

            read.Add(new AppData(
                slug,
                string.IsNullOrWhiteSpace(app.Name) ? slug : app.Name,
                app.CurrentCycle,
                _hub.Store.ListFindings(slug),
                _hub.Store.ListSessions(slug),
                _hub.Store.TryReadInventory(slug, app.CurrentCycle)));
        }

        lock (_gate)
        {
            _cache = read;
            return _cache;
        }
    }
}
