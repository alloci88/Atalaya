using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>El rango temporal del panel (F5.9 §3). Los filtros afectan a TODO el panel.</summary>
public enum MetricsRange
{
    /// <summary>
    /// La última semana. Cubos DIARIOS, así que la regla del eje (F35 §1.2) se cumple entera con
    /// ellos: siete cubos son siete días y son más de dos, de modo que aquí el eje nunca recorta
    /// nada. El periodo anterior son los siete días de antes.
    /// </summary>
    Week1,

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
    /// <summary>
    /// Al abrir: todas las apps, <b>cuatro semanas</b> (F35 §1.1). Eran ocho, y ocho semanas de
    /// una herramienta que se usa desde hace días son seis semanas de línea plana delante de la
    /// única en la que pasó algo. El eje ya no las dibuja (<see cref="MetricsQuery.AxisFrom"/>),
    /// pero el periodo por defecto también decide qué es «el periodo anterior» de cada tendencia:
    /// cuatro semanas comparan contra las cuatro de antes, que es la pregunta que alguien se hace
    /// mirando este panel.
    /// </summary>
    public static MetricsFilter Default { get; } = new(null, MetricsRange.Weeks4);
}

/// <summary>
/// Hacia qué lado es <b>bueno</b> que se mueva una métrica (F35 §1.3). Sin esto una flecha sería
/// solo una dirección: bajar la deuda es una buena noticia y bajar la cobertura es una mala, y las
/// dos son «▼».
/// </summary>
public enum TrendGoodness
{
    /// <summary>Subir es bueno: la cobertura.</summary>
    UpIsGood,

    /// <summary>Bajar es bueno: la deuda activa y el coste por hallazgo resuelto.</summary>
    DownIsGood,

    /// <summary>
    /// Ni bueno ni malo: <b>el coste</b>. Gastar más no es malo por sí —puede ser que se esté
    /// auditando más—, así que su flecha va en gris siempre. Pintarla de rojo convertiría el panel
    /// en un juicio sobre una decisión que no ha tomado.
    /// </summary>
    Neutral,
}

/// <summary>
/// El cambio de una cifra contra el <b>periodo anterior</b> —el mismo número de días
/// inmediatamente antes— (F35 §1.3).
/// <para>
/// <b>Y D-318 manda aquí igual que en todo lo demás</b>: sin periodo anterior no hay tendencia, y
/// una tendencia contra un cero no es un porcentaje infinito, es una división que no se puede
/// hacer. En los dos casos <see cref="Percent"/> es <c>null</c> y la vista escribe la frase que
/// dice por qué, nunca una flecha de relleno.
/// </para>
/// </summary>
/// <param name="Percent">El cambio relativo, en puntos porcentuales del valor anterior.</param>
/// <param name="Goodness">Hacia dónde es bueno moverse, que es lo que decide el color.</param>
/// <param name="HasPreviousPeriod">
/// Hubo actividad antes del periodo. <b>Distingue los dos motivos de que no haya flecha</b>, que no
/// son el mismo: «no hay periodo anterior» —esta herramienta no estaba puesta— y «no hay cifra
/// anterior con la que comparar» —lo hubo, y valía cero—. Decir lo primero cuando pasa lo segundo
/// sería falso, y es la clase de frase que hace dudar de todo el panel.
/// </param>
public sealed record MetricTrend(double? Percent, TrendGoodness Goodness, bool HasPreviousPeriod)
{
    /// <summary>No hay con qué comparar. Es un estado, no un cero.</summary>
    public static MetricTrend None(TrendGoodness goodness, bool hasPrevious = false)
        => new(null, goodness, hasPrevious);

    /// <summary>
    /// La tendencia entre dos valores. Devuelve <see cref="None"/> —y por tanto ninguna flecha—
    /// cuando no hay periodo anterior, cuando falta cualquiera de los dos valores, o cuando el
    /// anterior es cero y no hay nada por lo que dividir.
    /// </summary>
    public static MetricTrend Between(
        double? now, double? before, bool hasPrevious, TrendGoodness goodness)
        => !hasPrevious || now is not { } n || before is not { } b || b == 0
            ? None(goodness, hasPrevious)
            : new MetricTrend((n - b) / b * 100.0, goodness, hasPrevious);

