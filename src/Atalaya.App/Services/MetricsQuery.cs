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
public sealed record SeverityChips(int Critica, int Alta, int Media, int Baja)
{
    /// <summary>Cuántos hay de esa severidad. Evita el <c>switch</c> repetido en cada consumidor.</summary>
    public int Of(Severity severity) => severity switch
    {
        Severity.Critica => Critica,
        Severity.Alta => Alta,
        Severity.Media => Media,
        _ => Baja,
    };

    public int Total => Critica + Alta + Media + Baja;
}

/// <summary>
/// El rosco de severidad de una aplicación (F6.5): cómo se reparte su deuda VIVA.
/// <para>
/// Cuenta solo los hallazgos <b>activos</b>. Los resueltos y los silenciados no son deuda —uno
/// se arregló y del otro se decidió que no se arregla—, así que sumarlos aquí convertiría la
/// foto de lo que queda por hacer en un histórico de todo lo que hubo.
/// </para>
/// </summary>
public sealed record SeverityDonut(string Slug, string Name, SeverityChips Active)
{
    public int Total => Active.Total;

    /// <summary>
    /// Una app sin activos NO se omite de la fila: se dibuja vacía. Que una aplicación esté
    /// limpia es un dato, y esconderla la haría indistinguible de una que nadie ha auditado.
    /// </summary>
    public bool HasData => Total > 0;

    public int Of(Severity severity) => Active.Of(severity);
}

/// <summary>
/// Un punto del eje X: su etiqueta y lo que aportó cada app en el.
/// <para>
/// El punto NO sabe qué mide. Nació para el coste y hoy lo comparten la gráfica de coste y la de
/// resoluciones (F6.1): las dos son «línea por aplicación sobre el mismo eje temporal», y la
/// única diferencia entre ellas es de dónde sale el número de cada cubo. Duplicar el tipo habría
/// duplicado también los cubos, el reparto de «Otras» y el acumulado.
/// </para>
/// </summary>
/// <param name="Range">
/// El tramo COMPLETO que resume el punto, escrito («22–28 ago»). El eje no cabe repitiendo esto
/// en cada marca, así que la etiqueta corta va al eje y el tramo entero al tooltip: sin él, un
/// cubo semanal etiquetado con un solo día se lee como ese día — que es exactamente lo que pasó
/// el 28/08/2026, cuando dos resoluciones de ese día se leyeron como actividad del 22.
/// </param>
public sealed record SeriesPoint(
    DateTimeOffset From, string Label, string Range, IReadOnlyDictionary<string, decimal> ByApp)
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
public sealed record FlowBucket(string Label, string Range, int New, int Resolved, int ActiveAtEnd);

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

