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

/// <summary>De dónde sale cada extremo de un tramo de ciclo (F17 §6). Se declara, como todo dato (N-2).</summary>
public enum CycleEdge
{
    /// <summary>Fecha exacta: la apertura o el cierre quedaron registrados.</summary>
    Exact,

    /// <summary>Inferida de la primera o de la última sesión del ciclo: existía al menos desde/hasta entonces.</summary>
    Inferred,

    /// <summary>No hay traza: el tramo empieza o termina donde alcanza el dato, y el tooltip lo dice.</summary>
    Unknown,
}

/// <summary>
/// Un tramo de la cinta de ciclos (F17 §6): un ciclo de una aplicación, con su temática, sus
/// fechas y su foto. Los ciclos anteriores a F17 no tienen temática y se pintan como General;
/// una fecha que no se pudo recuperar termina donde alcanza el dato y se declara — nada se rellena.
/// </summary>
/// <param name="Audited">Unidades auditadas al cierre (o ahora, si está abierto).</param>
/// <param name="Auditable">Unidades auditables: todas menos las grandes.</param>
/// <param name="HasInventory">False si el <c>cycle{N}.json</c> ya no está en disco: sin foto de cobertura.</param>
/// <param name="Cost">Coste del ciclo en AI credits, solo lo facturable. Null si nada facturable lo cobró.</param>
/// <summary>Un periodo de temática dentro de un tramo (F17.1), ya recortado al tramo.</summary>
public sealed record ThemeSlice(AuditTheme Theme, DateTimeOffset From, DateTimeOffset To, string? By);

/// <param name="ReportSessionId">La sesión del cierre —su informe— o null si el ciclo se cerró por reinicio o sigue abierto.</param>
/// <param name="Slices">Los periodos de temática del ciclo, en orden (F17.1). Uno solo en el caso normal.</param>
public sealed record CycleSpan(
    string Slug,
    string AppName,
    int CycleN,
    AuditTheme Theme,
    DateTimeOffset From,
    DateTimeOffset To,
    bool IsOpen,
    CycleEdge StartEdge,
    CycleEdge EndEdge,
    int Audited,
    int Auditable,
    bool HasInventory,
    int NewFindings,
    int ResolvedFindings,
    decimal? Cost,
    string? ReportSessionId,
    IReadOnlyList<ThemeSlice> Slices)
{
    /// <summary>«C2 · Seguridad», o «C3 · Rendimiento → Seguridad» cuando el ciclo cambió de lupa.</summary>
    public string Label => $"C{CycleN} · {ThemesLabel}";

    /// <summary>Las temáticas por las que pasó, en orden y sin repetir las consecutivas.</summary>
    public string ThemesLabel => string.Join(" → ", DistinctThemes.Select(Copilot.ThemeCatalog.Display));

    public IReadOnlyList<AuditTheme> DistinctThemes
    {
        get
        {
            var list = new List<AuditTheme>();
            foreach (ThemeSlice s in Slices)
            {
                if (list.Count == 0 || list[^1] != s.Theme)
                {
                    list.Add(s.Theme);
                }
            }

            return list.Count == 0 ? new[] { Theme } : list;
        }
    }

    public bool ChangedTheme => DistinctThemes.Count > 1;

    public string ShortLabel => $"C{CycleN}";

    /// <summary>El fin se pudo recuperar (o el ciclo sigue abierto, que es fin conocido: hoy).</summary>
    public bool EndIsKnown => IsOpen || EndEdge == CycleEdge.Exact;
}

/// <summary>Una banda de la cinta: una aplicación y sus ciclos en orden.</summary>
/// <param name="HiddenEarlier">
/// Cuántos ciclos de la app quedaron ANTES del periodo elegido (F17.2). El periodo recorta por
/// pertenencia —se enseñan los ciclos que lo solapan— y lo que deja fuera se dice con el número,
/// en vez de fabricar un eje.
/// </param>
public sealed record CycleTrack(string Slug, string Name, IReadOnlyList<CycleSpan> Spans, int HiddenEarlier = 0)
{
    /// <summary>«2 ciclos anteriores fuera del periodo», o vacío.</summary>
    public string Notice => HiddenEarlier switch
    {
        <= 0 => string.Empty,
        1 => "1 ciclo anterior fuera del periodo",
        _ => $"{HiddenEarlier} ciclos anteriores fuera del periodo",
    };
}