    /// <inheritdoc cref="Between(double?, double?, bool, TrendGoodness)"/>
    public static MetricTrend Between(
        decimal? now, decimal? before, bool hasPrevious, TrendGoodness goodness)
        => Between((double?)now, (double?)before, hasPrevious, goodness);

    /// <summary>Hay con qué comparar y hay porcentaje que escribir.</summary>
    public bool HasValue => Percent is not null;

    /// <summary>La cifra no se ha movido. Se dice con palabras, no con una flecha a cero.</summary>
    public bool IsFlat => Percent == 0;

    /// <summary>«▲», «▼», o nada cuando no hay tendencia o no se movió.</summary>
    public string Arrow => Percent switch
    {
        > 0 => "▲",
        < 0 => "▼",
        _ => string.Empty,
    };

    /// <summary>La cifra se movió hacia donde conviene. Falso también cuando es neutra o plana.</summary>
    public bool IsGood => Goodness != TrendGoodness.Neutral && !IsFlat && Percent is { } p
        && (Goodness == TrendGoodness.UpIsGood ? p > 0 : p < 0);

    /// <summary>Se movió hacia donde no conviene.</summary>
    public bool IsBad => Goodness != TrendGoodness.Neutral && !IsFlat && Percent is { } p
        && (Goodness == TrendGoodness.UpIsGood ? p < 0 : p > 0);
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
    public string ThemesLabel => string.Join(" → ", DistinctThemes.Select(ThemeCatalog.Display));

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

/// <summary>
/// Una banda de la cinta: una aplicación y <b>todos</b> sus ciclos, en orden.
/// <para>
/// Hasta F35-4 el periodo recortaba por pertenencia y lo que dejaba fuera se decía con un número
/// —«2 ciclos anteriores fuera del periodo»—, porque no había eje donde ponerlos. Con eje de
/// tiempo real ya no hace falta: la cinta es historia y va del primer ciclo hasta hoy, así que no
/// queda nada fuera y no hay nada que avisar.
/// </para>
/// </summary>
public sealed record CycleTrack(string Slug, string Name, IReadOnlyList<CycleSpan> Spans);

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
/// <b>Qué clase de acción es una sesión</b> (F35 §2.6). Son cuatro y <b>cubren todos</b> los
/// <see cref="AuditMode"/>: es una partición, no una selección.
/// <para>
/// Que sea completa no es un detalle de estilo — es lo que hace que los tramos del rosco de coste
/// por acción sumen <b>exactamente</b> el coste del periodo de esa aplicación. El reparto por fase
/// que había antes se dejaba fuera el cierre y el reset «porque no llaman a ningún modelo», y eso
/// funcionaba solo mientras siguieran costando cero: el día que una de esas sesiones gastara algo,
/// el rosco diría un total y la tarjeta de coste otro (D-591: una pregunta, una respuesta).
/// </para>
/// </summary>
public enum AuditAction
{
    /// <summary>Lotes y los dos modos retirados que eran auditoría igual (integral, superficial).</summary>
    Auditoria,

    /// <summary>Comprobar si un hallazgo sigue ahí.</summary>
    Verificacion,

    /// <summary>El arreglo asistido.</summary>
    Arreglo,

    /// <summary>
    /// Cierre de ciclo y reset. Hoy no llaman a ningún modelo y su tramo sale a cero —y un tramo a
    /// cero no se dibuja—, pero existe para que la partición sea completa: es la diferencia entre
    /// «no costó nada» y «no se contó».
    /// </summary>
    Gestion,
}

/// <summary>La acción de un modo, y cómo se llama. Un solo sitio, como <c>AuditModeNames</c>.</summary>
public static class AuditActions
{
    public static AuditAction Of(AuditMode mode) => mode switch
    {
        AuditMode.Lotes or AuditMode.Integral or AuditMode.Superficial => AuditAction.Auditoria,
        AuditMode.Verify => AuditAction.Verificacion,
        AuditMode.Fix => AuditAction.Arreglo,
        _ => AuditAction.Gestion,
    };

    public static string Display(AuditAction action) => action switch
    {
        AuditAction.Auditoria => "Auditoría",
        AuditAction.Verificacion => "Verificación",
        AuditAction.Arreglo => "Arreglo",
        _ => "Gestión",
    };