/// <summary>
/// Lo que costó UN proveedor en el periodo, en SU unidad (F14).
/// <para>
/// Existe porque los dos proveedores no cuentan lo mismo: Copilot factura peticiones premium con
/// multiplicador y Claude Code informa dólares de tarifa de lista que su suscripción no cobra por
/// llamada. Sumarlos daría un número que no significa nada y que además parecería dinero. Así que
/// no se suman: se enseñan uno al lado del otro, cada uno con su unidad pegada.
/// </para>
/// </summary>
public sealed record ProviderCost(
    string ProviderId, string ProviderName, decimal? Cost, string CostUnit, int Sessions)
{
    /// <summary>
    /// El coste de esta casa es un EQUIVALENTE y no una factura (F15). Con suscripción no se paga
    /// por tokens, así que llamarlo «lo que costó» sería decir que se cobró algo que no se cobró.
    /// </summary>
    public bool IsSubscription => CreditText.IsSubscription(ProviderId);

    /// <summary>La línea que se lee en el panel: «GitHub Copilot · 68,2 AI credits».</summary>
    public string Line => Cost is { } c
        ? $"{ProviderName} · {CreditText.Number(c)} {CostUnit}"
        // Las que no se pueden valorar no llegan aquí: el agregado las deja fuera y las cuenta
        // aparte como «parcial», con su motivo (D-787). Esta rama es la red por si alguna vez sí.
        : $"{ProviderName} · coste no calculable";
}

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
    IReadOnlyList<ProviderCost> CostByProvider,
    int PartialCostSessions,
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
    IReadOnlyList<SeverityDonut> Severity,
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

    /// <summary>
    /// En el periodo conviven una FACTURA y un EQUIVALENTE (F15), así que no se ofrece un total
    /// único: sumar lo que se paga con lo que no se paga daría una cifra que parece un gasto y no
    /// lo es. Las dos están en credits —la aritmética sí valdría—, pero lo que no se puede mezclar
    /// en silencio es su significado, así que el panel enseña el desglose con sus etiquetas.
    /// </summary>
    public bool CostIsMixed
        => CostByProvider.Any(p => p.IsSubscription) && CostByProvider.Any(p => !p.IsSubscription);

    /// <summary>
    /// Hay sesiones con tokens que NO se han podido valorar —sin modelo registrado, o con un
    /// modelo sin tarifa—, así que lo que se enseña es menos que lo que se gastó. Se DICE: un total
    /// al que le falta gasto se lee como si fuera el gasto entero.
    /// </summary>
    public bool CostIsPartial => PartialCostSessions > 0;

    /// <summary>La frase del parcial, con el número, para poder actuar sobre ella.</summary>
    public string PartialCostNotice => PartialCostSessions == 1
        ? "1 sesión sin tarifa para su modelo: no está contada."
        : $"{PartialCostSessions} sesiones sin tarifa para su modelo: no están contadas.";

    /// <summary>
    /// El coste, listo para leer. Con un solo proveedor es el total de siempre; con varios son sus
    /// líneas, una por casa. Nunca una suma de unidades distintas.
    /// </summary>
    public IReadOnlyList<string> CostLines => CostByProvider.Select(p => p.Line).ToList();

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

    /// <summary>La huella del hub con la que se leyó <see cref="_cache"/>. Ver <see cref="Fingerprint"/>.</summary>
    private string? _stamp;

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
            _stamp = null;
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
        (DateTime fromLocal, DateTime toLocal) = Period(filter.Range, scope, now);
        DateTimeOffset from = Instant(fromLocal);
        DateTimeOffset to = Instant(toLocal);
        MetricsGranularity granularity = GranularityFor(filter.Range, from, to);
        IReadOnlyList<Bucket> buckets = Buckets(fromLocal, toLocal, granularity);

        var findings = scope.SelectMany(a => a.Findings).ToList();
        var sessions = scope.SelectMany(a => a.Sessions).ToList();
        var inPeriod = sessions.Where(s => s.StartedUtc >= from && s.StartedUtc < to).ToList();

        var active = findings.Where(f => f.Status == FindingStatus.Activo).ToList();
        int Sev(Severity s) => active.Count(f => f.Severity == s);

        // El tile cuenta EVENTOS de resolución, igual que la gráfica y que el burndown. Antes
        // leía el sello `resolved` del hallazgo, que se pone a null al reabrir: la misma pregunta
        // —«cuánto se resolvió en el periodo»— tenía tres respuestas distintas en la misma vista.
        int resolvedNow = findings.Sum(f => ResolutionsIn(f, from, to));
        TimeSpan span = to - from;
        int resolvedBefore = findings.Sum(f => ResolutionsIn(f, from - span, from));

        // TODA sesión con coste cuenta: auditoría, arreglo, verificación y lo que venga. El tile
        // y la gráfica salen de la MISMA función (CostIn), no de dos sumas parecidas.
        //
        // F15 — ahora las dos casas se miden en la MISMA unidad (credits, derivados de tokens), así
        // que ya se pueden sumar sin mentir en la aritmética. Lo que sigue sin poder mezclarse en
        // silencio es lo que significan: el de Copilot es una FACTURA y el de Claude Code con
        // suscripción es un EQUIVALENTE. Por eso el desglose por proveedor se mantiene y el total
        // único solo se ofrece cuando todo lo del periodo es de la misma naturaleza.
        ModelRateTable? rates = ModelRates();
        IReadOnlyList<ProviderCost> byProvider = CostByProvider(inPeriod, rates);

        bool mixesKinds = byProvider.Any(p => p.IsSubscription) && byProvider.Any(p => !p.IsSubscription);

        decimal? cost = byProvider.Count > 0 && !mixesKinds
            ? scope.Sum(a => CostIn(a.Sessions, from, to, rates))
            : null;
        int unitsAudited = inPeriod.Sum(s => s.Units.Count);

        // Cuántas sesiones del periodo tenían tokens pero NO se pudieron valorar: sin modelo
        // registrado, o con un modelo sin tarifa. El agregado que las contiene es PARCIAL, y hay
        // que decirlo — un total al que le falta gasto se lee como si fuera el gasto entero.
        int partial = inPeriod.Count(x => !CreditCalculator.Calculate(x, rates).HasValue && HasTokens(x));

        // El ratio «por unidad auditada» NO divide el gasto entero: divide lo que costó AUDITAR.
        // Un arreglo o una verificación no auditan ninguna unidad, así que su gasto subía el
        // ratio sin que cambiara nada de lo auditado (28/08/2026: 262,5 por unidad cuando auditar
        // esa unidad había costado 105). El tile de coste los sigue sumando —eso es el gasto—;
        // lo que no se puede es repartirlos entre algo que no produjeron.
        decimal auditCost = mixesKinds
            ? 0m
            : inPeriod.Where(x => x.Units.Count > 0).Sum(x => CostOf(x, rates));
        string costUnit = CreditText.Unit;

        int cycleAudited = 0;
        int cyclePending = 0;
        int cycleLarge = 0;
        var donuts = new List<CoverageDonut>();
        var severities = new List<SeverityDonut>();
        foreach (AppData app in scope)
        {
            // El reparto por severidad NO mira el periodo: los activos son la foto de HOY, igual
            // que el tile de arriba. Recortarlos por el rango daría una deuda más pequeña que la
            // real cada vez que alguien eligiera «4 semanas».
            var live = app.Findings.Where(f => f.Status == FindingStatus.Activo).ToList();
            severities.Add(new SeverityDonut(app.Slug, app.Name, new SeverityChips(
                live.Count(f => f.Severity == Severity.Critica),
                live.Count(f => f.Severity == Severity.Alta),
                live.Count(f => f.Severity == Severity.Media),
                live.Count(f => f.Severity == Severity.Baja))));

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

        (IReadOnlyList<string> series, bool hasOthers) = TopSeries(scope, a => CostIn(a.Sessions, from, to, rates));

        (IReadOnlyList<string> resSeries, bool resHasOthers) = TopSeries(
            scope, a => a.Findings.Sum(f => ResolutionsIn(f, from, to)));

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
            unitsAudited > 0 && auditCost > 0m ? auditCost / unitsAudited : null,
            byProvider,
            partial,
            unitsAudited,
            cycleAudited,
            cyclePending,
            cycleLarge,
            series,
            hasOthers,
            all.ToDictionary(a => a.Slug, a => a.Name, StringComparer.OrdinalIgnoreCase),
            Points(scope, buckets, series, hasOthers, (app, bFrom, bTo) => CostIn(app.Sessions, bFrom, bTo, rates)),
            resSeries,
            resHasOthers,
            Points(scope, buckets, resSeries, resHasOthers,
                (app, bFrom, bTo) => app.Findings.Sum(f => ResolutionsIn(f, bFrom, bTo))),
            donuts.OrderByDescending(d => d.Total).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            severities.OrderByDescending(d => d.Total).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            FlowBuckets(findings, buckets),
            SessionRows(scope, inPeriod, rates));
    }

    // ---------- El periodo y sus cubos ----------

    /// <summary>
    /// De cuándo a cuándo, en DÍAS LOCALES. «Todo» empieza en el dato más antiguo que haya, no en
    /// una fecha inventada; si no hay ninguno se comporta como ocho semanas para no dibujar un eje
    /// degenerado.
    /// <para>
    /// <b>Por qué locales y no UTC.</b> En disco todo es UTC, y así se compara; pero los cubos y
    /// sus etiquetas se leen en la hora del usuario. Cortando por medianoche UTC, en UTC+2 el cubo
    /// rotulado «28 ago» iba en realidad del 28 a las 02:00 al 29 a las 02:00: cualquier cosa
    /// hecha entre las 00:00 y las 02:00 del 28 caía —y se dibujaba— en el 27. El corte se hace
    /// donde está escrita la etiqueta.
    /// </para>
    /// <para>
    /// El extremo derecho es SIEMPRE la medianoche de mañana: el eje llega a hoy aunque el último
    /// cubo esté a cero. Un eje que termina en el pasado dice que no ha pasado nada desde
    /// entonces, y eso es una afirmación, no una ausencia de dato.
    /// </para>
    /// </summary>
    private static (DateTime From, DateTime To) Period(
        MetricsRange range, IReadOnlyList<AppData> scope, DateTimeOffset now)
    {
        DateTime to = Local(now).Date.AddDays(1);
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
                .Concat(a.Findings.SelectMany(ResolutionEvents))
                .Concat(a.Sessions.Select(s => s.StartedUtc)))
            .ToList();

        DateTime start = stamps.Count == 0 ? to.AddDays(-56) : Local(stamps.Min()).Date;
        return (start >= to ? to.AddDays(-56) : start, to);
    }

    /// <summary>La misma fecha, leída en la zona del usuario. Un solo sitio hace la conversión.</summary>
    private static DateTime Local(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime;

    /// <summary>
    /// El instante en que empieza esa fecha local. Es la frontera con la que se compara contra los
    /// sellos UTC del disco, para que el cubo cubra exactamente lo que su etiqueta dice.
    /// </summary>
    internal static DateTimeOffset Instant(DateTime localDate)
        => new(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));

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

    /// <summary>
    /// Un cubo del eje X: el tramo <c>[From, To)</c> que agrega y las dos formas de escribirlo.
    /// </summary>
    /// <param name="Label">Lo que cabe en el eje. Corto por obligación.</param>
    /// <param name="Range">El tramo entero, para el tooltip. Ver <see cref="SeriesPoint.Range"/>.</param>
    internal sealed record Bucket(DateTimeOffset From, DateTimeOffset To, string Label, string Range);

    /// <summary>
    /// Los cubos del eje X, del más antiguo al más reciente, sobre fechas LOCALES.
    /// <para>
    /// <b>Un cubo semanal se rotula por su ÚLTIMO día, no por el primero.</b> El último va de hoy
    /// hacia atrás —del 22 al 28 de agosto si hoy es 28—, y rotulándolo por su inicio el eje
    /// terminaba en «22 ago»: el día de hoy no aparecía por ninguna parte y lo hecho hoy se leía
    /// como actividad de hace seis días. Rotulado por el final, el último cubo dice «28 ago» —hoy—
    /// y el eje termina donde termina el tiempo. El tramo completo va en <see cref="Bucket.Range"/>
    /// para que ni siquiera esa lectura quede a interpretación.
    /// </para>
    /// <para>
    /// El cubo mensual sigue rotulándose por su mes («ago 26»): ahí no hay ambigüedad que
    /// resolver, y un «31 ago» en el eje se leería como un día.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<Bucket> Buckets(
        DateTime from, DateTime to, MetricsGranularity granularity)
    {
        var result = new List<Bucket>();
        DateTime cursor = from;
        while (cursor < to && result.Count < 400)
        {
            DateTime next = granularity switch
            {
                MetricsGranularity.Diaria => cursor.AddDays(1),
                MetricsGranularity.Mensual => cursor.AddMonths(1),
                _ => cursor.AddDays(7),
            };

            DateTime end = next > to ? to : next;

            // El último día INCLUIDO en el cubo: el extremo derecho es abierto, así que el 29 a
            // las 00:00 cierra el cubo del 28. Rotular con el extremo diría un día que no cuenta.
            DateTime last = end.AddDays(-1);
            string label = granularity switch
            {
                MetricsGranularity.Mensual => cursor.ToString("MMM yy"),
                MetricsGranularity.Diaria => cursor.ToString("d MMM"),
                _ => last.ToString("d MMM"),
            };

            result.Add(new Bucket(Instant(cursor), Instant(end), label, RangeText(cursor, last)));
            cursor = next;
        }

        return result;
    }

    /// <summary>
    /// Cómo se escribe un tramo: «28 ago» si es un día, «22–28 ago» dentro del mismo mes y
    /// «28 jul–3 ago» cuando lo cruza. Es lo que hace que un cubo semanal no se pueda confundir
    /// con el día que lo rotula.
    /// </summary>
    internal static string RangeText(DateTime first, DateTime last)
    {
        if (first.Date >= last.Date)
        {
            return first.ToString("d MMM");
        }

        return first.Year == last.Year && first.Month == last.Month
            ? $"{first:%d}–{last:d MMM}"
            : $"{first:d MMM}–{last:d MMM}";
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
    /// <summary>
    /// Agrupa las sesiones del periodo por proveedor, cada una con SU unidad de coste (F14).
    /// <para>
    /// Las sesiones anteriores a F14 no llevan proveedor escrito, y eso NO es un dato que falte:
    /// era Copilot, porque no había otro. Se les asigna esa casa en vez de inventar una categoría
    /// «desconocido» que solo conseguiría partir el histórico en dos.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ProviderCost> CostByProvider(
        IReadOnlyList<AuditSession> sessions, ModelRateTable? rates)
        => sessions
            .Select(s => (Session: s, Cost: CreditCalculator.Calculate(s, rates)))
            .Where(x => x.Cost.HasValue)
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Session.Provider) ? LegacyProviderId : x.Session.Provider!,
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProviderCost(
                g.Key,
                ProviderDisplayName(g.Key),
                g.Sum(x => x.Cost.Credits ?? 0m),
                CreditText.LabelFor(g.Key),
                g.Count()))
            .OrderBy(p => p.ProviderName, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>¿Guardó esta sesión tokens? Sin ellos no hay nada que valorar, y no es «parcial».</summary>
    private static bool HasTokens(AuditSession s)
        => s.Usage.InputTokens > 0 || s.Usage.OutputTokens > 0
        || s.Usage.CacheReadTokens > 0 || s.Usage.CacheWriteTokens > 0;

    /// <summary>Las tarifas del hub, o null si no hay o no se pueden leer.</summary>
    private ModelRateTable? ModelRates()
    {
        try
        {
            return _hub.Store.TryReadModelRates();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Lo que era toda sesión antes de que hubiera un segundo proveedor.</summary>
    private const string LegacyProviderId = "copilot";

    /// <summary>
    /// El nombre legible de un identificador guardado. Se resuelve aquí, con una tabla mínima, y no
    /// preguntándole al registro de proveedores: Métricas lee sesiones de hace meses y tiene que
    /// poder nombrar una casa aunque esta versión ya no la traiga. Lo que no conoce lo enseña tal
    /// cual, que es más honesto que dejarlo en blanco.
    /// </summary>
    private static string ProviderDisplayName(string providerId) => providerId.ToLowerInvariant() switch
    {
        "copilot" => "GitHub Copilot",
        "claude-code" => "Claude Code",
        _ => providerId,
    };

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
        IReadOnlyList<Bucket> buckets,
        IReadOnlyList<string> series,
        bool hasOthers,
        Func<AppData, DateTimeOffset, DateTimeOffset, decimal> valueOf)
    {
        var named = series
            .Where(s => s != MetricsDashboard.OthersSlug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var points = new List<SeriesPoint>(buckets.Count);

        foreach ((DateTimeOffset bFrom, DateTimeOffset bTo, string label, string range) in buckets)
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

            points.Add(new SeriesPoint(bFrom, label, range, byApp));
        }

        return points;
    }

    // ---------- El coste: una sola función para el tile y para la gráfica ----------

    /// <summary>
    /// Lo que costó una sesión. <b>Toda</b> sesión cuenta —auditoría, arreglo, verificación y lo
    /// que venga—: el gasto es el gasto, y filtrar por modo aquí dejaría fuera precisamente lo que
    /// se ha empezado a gastar después (H9 trajo los arreglos, el verify en dos fases las
    /// verificaciones). Una sesión sin coste declarado aporta cero, que no es lo mismo que «no se
    /// sabe» — esa distinción la lleva quien pregunta si HAY coste, no esta suma.
    /// </summary>
    internal static decimal CostOf(AuditSession session) => CostOf(session, null);

    /// <inheritdoc cref="CostOf(AuditSession)"/>
    /// <remarks>
    /// F15 — el coste se DERIVA de los tokens con la tarifa del modelo de la sesión; ya no se lee
    /// el número que guardó el proveedor. Aquel estaba en premium requests, la unidad que GitHub
    /// retiró el 1 de junio de 2026. Los tokens, en cambio, son el hecho primario y siguen en el
    /// hub, así que el histórico se recalcula entero sin migrar un solo fichero.
    /// <para>
    /// Una sesión cuyo coste no se puede derivar —sin tokens, sin modelo o sin tarifa— aporta cero
    /// a la SUMA y se cuenta aparte como parcial. La distinción entre «costó cero» y «no se sabe»
    /// la lleva quien pregunta, no esta función.
    /// </para>
    /// </remarks>
    internal static decimal CostOf(AuditSession session, ModelRateTable? rates)
        => CreditCalculator.Calculate(session, rates).Credits ?? 0m;

    /// <summary>
    /// El coste de las sesiones que arrancaron dentro del tramo. El tile de «Coste del periodo» y
    /// cada punto de la gráfica salen de AQUÍ, no de dos sumas parecidas: cuando el tile y la
    /// gráfica hacen su propia cuenta, tarde o temprano una de las dos se queda sin actualizar
    /// (nos pasó dos veces). Una sesión sin modo reconocible se cuenta igual: la que se saltaría
    /// es la única que no se podría explicar.
    /// </summary>
    internal static decimal CostIn(
        IEnumerable<AuditSession> sessions, DateTimeOffset from, DateTimeOffset to, ModelRateTable? rates = null)
        => sessions.Where(s => s.StartedUtc >= from && s.StartedUtc < to).Sum(x => CostOf(x, rates));

    // ---------- Las resoluciones ----------

    /// <summary>
    /// Cuántas veces se resolvió este hallazgo dentro del tramo. Es la ÚNICA cuenta de
    /// resoluciones de la vista: la usan el tile, la gráfica y el burndown, para que la misma
    /// pregunta no tenga tres respuestas.
    /// </summary>
    internal static int ResolutionsIn(Finding finding, DateTimeOffset from, DateTimeOffset to)
        => ResolutionEvents(finding).Count(utc => utc >= from && utc < to);

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
        IReadOnlyList<Bucket> buckets)
    {
        var result = new List<FlowBucket>(buckets.Count);
        foreach ((DateTimeOffset from, DateTimeOffset to, string label, string range) in buckets)
        {
            int created = findings.Count(f => f.FirstDetected.Utc >= from && f.FirstDetected.Utc < to);
            int closed = findings.Sum(f => ResolutionsIn(f, from, to));
            int alive = findings.Count(f => AliveAt(f, to));
            result.Add(new FlowBucket(label, range, created, closed, alive));
        }

        return result;
    }

    /// <summary>
    /// Si el hallazgo seguía siendo deuda VIVA en ese instante, reconstruido de su historial.
    /// <para>
    /// Antes se miraba el sello <c>resolved</c> del hallazgo de hoy, y eso contaba mal dos casos
    /// que existen de verdad: un hallazgo reabierto pierde el sello —quedaba «vivo» también
    /// durante el tramo en que estuvo cerrado— y uno SILENCIADO nunca lo tiene, así que engordaba
    /// el burndown como deuda pendiente mientras el rosco de severidad, que solo cuenta activos,
    /// lo daba por fuera. La misma app enseñaba dos deudas distintas en la misma pantalla.
    /// </para>
    /// <para>
    /// Sin historial —hallazgos traídos de V4— se cae al estado de hoy con su sello: es lo único
    /// que hay, y es mejor que declarar viva una deuda que consta cerrada.
    /// </para>
    /// </summary>
    internal static bool AliveAt(Finding finding, DateTimeOffset at)
    {
        if (finding.FirstDetected.Utc >= at)
        {
            return false;
        }

        var changes = finding.History
            .Where(e => e.Utc < at)
            .Select(e => (e.Utc, State: StateOf(e.Event)))
            .Where(e => e.State is not null)
            .ToList();

        if (changes.Count == 0)
        {
            return StoredAliveAt(finding, at);
        }

        DateTimeOffset last = changes.Max(e => e.Utc);
        var latest = changes.Where(e => e.Utc == last).Select(e => e.State!.Value).Distinct().ToList();

        // Un historial puede traer DOS eventos contradictorios con el mismo sello, y los hay en el
        // hub: MEJ-0037 se resolvió y se reabrió en el mismo instante, y su ficha quedó guardada
        // como «resuelto». Cuando el historial no puede desempatarse solo, manda el estado
        // guardado — que es el que ya enseñan la lista de hallazgos y el rosco de severidad. No se
        // toca el fichero: se lee con un criterio, y el criterio es no contradecir al resto de la
        // aplicación sobre la misma ficha.
        return latest.Count == 1 ? latest[0] : finding.Status == FindingStatus.Activo;
    }

    /// <summary>Si el evento abre deuda (<c>true</c>), la cierra (<c>false</c>) o no la toca.</summary>
    private static bool? StateOf(FindingEvent kind) => kind switch
    {
        FindingEvent.Resolved or FindingEvent.Silenced => false,
        FindingEvent.Reopened or FindingEvent.Unsilenced => true,
        _ => null,
    };

    /// <summary>
    /// El respaldo para hallazgos sin historial de estado —los traídos de V4—: su estado de hoy
    /// con su sello. Es lo único que hay, y es mejor que declarar viva una deuda que consta
    /// cerrada.
    /// </summary>
    private static bool StoredAliveAt(Finding finding, DateTimeOffset at) => finding.Status switch
    {
        FindingStatus.Resuelto => finding.Resolved is not { } r || r.Utc >= at,
        FindingStatus.Silenciado => false,
        _ => true,
    };

    // ---------- Gráfica 5: actividad de sesiones ----------

    private static IReadOnlyList<SessionRow> SessionRows(
        IReadOnlyList<AppData> scope, IReadOnlyList<AuditSession> inPeriod, ModelRateTable? rates)
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
                // F15 — el coste de la fila se DERIVA como el del tile: misma aritmética, misma
                // tarifa, mismo modelo. Dos cuentas parecidas para el mismo número acaban siempre
                // discrepando (ya pasó dos veces con el tile y la gráfica).
                CreditCalculator.Calculate(s, rates).Credits,
                CreditText.LabelFor(s.Provider)))
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
        string stamp = Fingerprint();
        lock (_gate)
        {
            if (_cache is not null && _stamp == stamp)
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
            _stamp = stamp;
            _cache = read;
            return _cache;
        }
    }

    /// <summary>
    /// La huella barata del hub: cuántos ficheros primarios hay y cuándo se tocó el último. No
    /// abre ninguno —solo pregunta al directorio—, así que cuesta una fracción de lo que cuesta
    /// releerlos, y es lo que hace que una sesión recién terminada aparezca al volver a la vista.
    /// <para>
    /// La caché se invalidaba SOLO con el evento de sync, y ese evento lo levanta un <b>pull</b>
    /// del remoto. Todo lo que escribe esta máquina —una auditoría, un arreglo, una verificación—
    /// no pasa por ahí: el panel seguía enseñando la foto anterior hasta reiniciar la aplicación,
    /// y era justo la sesión que el usuario acababa de terminar la que faltaba. Se mira el DISCO y
    /// no una lista de escritores porque la lista es lo que se queda sin actualizar (D-239): esto
    /// funciona igual para el escritor que se añada mañana.
    /// </para>
    /// <para>
    /// Si el directorio se mueve bajo los pies mientras se recorre, se devuelve una huella nueva:
    /// releer de más cuesta una carga; servir una foto vieja se ve en pantalla.
    /// </para>
    /// </summary>
    private string Fingerprint()
    {
        string apps = _hub.HubPaths.AppsDir;
        if (!Directory.Exists(apps))
        {
            return "vacío";
        }

        try
        {
            long count = 0;
            long newest = 0;
            foreach (string file in Directory.EnumerateFiles(apps, "*.json", SearchOption.AllDirectories))
            {
                count++;
                long ticks = File.GetLastWriteTimeUtc(file).Ticks;
                if (ticks > newest)
                {
                    newest = ticks;
                }
            }

            return $"{count}:{newest}";
        }
        catch (IOException)
        {
            return Guid.NewGuid().ToString("N");
        }
        catch (UnauthorizedAccessException)
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