/// <summary>Una línea del registro de operaciones (gráfica 4).</summary>
/// <param name="Provider">
/// Con qué casa se hizo (F16 §C). Va junto al coste y no como adorno: con dos proveedores, dos
/// filas del mismo día pueden gastar de bolsas distintas, y sin esta columna la única forma de
/// saber de cuál era abrir el informe de cada una.
/// </param>
/// <param name="Cost">
/// Los credits que la sesión le costó a la organización, o <c>null</c> cuando no hay coste que
/// enseñar: porque su casa no factura (F16-RETOQUE §1) o porque falta la tarifa de su modelo.
/// </param>
/// <param name="Tokens">
/// Los tokens de la sesión, por tipo. Están en la fila desde F16-RETOQUE §1 porque son <b>lo que
/// queda</b> cuando el coste no aplica: una sesión de Claude Code sigue teniendo un peso que se
/// puede comparar con el de otra, y sin esta columna la actividad la enseñaría en blanco.
/// </param>
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
    string CostUnit,
    string Provider,
    string Tokens = "",
    string TokensDetail = "",
    bool Billed = true,
    CostReconciliation? Estimated = null)
{
    /// <summary>
    /// El coste de esta fila es una valoración y no una medida (F29 §1): lleva su asterisco donde
    /// se enseñe, y el tooltip dice con qué tarifa, quién la asignó y cuándo. La marca no se quita.
    /// </summary>
    public bool CostIsEstimate => Estimated is not null;
}

/// <summary>
/// Lo que costó UN proveedor en el periodo (F14, reducido en F16-RETOQUE §1).
/// <para>
/// Aquí solo llegan las casas que <b>facturan</b>. El desglose se mantiene porque mañana puede
/// haber dos que facturen, y porque saber de qué bolsa salió un gasto es la mitad de poder
/// cuadrarlo con el panel del proveedor. Lo que ya no aparece es Claude Code: su consumo va contra
/// la suscripción de quien lo usa y no se tarifa, así que no tiene línea de coste que enseñar
/// — sale en la actividad, con su proveedor y sus tokens.
/// </para>
/// </summary>
public sealed record ProviderCost(
    string ProviderId, string ProviderName, decimal? Cost, string CostUnit, int Sessions)
{
    /// <summary>La línea que se lee en el panel: «GitHub Copilot · 68,2 AI credits».</summary>
    public string Line => Cost is { } c
        ? $"{ProviderName} · {CostFormat.Number(c)} {CostUnit}"
        // Las que no se pueden valorar no llegan aquí: el agregado las deja fuera y las cuenta
        // aparte como «parcial», con su motivo (D-787). Esta rama es la red por si alguna vez sí.
        : $"{ProviderName} · coste no calculable";
}