    /// <summary>Las cuatro, en el orden en que se leen: descubrir, comprobar, arreglar, gestionar.</summary>
    public static IReadOnlyList<AuditAction> All { get; } = new[]
    {
        AuditAction.Auditoria, AuditAction.Verificacion, AuditAction.Arreglo, AuditAction.Gestion,
    };
}

/// <summary>Un tramo del rosco de coste por acción: qué acción, cuánto costó y en cuántas sesiones.</summary>
public sealed record ActionSlice(AuditAction Action, decimal Credits, int Sessions)
{
    public string Label => AuditActions.Display(Action);
}

/// <summary>
/// El rosco de <b>coste por acción</b> de una aplicación (F35 §2.6): en qué se le fue el gasto del
/// periodo. Los cuatro tramos suman <see cref="Total"/>, que es el coste del periodo de esa
/// aplicación — la misma cifra que la tarjeta de coste filtrada a ella.
/// </summary>
public sealed record ActionCostDonut(string Slug, string Name, IReadOnlyList<ActionSlice> Slices)
{
    public decimal Total => Slices.Sum(s => s.Credits);

    /// <summary>
    /// Una app sin gasto en el periodo NO se omite de la fila: se dibuja vacía, igual que un rosco
    /// de cobertura sin inventario (F6.5). Una fila con huecos dice quién no gastó; una fila que
    /// solo trae a los que gastaron hace creer que las demás no están.
    /// </summary>
    public bool HasData => Total > 0m;
}

/// <summary>
/// Un cubo de <b>antigüedad de la deuda</b> (F35 §2.7): cuántos hallazgos activos llevan abiertos
/// ese tiempo desde que se detectaron.
/// </summary>
public sealed record AgeBucket(string Label, int From, int To)
{
    /// <summary>Los cuatro cubos, en orden y sin solapar: [0,7) [7,28) [28,84) [84,∞).</summary>
    public static IReadOnlyList<AgeBucket> All { get; } = new[]
    {
        new AgeBucket("< 1 sem", 0, 7),
        new AgeBucket("1–4 sem", 7, 28),
        new AgeBucket("4–12 sem", 28, 84),
        new AgeBucket("> 12 sem", 84, int.MaxValue),
    };

    /// <summary>En qué cubo cae una edad en días. Siempre cae en exactamente uno.</summary>
    public static int IndexOf(double days)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (days >= All[i].From && days < All[i].To)
            {
                return i;
            }
        }

        return All.Count - 1;
    }
}

/// <summary>
/// La deuda VIVA de una aplicación repartida por antigüedad (F35 §2.7): la gráfica que dice si la
/// deuda rota o se pudre.
/// <para>
/// <b>No la recorta el periodo</b>, igual que el rosco de severidad (D-320): es la foto de hoy, y
/// su suma tiene que ser la deuda activa de la tarjeta 2. Recortarla por la ventana temporal daría
/// una deuda más pequeña que la real — y además vaciaría por definición los cubos de más de cuatro
/// semanas cada vez que alguien eligiera «4 semanas», que es justo la pregunta que la gráfica
/// existe para contestar.
/// </para>
/// </summary>
public sealed record DebtAgeRow(string Slug, string Name, IReadOnlyList<int> Counts)
{
    public int Total => Counts.Sum();
}