/// <summary>
/// En qué se le va el dinero, por FASE del trabajo (F18 §1). Las tres fases son las tres cosas
/// distintas que se le piden a un modelo, y cuestan muy distinto: <b>descubrimiento</b> (auditar,
/// que barre cada unidad hasta agotarla), <b>verificación</b> (releer un puñado de hallazgos) y
/// <b>arreglo</b> (una conversación larga sobre un solo defecto).
/// <para>
/// Sale del modo de la sesión, que ya estaba escrito: no hay dato nuevo en el hub. Hasta F18, para
/// contestar «¿en qué se me va el dinero?» había que abrir los informes uno a uno.
/// </para>
/// <para>
/// <b>Los tokens van siempre; el coste, solo cuando lo hay.</b> Una fase hecha con una casa que no
/// factura tiene peso pero no tiene precio, y poner un 0 diría que fue gratis.
/// </para>
/// </summary>
/// <param name="Sessions">Cuántas sesiones del periodo fueron de esta fase.</param>
/// <param name="Cost">Los credits facturados, o null si nada de esta fase factura.</param>
public sealed record PhaseCost(
    string Phase, int Sessions, int Calls, long Tokens, decimal? Cost)
{
    /// <summary>«Descubrimiento · 12 sesiones · 264 llamadas · 1.284.000 tokens · 193,3 AI credits».</summary>
    public string Line
    {
        get
        {
            string head = $"{Phase} · {Sessions} sesión(es) · {Calls} llamada(s) · "
                + $"{Tokens.ToString("N0", AppCulture.Display)} tokens";
            return Cost is { } c ? $"{head} · {CostFormat.WithUnit(c, null)}" : head;
        }
    }
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
    int UntariffedSessions,
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
    IReadOnlyList<SessionRow> Sessions,
    IReadOnlyList<CycleTrack> Cycles,
    IReadOnlyList<PhaseCost>? Phases = null)
{
    /// <summary>El reparto por fase del periodo (F18 §1). Vacío cuando no hubo sesiones.</summary>
    public IReadOnlyList<PhaseCost> ByPhase => Phases ?? Array.Empty<PhaseCost>();

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

    // <b>El coste de este panel es la FACTURA de la organización, y solo eso</b> (F16-RETOQUE §1).
    // Aquí vivía `CostIsMixed`: el panel hacía malabares con dos naturalezas —una factura y un
    // «equivalente API»— y se negaba a dar un total cuando convivían. Ya no hace falta, y por eso
    // no hay ninguna propiedad que lo diga: lo que no factura no se tarifa y no llega, así que todo
    // lo que suma este panel es de la misma naturaleza POR CONSTRUCCIÓN. Un total que mezclara
    // factura con suscripción no es que se evite — es que no se puede formar.

    /// <summary>
    /// Hay sesiones <b>facturables</b> con tokens que NO se han podido valorar —sin modelo
    /// registrado, o con un modelo sin tarifa—, así que lo que se enseña es menos que lo que se
    /// gastó. Se DICE: un total al que le falta gasto se lee como si fuera el gasto entero.
    /// <para>
    /// Las de una casa que no factura <b>no cuentan como parciales</b> (F16-RETOQUE §1): no es que
    /// falte su gasto, es que no lo tienen. Meterlas aquí haría que el panel pidiera una tarifa que
    /// no debe existir, y un aviso que ladra sin causa se aprende a ignorar.
    /// </para>
    /// </summary>
    public bool CostIsPartial => PartialCostSessions > 0;

    /// <summary>La frase del parcial, con el número, para poder actuar sobre ella.</summary>
    public string PartialCostNotice => PartialCostSessions == 1
        ? "1 sesión sin tarifa para su modelo: no está contada."
        : $"{PartialCostSessions} sesiones sin tarifa para su modelo: no están contadas.";

    /// <summary>
    /// En el periodo hubo sesiones de una casa que <b>no factura</b> (F16-RETOQUE §1).
    /// <para>
    /// No es un aviso de que falte nada: es la explicación de por qué el coste del periodo no
    /// cubre toda la actividad que se ve más abajo. Sin decirlo, quien mire el tile y luego el
    /// registro de sesiones no entendería la diferencia — y lo primero que haría sería buscar la
    /// tarifa que falta, que es justo lo que aquí no hay que hacer.
    /// </para>
    /// </summary>
    public bool HasUntariffed => UntariffedSessions > 0;

    /// <inheritdoc cref="HasUntariffed"/>
    public string UntariffedNotice => UntariffedSessions == 1
        ? "1 sesión con Claude Code: no se tarifa —va contra la suscripción de quien la lanzó—, "
          + "así que no está en esta cifra. Sus tokens sí están en la actividad."
        : $"{UntariffedSessions} sesiones con Claude Code: no se tarifan —van contra la suscripción "
          + "de quien las lanzó—, así que no están en esta cifra. Sus tokens sí están en la actividad.";

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
        InventoryCycle? Inventory,
        IReadOnlyList<InventoryCycle> Inventories);

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
        // F16-RETOQUE §1 — y «con coste» quiere decir lo que FACTURA. El consumo de Claude Code va
        // contra la suscripción de quien lo usa, así que no se tarifa y no entra en ninguna de
        // estas cifras: el tile, la gráfica y el desglose son la factura de la organización y nada
        // más. Ya no hay dos naturalezas que malabarear —eso era D-789—, porque la segunda dejó de
        // producir un número. Esas sesiones no desaparecen: salen en la actividad, con su proveedor
        // y sus tokens.
        CostLookup rates = CostBasis();
        IReadOnlyList<ProviderCost> byProvider = CostByProvider(inPeriod, rates);

        decimal? cost = byProvider.Count > 0
            ? scope.Sum(a => CostIn(a.Sessions, from, to, rates))
            : null;
        int unitsAudited = inPeriod.Sum(s => s.Units.Count);

        // Cuántas sesiones FACTURABLES del periodo tenían tokens pero NO se pudieron valorar: sin
        // modelo registrado, o con un modelo sin tarifa. El agregado que las contiene es PARCIAL, y
        // hay que decirlo — un total al que le falta gasto se lee como si fuera el gasto entero.
        // Las que no facturan no cuentan: no les falta una tarifa, es que no llevan ninguna.
        int partial = inPeriod.Count(x =>
            rates.Of(x).Why is CostUnavailable.ModelUnknown or CostUnavailable.RateMissing
            && HasTokens(x));

        // Y cuántas del periodo son de una casa que no factura. No es un hueco que rellenar: es lo
        // que explica que el coste del tile no cubra toda la actividad de más abajo.
        int untariffed = inPeriod.Count(x => !CreditCalculator.IsBilled(x.Provider));

        // El ratio «por unidad auditada» NO divide el gasto entero: divide lo que costó AUDITAR.
        // Un arreglo o una verificación no auditan ninguna unidad, así que su gasto subía el
        // ratio sin que cambiara nada de lo auditado (28/08/2026: 262,5 por unidad cuando auditar
        // esa unidad había costado 105). El tile de coste los sigue sumando —eso es el gasto—;
        // lo que no se puede es repartirlos entre algo que no produjeron.
        decimal auditCost = inPeriod.Where(x => x.Units.Count > 0).Sum(x => CostOf(x, rates));
        string costUnit = CostFormat.Unit;

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
            untariffed,
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
            SessionRows(scope, inPeriod, rates),
            CycleTracks(scope, from, to, now, rates),
            PhaseCosts(inPeriod, rates));
    }

    /// <summary>
    /// El reparto por fase del periodo (F18 §1). El modo de la sesión ES la fase; los modos
    /// retirados (Integral, Superficial) son auditoría igual, y los de gestión —cierre y reset— no
    /// llaman a ningún modelo, así que no aparecen: una fila a cero solo ocupa sitio.
    /// </summary>
    private static IReadOnlyList<PhaseCost> PhaseCosts(
        IReadOnlyList<AuditSession> sessions, CostLookup rates)
    {
        var order = new (string Name, Func<AuditSession, bool> Is)[]
        {
            ("Descubrimiento", x => x.Mode is AuditMode.Lotes or AuditMode.Integral or AuditMode.Superficial),
            ("Verificación", x => x.Mode == AuditMode.Verify),
            ("Arreglo", x => x.Mode == AuditMode.Fix),
        };

        var rows = new List<PhaseCost>();
        foreach ((string name, Func<AuditSession, bool> isPhase) in order)
        {
            var mine = sessions.Where(isPhase).ToList();
            if (mine.Count == 0)
            {
                continue;
            }

            // Los tokens son de TODAS —es el peso, y siempre está—; el coste solo de las que
            // facturan, y null cuando ninguna lo hace. Un 0 diría que la fase salió gratis.
            long tokens = mine.Sum(x =>
                Math.Max(0, x.Usage.InputTokens) + Math.Max(0, x.Usage.OutputTokens)
                + Math.Max(0, x.Usage.CacheReadTokens) + Math.Max(0, x.Usage.CacheWriteTokens));

            var billed = mine.Where(x => CreditCalculator.IsBilled(x.Provider)).ToList();
            decimal? cost = billed.Count > 0 ? billed.Sum(x => CostOf(x, rates)) : null;

            rows.Add(new PhaseCost(name, mine.Count, mine.Sum(x => Math.Max(0, x.Usage.Calls)), tokens, cost));
        }

        return rows;
    }

    // ---------- Gráfica 7: la cinta de ciclos (F17 §6) ----------

    /// <summary>
    /// La historia de auditoría de cada app: un tramo por ciclo, del 1 al vigente. Las fechas
    /// salen, por este orden, de lo registrado (la apertura escrita en el ciclo, el cierre o reset
    /// que lo abrió) y de lo inferible (su primera o su última sesión), y cada extremo dice de
    /// dónde salió. <b>Sin apertura ni sesión no hay tramo</b> (F17.1): en F17 se colgaba del
    /// primer hallazgo de la app, y eso pintaba un «C1 · General» para una aplicación cuyos
    /// únicos hallazgos eran medidos por la propia Atalaya —un ciclo que nadie auditó—. El periodo
    /// RECORTA: un ciclo que no toca el periodo no aparece; uno que lo cruza se dibuja entero y lo
    /// recorta el eje. Y una app sin tramos conserva su banda, vacía y rotulada.
    /// </summary>
    private static IReadOnlyList<CycleTrack> CycleTracks(
        IReadOnlyList<AppData> scope, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now,
        CostLookup rates)
    {
        var tracks = new List<(CycleTrack Track, int Key, int Weight)>();
        foreach (AppData app in scope)
        {
            var spans = new List<CycleSpan>();
            DateTimeOffset? previousEnd = null;

            for (int n = 1; n <= app.CurrentCycle; n++)
            {
                InventoryCycle? inv = app.Inventories.FirstOrDefault(c => c.CycleN == n);
                CycleStart start = CycleSummary.StartOf(app.Sessions, n);

                (DateTimeOffset? begin, CycleEdge startEdge) =
                    inv?.OpenedUtc is { } opened ? (opened, CycleEdge.Exact)
                    : start.When is { } when ? (when, start.Source == CycleStartSource.Opened ? CycleEdge.Exact : CycleEdge.Inferred)
                    : previousEnd is { } prev ? (prev, CycleEdge.Exact)
                    : ((DateTimeOffset?)null, CycleEdge.Unknown);
                if (begin is null)
                {
                    continue; // ni apertura ni sesión: no hay ciclo que pintar, y no se inventa
                }

                bool isOpen = n == app.CurrentCycle;
                AuditSession? opening = app.Sessions
                    .Where(s => s.CycleN == n + 1 && s.Mode is AuditMode.Cierre or AuditMode.Reset)
                    .OrderBy(s => s.StartedUtc)
                    .FirstOrDefault();
                InventoryCycle? nextInv = app.Inventories.FirstOrDefault(c => c.CycleN == n + 1);
                AuditSession? lastOfCycle = app.Sessions
                    .Where(s => s.CycleN == n)
                    .OrderByDescending(s => s.EndedUtc ?? s.StartedUtc)
                    .FirstOrDefault();

                (DateTimeOffset end, CycleEdge endEdge) =
                    isOpen ? (now, CycleEdge.Exact)
                    : opening is not null ? (opening.StartedUtc, CycleEdge.Exact)
                    : nextInv?.OpenedUtc is { } nextOpened ? (nextOpened, CycleEdge.Exact)
                    : lastOfCycle is not null ? (lastOfCycle.EndedUtc ?? lastOfCycle.StartedUtc, CycleEdge.Inferred)
                    : (begin.Value, CycleEdge.Unknown);
                if (end < begin.Value)
                {
                    end = begin.Value;
                }

                previousEnd = end;

                var mine = app.Sessions.Where(s => s.CycleN == n).ToList();
                decimal? cost = mine.Any(s => rates.Of(s).HasValue)
                    ? mine.Sum(s => CostOf(s, rates))
                    : null;

                int audited = inv?.Units.Count(u => u.State == UnitState.Auditada) ?? 0;
                int large = inv?.Units.Count(u => u.State == UnitState.Grande) ?? 0;
                int total = inv?.Units.Count ?? 0;

                DateTimeOffset b = begin.Value;
                spans.Add(new CycleSpan(
                    app.Slug,
                    app.Name,
                    n,
                    inv?.Theme ?? AuditTheme.General,
                    b,
                    end,
                    isOpen,
                    startEdge,
                    endEdge,
                    audited,
                    total - large,
                    inv is not null,
                    app.Findings.Count(f => f.FirstDetected.Utc >= b && f.FirstDetected.Utc < end),
                    app.Findings.Sum(f => ResolutionsIn(f, b, end)),
                    cost,
                    opening?.Mode == AuditMode.Cierre ? opening.Id.ToString() : null,
                    SlicesOf(inv, b, end)));
            }

            // El filtro de periodo recorta el eje: fuera de él no hay tramos. La banda se queda
            // igualmente, vacía y rotulada: una fila ausente invita a que otro tramo ocupe su sitio.
            var visible = spans.Where(s => s.To > from && s.From < to).ToList();
            int hiddenEarlier = spans.Count(s => s.To <= from);

            var live = app.Findings.Where(f => f.Status == FindingStatus.Activo).ToList();
            int critica = live.Count(f => f.Severity == Severity.Critica);
            int alta = live.Count(f => f.Severity == Severity.Alta);
            tracks.Add((
                new CycleTrack(app.Slug, app.Name, visible, hiddenEarlier),
                PortfolioOrder.Key(critica, live.Count),
                PortfolioOrder.Weight(critica, alta)));
        }

        return tracks
            .OrderBy(t => t.Key)
            .ThenByDescending(t => t.Weight)
            .ThenBy(t => t.Track.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Track)
            .ToList();
    }

    /// <summary>
    /// Los periodos de temática del ciclo, recortados al tramo (F17.1). Sin historial escrito, uno
    /// solo con la temática vigente; sin fichero de ciclo, uno solo General. El primer periodo
    /// empieza donde empieza el tramo aunque su fecha no esté, y el último termina donde termina.
    /// </summary>
    private static IReadOnlyList<ThemeSlice> SlicesOf(InventoryCycle? inv, DateTimeOffset begin, DateTimeOffset end)
    {
        if (inv is null)
        {
            return new[] { new ThemeSlice(AuditTheme.General, begin, end, null) };
        }

        var slices = new List<ThemeSlice>();
        IReadOnlyList<ThemePeriod> periods = inv.Periods;
        DateTimeOffset cursor = begin;
        for (int i = 0; i < periods.Count; i++)
        {
            ThemePeriod p = periods[i];
            DateTimeOffset from = i == 0 ? begin : (p.FromUtc ?? cursor);
            DateTimeOffset to = i == periods.Count - 1 ? end : (p.ToUtc ?? periods[i + 1].FromUtc ?? end);
            if (from < begin)
            {
                from = begin;
            }

            if (to > end)
            {
                to = end;
            }

            if (to < from)
            {
                to = from;
            }

            slices.Add(new ThemeSlice(p.Theme, from, to, p.By));
            cursor = to;
        }

        return slices;
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
    /// Agrupa por proveedor las sesiones del periodo <b>que facturan</b> (F14, F16-RETOQUE §1).
    /// <para>
    /// El filtro es el mismo <c>Calculate</c> de siempre: una casa que no factura devuelve
    /// «no tarifado» y se cae por el <c>HasValue</c>, igual que se cae una a la que le falta la
    /// tarifa. No hay aquí ninguna regla propia — si la hubiera, sería la segunda copia de una
    /// decisión que ya vive en <see cref="CreditCalculator.IsBilled"/>.
    /// </para>
    /// <para>
    /// Las sesiones anteriores a F14 no llevan proveedor escrito, y eso NO es un dato que falte:
    /// era Copilot, porque no había otro. Se les asigna esa casa en vez de inventar una categoría
    /// «desconocido» que solo conseguiría partir el histórico en dos.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ProviderCost> CostByProvider(
        IReadOnlyList<AuditSession> sessions, CostLookup rates)
        => sessions
            .Select(s => (Session: s, Cost: rates.Of(s)))
            .Where(x => x.Cost.HasValue)
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Session.Provider) ? LegacyProviderId : x.Session.Provider!,
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProviderCost(
                g.Key,
                ProviderDisplayName(g.Key),
                g.Sum(x => x.Cost.Credits ?? 0m),
                CostFormat.BillingUnit,
                g.Count()))
            .OrderBy(p => p.ProviderName, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>¿Guardó esta sesión tokens? Sin ellos no hay nada que valorar, y no es «parcial».</summary>
    private static bool HasTokens(AuditSession s)
        => s.Usage.InputTokens > 0 || s.Usage.OutputTokens > 0
        || s.Usage.CacheReadTokens > 0 || s.Usage.CacheWriteTokens > 0;

    /// <summary>Las tarifas del hub, o null si no hay o no se pueden leer.</summary>
    /// <summary>
    /// <b>Con qué se valora en este panel</b> (F29 §1): la tabla de tarifas y las reconciliaciones
    /// escritas. Las dos juntas, porque preguntar solo por la tabla dejaría a una sesión ya
    /// reconciliada contando como «parcial» — y el aviso pediría arreglar algo ya arreglado.
    /// </summary>
    private CostLookup CostBasis()
    {
        var reconciled = new Dictionary<Domain.Ids.Ulid, CostReconciliation>();
        foreach (string slug in _hub.Store.ListAppSlugs())
        {
            try
            {
                foreach (CostReconciliation r in _hub.Store.ListCostReconciliations(slug))
                {
                    reconciled[r.SessionId] = r;
                }
            }
            catch (Exception)
            {
                // Un fichero a medio escribir por un merge no tumba el panel: sin su
                // reconciliación, esa sesión vuelve a contar como parcial, que es lo que era.
            }
        }

        return new CostLookup(ModelRates(), reconciled);
    }

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
    /// El nombre legible de un identificador guardado. Sale de <see cref="ProviderNames"/>, que es
    /// el mapa con el que se lee el HISTÓRICO —y no del registro de proveedores: Métricas lee
    /// sesiones de hace meses y tiene que poder nombrar una casa aunque esta versión ya no la
    /// traiga—. Era una tabla propia hasta F16 §C, que es como nacen dos nombres para lo mismo.
    /// </summary>
    private static string ProviderDisplayName(string providerId) => ProviderNames.Display(providerId);

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
    internal static decimal CostOf(AuditSession session) => CostOf(session, CostLookup.Empty);

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
    internal static decimal CostOf(AuditSession session, CostLookup rates)
        => rates.Of(session).Credits ?? 0m;

    /// <summary>
    /// El coste de las sesiones que arrancaron dentro del tramo. El tile de «Coste del periodo» y
    /// cada punto de la gráfica salen de AQUÍ, no de dos sumas parecidas: cuando el tile y la
    /// gráfica hacen su propia cuenta, tarde o temprano una de las dos se queda sin actualizar
    /// (nos pasó dos veces). Una sesión sin modo reconocible se cuenta igual: la que se saltaría
    /// es la única que no se podría explicar.
    /// </summary>
    internal static decimal CostIn(
        IEnumerable<AuditSession> sessions, DateTimeOffset from, DateTimeOffset to, CostLookup? rates = null)
        => sessions.Where(s => s.StartedUtc >= from && s.StartedUtc < to)
            .Sum(x => CostOf(x, rates ?? CostLookup.Empty));

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
        IReadOnlyList<AppData> scope, IReadOnlyList<AuditSession> inPeriod, CostLookup rates)
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
                rates.Of(s).Credits,
                CostFormat.BillingUnit,
                ProviderNames.Display(s.Provider),
                // Los tokens de la fila. Con una casa que no factura son la ÚNICA magnitud que la
                // actividad puede enseñar, y son dato primario: se quedan (F16-RETOQUE §1).
                CostFormat.TokensTotal(
                    s.Usage.InputTokens, s.Usage.OutputTokens,
                    s.Usage.CacheReadTokens, s.Usage.CacheWriteTokens),
                CostFormat.Tokens(
                    s.Usage.InputTokens, s.Usage.OutputTokens,
                    s.Usage.CacheReadTokens, s.Usage.CacheWriteTokens),
                CreditCalculator.IsBilled(s.Provider),
                // F29 §1 — la marca de «estimado» viaja con la fila: un coste valorado con una
                // tarifa que alguien eligió no puede pintarse igual que uno medido.
                rates.Of(s).EstimatedWith))
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
                _hub.Store.TryReadInventory(slug, app.CurrentCycle),
                _hub.Store.ListInventories(slug)));
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