/// <summary>
/// Una de las reglas que más hallazgos generaron <b>en el periodo</b> (F35 §2.8).
/// </summary>
/// <param name="RuleId">El id estable. Es lo que se cuenta; el nombre es cómo se lee.</param>
/// <param name="Name">
/// El título del catálogo. Una regla que el catálogo no conozca —un <c>criterio.&lt;área&gt;</c>, o
/// una retirada— se enseña con su id: es lo que hay, y es mejor que esconder la fila.
/// </param>
public sealed record RuleCount(string RuleId, string Name, int Count);

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
    IReadOnlyList<ActionCostDonut>? ActionCost = null,
    // ---- F35 §1.3: lo que las cuatro cifras necesitan y no estaba agregado ----
    bool HasPreviousPeriod = false,
    int ScopeApps = 0,
    int? ScopeCycle = null,
    int CycleAuditedBefore = 0,
    int CyclePendingBefore = 0,
    int NewInPeriod = 0,
    int ActiveAtPeriodStart = 0,
    decimal? CostPreviousPeriod = null,
    int SessionsInPeriod = 0,
    DateTimeOffset? AxisStart = null,
    IReadOnlyList<DebtAgeRow>? DebtByAge = null,
    IReadOnlyList<RuleCount>? TopRules = null)
{
    /// <summary>
    /// El eje de las gráficas empieza DESPUÉS del comienzo del periodo (F35 §1.2): los tramos de
    /// delante no tenían actividad y se han recortado. Se dice en la cabecera — un rótulo de
    /// periodo que prometiera un eje que no se dibuja es el defecto de D-593 puesto del revés.
    /// </summary>
    public bool AxisIsTrimmed => AxisStart is { } start && start > From;

    /// <summary>El coste por acción de cada aplicación (F35 §2.6). Vacío sin aplicaciones.</summary>
    public IReadOnlyList<ActionCostDonut> ByAction => ActionCost ?? Array.Empty<ActionCostDonut>();

    /// <summary>La deuda viva por antigüedad, una fila por aplicación (F35 §2.7).</summary>
    public IReadOnlyList<DebtAgeRow> ByAge => DebtByAge ?? Array.Empty<DebtAgeRow>();

    /// <summary>Las cinco reglas que más hallazgos generaron en el periodo (F35 §2.8).</summary>
    public IReadOnlyList<RuleCount> Rules => TopRules ?? Array.Empty<RuleCount>();

    /// <summary>Hubo gasto que repartir en alguna aplicación. Sin él, el bloque dice por qué.</summary>
    public bool HasActionCost => ByAction.Any(d => d.HasData);

    /// <summary>Hay deuda viva que repartir por antigüedad.</summary>
    public bool HasDebtAges => ByAge.Sum(r => r.Total) > 0;

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

    // ================================================== F35 §1.3 — las cuatro cifras
    //
    // Las cuatro salen de AQUÍ y no de cuatro cuentas parecidas repartidas por la vista: es la
    // misma lección de D-591 y D-597 —una pregunta, una respuesta— aplicada a las tarjetas nuevas.

    /// <summary>
    /// <b>La cobertura del ciclo</b>, agregada como SUMA DE UNIDADES sobre lo auditable (D-322).
    /// Se calcula por aplicación contra su ciclo en curso y se suma; nunca se promedian
    /// porcentajes, y nunca se enseña un número de ciclo sobre una suma de ciclos distintos —para
    /// eso está <see cref="ScopeCycle"/>, que solo existe con una aplicación en el filtro.
    /// </summary>
    public double? CoveragePct => CycleAudited + CyclePending == 0
        ? null
        : (double)CycleAudited / (CycleAudited + CyclePending);

    /// <summary>
    /// La misma cobertura <b>al cierre del periodo anterior</b>, reconstruida del inventario
    /// vigente: una unidad contaba como auditada entonces si la sesión que la auditó había
    /// arrancado ya. Es la única reconstrucción posible —el inventario es una foto de hoy— y por
    /// eso una unidad auditada sin sesión que la date no puede contarse: cae en pendiente, que es
    /// lo conservador (declararía menos cobertura, nunca más).
    /// </summary>
    public double? CoveragePctBefore => CycleAuditedBefore + CyclePendingBefore == 0
        ? null
        : (double)CycleAuditedBefore / (CycleAuditedBefore + CyclePendingBefore);

    /// <inheritdoc cref="CoveragePct"/>
    public MetricTrend CoverageTrend
        => MetricTrend.Between(CoveragePct, CoveragePctBefore, HasPreviousPeriod, TrendGoodness.UpIsGood);

    /// <summary>
    /// <b>La deuda activa</b>: la de hoy, que es también la del cierre del periodo —el extremo
    /// derecho del rango es siempre la medianoche de mañana—, así que decir «responde al periodo»
    /// y decir «es la foto de hoy» (D-320) es decir lo mismo mientras el periodo termine en hoy.
    /// Lo que sí cambia con el periodo es contra qué se compara: la deuda viva al empezarlo.
    /// </summary>
    public MetricTrend DebtTrend => MetricTrend.Between(
        (double)ActiveTotal, ActiveAtPeriodStart, HasPreviousPeriod, TrendGoodness.DownIsGood);

    /// <summary>
    /// <b>El coste del periodo</b>, contra el anterior y <b>sin juicio de color</b>: gastar más no
    /// es malo por sí mismo (<see cref="TrendGoodness.Neutral"/>).
    /// </summary>
    public MetricTrend CostTrend
        => MetricTrend.Between(CostInPeriod, CostPreviousPeriod, HasPreviousPeriod, TrendGoodness.Neutral);

    /// <summary>
    /// <b>Lo que costó cada hallazgo resuelto</b> en el periodo. Sin resueltos NO es cero ni
    /// infinito: es una división que no se puede hacer, y la tarjeta lo dice (D-318).
    /// </summary>
    public decimal? CostPerResolution => ResolvedInPeriod > 0 && CostInPeriod is { } c
        ? c / ResolvedInPeriod
        : null;

    /// <inheritdoc cref="CostPerResolution"/>
    public decimal? CostPerResolutionBefore => ResolvedPreviousPeriod > 0 && CostPreviousPeriod is { } c
        ? c / ResolvedPreviousPeriod
        : null;

    /// <inheritdoc cref="CostPerResolution"/>
    public MetricTrend CostPerResolutionTrend => MetricTrend.Between(
        CostPerResolution, CostPerResolutionBefore, HasPreviousPeriod, TrendGoodness.DownIsGood);

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

        // F35 §1.2 — el eje empieza en el primer cubo CON ACTIVIDAD. Los cubos del periodo entero
        // se calculan igual: el periodo sigue siendo el periodo (es lo que definen las cifras y el
        // «periodo anterior»); lo que se recorta es lo que se DIBUJA.
        IReadOnlyList<Bucket> buckets = AxisFrom(
            Buckets(fromLocal, toLocal, granularity), scope.SelectMany(ActivityStamps));

        var findings = scope.SelectMany(a => a.Findings).ToList();
        var sessions = scope.SelectMany(a => a.Sessions).ToList();
        var inPeriod = sessions.Where(s => s.StartedUtc >= from && s.StartedUtc < to).ToList();

        // ¿HAY periodo anterior contra el que comparar? (F35 §1.3). No es «hay días antes» —siempre
        // los hay—: es si en esos días pasó algo. Sin una sola sesión ni un solo evento de hallazgo
        // antes del periodo, comparar contra ellos sería comparar contra las semanas en las que
        // esta herramienta todavía no existía, y de ahí no sale una tendencia: sale un ∞ %.
        bool hasPrevious = scope.SelectMany(ActivityStamps).Any(s => s < from);

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

        // La misma cobertura AL EMPEZAR EL PERIODO, para la tendencia de la tarjeta 1 (F35 §1.3).
        int cycleAuditedBefore = 0;
        int cyclePendingBefore = 0;

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

            // Al empezar el periodo, una unidad estaba auditada si la sesión que la auditó ya
            // había arrancado. Sin sesión que la date cuenta como pendiente: es lo conservador
            // —enseña menos cobertura de la que hubo, nunca más— y es lo único que se puede decir.
            var startedAt = app.Sessions.ToDictionary(s => s.Id, s => s.StartedUtc);
            int auditedBefore = units.Count(u =>
                u.State == UnitState.Auditada
                && u.AuditedInSession is { } id
                && startedAt.TryGetValue(id, out DateTimeOffset when)
                && when < from);
            cycleAuditedBefore += auditedBefore;
            cyclePendingBefore += audited + pending - auditedBefore;
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
            ActionCosts(scope, from, to, rates),
            hasPrevious,
            scope.Count,

            // El número de ciclo SOLO con una aplicación en el filtro. Con varias no hay un ciclo:
            // hay tantos como aplicaciones, y escribir uno sobre la suma de todas sería inventarse
            // que van a la vez. Con varias se dice cuántas son, que es la verdad que cabe.
            scope.Count == 1 ? scope[0].CurrentCycle : null,
            cycleAuditedBefore,
            cyclePendingBefore,
            findings.Count(f => f.FirstDetected.Utc >= from && f.FirstDetected.Utc < to),
            findings.Count(f => AliveAt(f, from)),

            // El coste del periodo anterior sale de la MISMA función que el del periodo (D-597):
            // dos ventanas de la misma cuenta, no dos cuentas.
            byProvider.Count > 0 ? scope.Sum(a => CostIn(a.Sessions, from - span, from, rates)) : null,
            inPeriod.Count,
            buckets.Count > 0 ? buckets[0].From : null,
            DebtAges(scope, now),
            TopRules(findings, from, to));
    }

    /// <summary>
    /// <b>Cuándo pasó algo</b>: una sesión, la detección de un hallazgo o una de sus resoluciones
    /// (F35 §1.2). Es la definición de «actividad» del eje y la de «hay periodo anterior», y es una
    /// sola para que las dos no puedan discrepar. El coste no aporta sellos propios: un coste
    /// siempre es el de una sesión, que ya está contada.
    /// </summary>
    private static IEnumerable<DateTimeOffset> ActivityStamps(AppData app)
        => app.Sessions.Select(s => s.StartedUtc)
            .Concat(app.Findings.Select(f => f.FirstDetected.Utc))
            .Concat(app.Findings.SelectMany(ResolutionEvents));

    /// <summary>Lo mínimo que puede medir un eje de tiempo. Ver <see cref="AxisFrom"/>.</summary>
    internal const int MinAxisDays = 7;

    /// <summary>
    /// <b>El eje empieza en el primer cubo con actividad</b> (F35 §1.2).
    /// <para>
    /// Ocho semanas de línea plana no son ocho semanas de dato: son las semanas en las que esta
    /// herramienta todavía no estaba puesta, dibujadas como si en ellas no hubiera pasado nada.
    /// Las dos frases suenan igual y no lo son — «no hubo gasto» es una medida y «no había nada
    /// que medir» es una ausencia, y un eje que las pinta iguales aplasta contra el suelo la única
    /// semana con datos.
    /// </para>
    /// <para>
    /// <b>Dos topes.</b> El eje nunca baja de <see cref="MinAxisDays"/> días ni de dos cubos: un
    /// solo punto no es una gráfica, y una serie de un punto no dice si sube o baja. Y el último
    /// cubo no se toca nunca, así que el eje sigue terminando en hoy (D-593).
    /// </para>
    /// <para>
    /// Sin ninguna actividad en el periodo se cae al mínimo por la cola: las gráficas enseñarán su
    /// estado vacío, y el eje que haya detrás no puede ser el de ocho semanas de nada.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<Bucket> AxisFrom(
        IReadOnlyList<Bucket> buckets, IEnumerable<DateTimeOffset> stamps)
    {
        if (buckets.Count == 0)
        {
            return buckets;
        }

        var when = stamps.ToList();
        int first = 0;
        while (first < buckets.Count
               && !when.Any(s => s >= buckets[first].From && s < buckets[first].To))
        {
            first++;
        }

        if (first == buckets.Count)
        {
            first = buckets.Count - 1;
        }

        DateTimeOffset end = buckets[^1].To;
        while (first > 0
               && ((end - buckets[first].From).TotalDays < MinAxisDays || buckets.Count - first < 2))
        {
            first--;
        }

        return first == 0 ? buckets : buckets.Skip(first).ToList();
    }

    /// <summary>
    /// <b>El coste del periodo repartido por acción, aplicación a aplicación</b> (F35 §2.6).
    /// <para>
    /// Sale del modo de la sesión, que ya estaba escrito: no hay dato nuevo en el hub. Y los
    /// tramos salen de <see cref="CostOf"/>, la misma función que suma la tarjeta de coste, así
    /// que el rosco de una aplicación suma <b>exactamente</b> el coste que la tarjeta enseña con
    /// esa aplicación en el filtro. No es una coincidencia que haya que vigilar: es la misma
    /// cuenta partida en cuatro (D-591, D-597).
    /// </para>
    /// <para>
    /// Un tramo a cero no se guarda —no se dibuja, y una leyenda con «Gestión 0 %» solo ocupa
    /// sitio—, pero la acción existe en <see cref="AuditAction"/>: la partición es completa, así
    /// que ninguna sesión puede caerse del reparto sin que nadie lo note.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ActionCostDonut> ActionCosts(
        IReadOnlyList<AppData> scope, DateTimeOffset from, DateTimeOffset to, CostLookup rates)
    {
        var donuts = new List<ActionCostDonut>();
        foreach (AppData app in scope)
        {
            var mine = app.Sessions.Where(s => s.StartedUtc >= from && s.StartedUtc < to).ToList();
            var slices = new List<ActionSlice>();
            foreach (AuditAction action in AuditActions.All)
            {
                var ofAction = mine.Where(s => AuditActions.Of(s.Mode) == action).ToList();
                decimal credits = ofAction.Sum(s => CostOf(s, rates));
                if (credits > 0m)
                {
                    slices.Add(new ActionSlice(action, credits, ofAction.Count));
                }
            }

            donuts.Add(new ActionCostDonut(app.Slug, app.Name, slices));
        }

        // El mismo orden que la fila de roscos de cobertura: el que más pesa, primero.
        return donuts
            .OrderByDescending(d => d.Total)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// <b>La deuda viva repartida por antigüedad</b> (F35 §2.7): la gráfica que dice si la deuda
    /// rota o se pudre.
    /// <para>
    /// La edad se cuenta desde la <b>detección</b> del hallazgo hasta ahora, y cada activo cae en
    /// exactamente un cubo, así que la suma de todas las barras es la deuda activa de la tarjeta 2.
    /// No la recorta el periodo (D-320): recortarla vaciaría por definición los cubos de más de
    /// cuatro semanas cada vez que alguien eligiera «4 semanas», que es justo lo que se viene a
    /// mirar aquí.
    /// </para>
    /// </summary>
    private static IReadOnlyList<DebtAgeRow> DebtAges(
        IReadOnlyList<AppData> scope, DateTimeOffset now)
    {
        var rows = new List<DebtAgeRow>();
        foreach (AppData app in scope)
        {
            var counts = new int[AgeBucket.All.Count];
            foreach (Finding f in app.Findings.Where(f => f.Status == FindingStatus.Activo))
            {
                counts[AgeBucket.IndexOf((now - f.FirstDetected.Utc).TotalDays)]++;
            }

            rows.Add(new DebtAgeRow(app.Slug, app.Name, counts));
        }

        return rows
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Cuántas reglas se listan. Cinco: la lista es para decidir, no para inventariar.</summary>
    public const int TopRuleCount = 5;

    /// <summary>
    /// <b>Las reglas que más hallazgos generaron en el periodo</b> (F35 §2.8). Cuenta hallazgos
    /// <b>detectados</b> dentro del periodo, sea cual sea su estado hoy: la pregunta es qué está
    /// produciendo trabajo, y un hallazgo que ya se arregló lo produjo igual.
    /// <para>
    /// El empate se rompe por el <b>nombre</b> —el que se lee—, en orden ordinal: sin desempate,
    /// dos reglas con el mismo recuento se intercambiarían de sitio según en qué orden devolviera
    /// el disco los ficheros, y la lista bailaría entre dos cargas sin que nada hubiera cambiado.
    /// </para>
    /// </summary>
    private static IReadOnlyList<RuleCount> TopRules(
        IReadOnlyList<Finding> findings, DateTimeOffset from, DateTimeOffset to)
        => findings
            .Where(f => f.FirstDetected.Utc >= from && f.FirstDetected.Utc < to)
            .GroupBy(f => f.RuleId, StringComparer.Ordinal)
            .Select(g => new RuleCount(g.Key, RuleName(g.Key), g.Count()))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(TopRuleCount)
            .ToList();

    /// <summary>
    /// Cómo se lee una regla: el título del catálogo. Una que no esté —un <c>criterio.&lt;área&gt;</c>,
    /// o una retirada— se enseña con su id: es lo que hay, y esconder la fila sería peor.
    /// </summary>
    internal static string RuleName(string ruleId)
        => RuleCatalog.Find(ruleId)?.Title ?? ruleId;

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

            // F35-4 §1.1 — EL PERIODO NO RECORTA LA CINTA. Es historia, como la antigüedad de la
            // deuda: su eje va del primer ciclo registrado hasta hoy, y recortarlo por la ventana
            // del selector escondería justo los ciclos que explican de dónde viene la aplicación.
            // Hasta aquí se recortaba por pertenencia y lo que quedaba fuera se decía con un
            // número; ahora no queda nada fuera, así que no hay nada que decir.
            var visible = spans;

            var live = app.Findings.Where(f => f.Status == FindingStatus.Activo).ToList();
            int critica = live.Count(f => f.Severity == Severity.Critica);
            int alta = live.Count(f => f.Severity == Severity.Alta);
            tracks.Add((
                new CycleTrack(app.Slug, app.Name, visible),
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
            MetricsRange.Week1 => 1,
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
        if (range is MetricsRange.Week1 or MetricsRange.Weeks4)
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
