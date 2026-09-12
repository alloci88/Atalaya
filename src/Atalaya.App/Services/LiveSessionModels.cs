using System.Collections.ObjectModel;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>Estado visible de una unidad dentro de la sesión en curso (V5, columna 1).</summary>
public enum UnitRunState
{
    Pendiente,
    Auditando,
    Completa,

    /// <summary>El auditor dejó hallazgos existentes sin veredicto (F4).</summary>
    Incompleta,

    /// <summary>El barrido agotó el tope sin secarse (F4.1).</summary>
    CoberturaIncompleta,

    /// <summary>Cortada por <c>MaxTokensPerUnit</c> (F3 Hito 1c).</summary>
    CortadaPorPresupuesto,

    /// <summary>La sesión se detuvo antes de llegar a ella (F5.1b).</summary>
    Detenida,

    /// <summary>El fichero no está en el clon.</summary>
    NoLocalizada,
}

/// <summary>
/// <b>Una entrada de la conversación de una auditoría</b>, sobre el modelo común de
/// <see cref="ConversationEntry"/> (F30 §3).
/// <para>
/// Era una línea de una columna con su carril de icono y su propia plantilla; ahora es una burbuja
/// de la misma conversación que el arreglo asistido, y lo único que queda aquí son las dos formas
/// de crearla: un evento —con su marca, su clase y, si narra un hallazgo, su gravedad— y el texto
/// del agente, que se va acumulando.
/// </para>
/// </summary>
public sealed partial class ActivityEntry : ConversationEntry
{
    /// <summary>
    /// Todo lo que NO es la prosa del agente. Se conserva con este nombre porque de él cuelgan los
    /// tests que cuadran la narración con los contadores de la sesión (F5.14).
    /// </summary>
    public bool IsEvent => Kind != ConversationKind.Prosa;

    public static ActivityEntry Event(
        string glyph, string text, Severity? severity = null,
        ConversationKind kind = ConversationKind.Hito)
        => new()
        {
            Voice = ConversationVoice.Atalaya,
            Kind = kind,
            Glyph = glyph,
            Severity = severity,
            Text = text,
        };

    public static ActivityEntry Text_(string text)
        => new() { Voice = ConversationVoice.Agente, Kind = ConversationKind.Prosa, Text = text };
}

/// <summary>El tono de una pastilla de resumen de pasada. Tres, y ninguno más.</summary>
public enum PassTone
{
    /// <summary>Un dato: cuántos confirmó, cuántos disputó. Ni bueno ni malo.</summary>
    Neutral,

    /// <summary>La pasada aportó algo: hallazgos nuevos.</summary>
    Success,

    /// <summary>Algo que mirar: una pasada seca, que es la que no encontró nada.</summary>
    Warning,
}

/// <summary>
/// Un dato del resumen de una pasada, como pastilla (R11 §1b): «3 nuevos», «4 confirmados»,
/// «seca».
/// <para>
/// Antes esto era UNA frase con puntos medios —«Pasada 2 — 0 nuevo(s) · 4 confirmado(s) · 0
/// disputado(s) · seca»— y para saber si la pasada había aportado algo había que leerla entera. Con
/// tres pastillas la respuesta se ve sin leer, que es el principio 4 aplicado al sitio donde más
/// veces se mira.
/// </para>
/// </summary>
public sealed record PassChip(string Label, PassTone Tone);

/// <summary>Una pasada del barrido, como sección colapsable de la columna de actividad.</summary>
public sealed partial class PassProgress : ObservableObject
{
    public required int Index { get; init; }

    public ObservableCollection<ActivityEntry> Entries { get; } = new();

    /// <summary>El rótulo de la fila: «Pasada 2», y nada más. El resumen son las pastillas.</summary>
    public string Title => $"Pasada {Index}";

    /// <summary>
    /// <b>El separador de sección dentro del hilo</b> (F30 §3): «Pasada 2 · turno del hilo».
    /// <para>
    /// Dice lo que la pasada ES desde F25 y M2: un turno de la misma conversación con el agente, no
    /// un intento nuevo. Con el árbol de expanders retirado, el hilo se lee de arriba abajo y esta
    /// línea es lo único que dice dónde empieza cada turno.
    /// </para>
    /// </summary>
    public string SectionTitle => $"Pasada {Index} · turno del hilo";

    /// <summary>
    /// Lo que hizo la pasada, en pastillas. <b>Los tres números van siempre y en el mismo orden</b>
    /// (F12 §H.2): una pasada de reconciliación que confirma siete hallazgos no puede resumirse
    /// como «seca», que se lee como «aquí no ha pasado nada». Lo que cambia respecto a F12 es la
    /// forma, no lo que se cuenta.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<PassChip> _chips = Array.Empty<PassChip>();

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>«1 nuevo» / «3 nuevos»: el plural concuerda, como en el resto de la aplicación.</summary>
    public static string Counted(int count, string singular, string plural)
        => $"{count} {(count == 1 ? singular : plural)}";
}

/// <summary>
/// Una unidad de la cola, con su estado y su narración (V5, columnas 1 y 2). Es el objeto que
/// permite que la vista se reconstruya al volver a ella: el estado vive aquí, no en la vista.
/// </summary>
public sealed partial class UnitProgress : ObservableObject
{
    public required string Path { get; init; }

    /// <summary>
    /// Lo que se lee en la cola (F5.3): SOLO el nombre del fichero. La ruta completa vive en el
    /// tooltip y en la cabecera de la sección de actividad, así que repetirla —recortada, además—
    /// en una columna estrecha solo gastaba sitio sin identificar nada.
    /// <para>
    /// Lo calcula <see cref="ShortNames"/> sobre el lote entero, porque desambiguar dos ficheros
    /// que se llaman igual es una propiedad del LOTE, no de una ruta suelta.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _shortName = string.Empty;

    public ObservableCollection<PassProgress> Passes { get; } = new();

    [ObservableProperty]
    private UnitRunState _state = UnitRunState.Pendiente;

    [ObservableProperty]
    private int _currentPass;

    /// <summary>Hallazgos nuevos que aportó esta unidad, una vez cerrada.</summary>
    [ObservableProperty]
    private int _findings;

    [ObservableProperty]
    private int _passCount;

    [ObservableProperty]
    private decimal? _cost;

    [ObservableProperty]
    private long _tokens;

    /// <summary>
    /// <b>Abierta por defecto</b> (F30 §3). Mientras la sesión corre, el hilo se lee entero de
    /// arriba abajo: plegar es del usuario. Al terminar, <c>LiveSessionService</c> deja abierta solo
    /// la última pasada de cada unidad, que es lo que se va a mirar de una sesión ya cerrada.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>
    /// <b>El resumen de la unidad, a la derecha de su cabecera</b> (R11 §1a): «2 pasadas ·
    /// 0 hallazgos · 0,12 $». Vacío mientras la unidad no ha terminado — un resumen de algo que
    /// todavía está pasando no resume nada.
    /// <para>
    /// <b>Y el coste pasa por <see cref="CostFormat"/></b> (R11 §1f). Se escribía aquí a mano
    /// —<c>$" · coste {c:0.##}"</c>— así que la tarjeta decía «coste 15» en credits mientras el pie
    /// de la misma pantalla decía «0,12 $»: el mismo número, dos unidades y ninguna dicha. Es
    /// exactamente el defecto que F29 §2 vino a cerrar, escapado por un sitio que aquel test no
    /// miraba porque no es un XAML.
    /// </para>
    /// </summary>
    public string SummaryLine => PassCount == 0 && Findings == 0 && Cost is null && Tokens == 0
        ? string.Empty
        : string.Join(" · ", new[]
        {
            PassProgress.Counted(PassCount, "pasada", "pasadas"),
            PassProgress.Counted(Findings, "hallazgo", "hallazgos"),
            CostOrTokens,
        });

    /// <summary>
    /// La ÚNICA línea que va debajo del nombre en la fila de la cola (R11 §1d): el resumen sin los
    /// hallazgos —la columna mide 280 px y ahí lo que se busca es por dónde va y cuánto lleva
    /// gastado— y, mientras no haya nada que resumir, el estado en palabras.
    /// <para>
    /// Es una sola propiedad y no dos porque la fila tiene un solo renglón: eran tres líneas
    /// —nombre, estado y resultado— y por eso la unidad ocupaba una tarjeta en vez de una fila.
    /// </para>
    /// </summary>
    public string QueueLine => PassCount == 0 && Cost is null && Tokens == 0
        ? StateLabel
        : $"{PassProgress.Counted(PassCount, "pasada", "pasadas")} · {CostOrTokens}";

    /// <summary>
    /// El coste en la divisa activa, o los tokens cuando no hay coste que calcular. Un solo sitio,
    /// para que las dos líneas de arriba no puedan decirlo de dos maneras.
    /// </summary>
    private string CostOrTokens => Cost is { } credits
        ? CostFormat.Of(credits)
        : $"{Tokens} tokens";

    public string StateGlyph => State switch
    {
        UnitRunState.Auditando => "◐",
        UnitRunState.Completa => "✓",
        UnitRunState.Incompleta => "◑",
        UnitRunState.CoberturaIncompleta => "◔",
        UnitRunState.CortadaPorPresupuesto => "✂",
        UnitRunState.Detenida => "■",
        UnitRunState.NoLocalizada => "?",
        _ => "○",
    };

    public string StateLabel => State switch
    {
        UnitRunState.Auditando => CurrentPass > 0 ? $"pasada {CurrentPass}" : "auditando",
        UnitRunState.Completa => "completa",
        UnitRunState.Incompleta => "incompleta",
        UnitRunState.CoberturaIncompleta => "cobertura incompleta",
        UnitRunState.CortadaPorPresupuesto => "cortada por presupuesto",
        UnitRunState.Detenida => "detenida",
        UnitRunState.NoLocalizada => "no localizada",
        _ => "pendiente",
    };

    partial void OnStateChanged(UnitRunState value)
    {
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateLabel));
        RefreshSummaries();
    }

    partial void OnCurrentPassChanged(int value)
    {
        OnPropertyChanged(nameof(StateLabel));
        RefreshSummaries();
    }

    // Los dos resúmenes son derivados: se recalculan cuando cambia cualquiera de sus tres piezas.
    partial void OnPassCountChanged(int value) => RefreshSummaries();

    partial void OnFindingsChanged(int value) => RefreshSummaries();

    partial void OnCostChanged(decimal? value) => RefreshSummaries();

    partial void OnTokensChanged(long value) => RefreshSummaries();

    private void RefreshSummaries()
    {
        OnPropertyChanged(nameof(SummaryLine));
        OnPropertyChanged(nameof(QueueLine));
    }

    /// <summary>
    /// Nombres de la cola para un lote (F5.3): el del fichero a secas, y para los que chocan, el
    /// MÍNIMO de tramos de ruta que los separa — <c>Class/EnumContextMenuType.cs</c> solo cuando
    /// hay otro <c>EnumContextMenuType.cs</c> en el mismo lote.
    /// <para>
    /// Crece por grupos y vuelve a agrupar en cada vuelta: alargar unos pocos puede crear un
    /// choque nuevo con otro que ya era único, y sin recomprobar quedarían dos filas idénticas.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ShortNames(IReadOnlyList<string> paths)
    {
        var segments = paths
            .Select(p => (p ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        var depth = new int[paths.Count];
        Array.Fill(depth, 1);

        while (true)
        {
            var clashing = Enumerable.Range(0, paths.Count)
                .GroupBy(i => Tail(segments[i], depth[i]), StringComparer.Ordinal)
                .Where(g => g.Select(i => paths[i]).Distinct(StringComparer.Ordinal).Count() > 1)
                .ToList();

            if (clashing.Count == 0)
            {
                break;
            }

            bool grew = false;
            foreach (var group in clashing)
            {
                foreach (int i in group)
                {
                    if (depth[i] < segments[i].Length)
                    {
                        depth[i]++;
                        grew = true;
                    }
                }
            }

            // Ya no queda ruta que añadir: son la misma unidad repetida, no dos que confundir.
            if (!grew)
            {
                break;
            }
        }

        return Enumerable.Range(0, paths.Count).Select(i => Tail(segments[i], depth[i])).ToList();
    }

    /// <summary>Los <paramref name="count"/> últimos tramos de una ruta, con barras normales.</summary>
    private static string Tail(string[] segments, int count)
        => segments.Length == 0
            ? string.Empty
            : string.Join('/', segments[^Math.Min(count, segments.Length)..]);
}

/// <summary>Un conteo por severidad, para el resumen de una cabecera de grupo.</summary>
public sealed record SeverityChip(Severity Severity, int Count)
{
    public string Label => SeverityNames.Counted(Severity, Count);
}

/// <summary>
/// Un hallazgo dentro del desglose de una línea del resumen (F12 §H.1): además de la frase que se
/// lee, de qué UNIDAD es y con qué severidad. Sin esas dos cosas el desglose no se puede agrupar,
/// que es exactamente por lo que salía como una lista corrida.
/// <para>
/// <b>Y de qué hallazgo</b> (R10 §5). La gravedad se pinta con la pastilla del sistema y la fila
/// entera lleva a la ficha, así que el desglose necesita saber a quién lleva. Es opcional porque
/// las líneas que no hablan de un hallazgo concreto —una unidad incompleta, un corte— usan la
/// misma fila.
/// </para>
/// </summary>
public sealed record SummaryItem(string Unit, Severity Severity, string Text, Ulid FindingId = default)
{
    /// <summary>Hay ficha a la que ir. Sin esto la fila se pinta igual pero no se pulsa.</summary>
    public bool HasFinding => FindingId != default;
}

/// <summary>
/// Los hallazgos de una unidad dentro del desglose de una línea (F12 §H.1). Es el MISMO patrón que
/// la vista de Hallazgos: el fichero como cabecera y el recuento por severidad al lado.
/// </summary>
public sealed class SummaryGroup
{
    public required string Unit { get; init; }

    public required IReadOnlyList<SummaryItem> Items { get; init; }

    /// <summary>El nombre del fichero: la ruta entera va debajo, atenuada, como en V3.</summary>
    public string FileName
    {
        get
        {
            string name = Path.GetFileName(Unit.Replace('\\', '/'));
            return name.Length > 0 ? name : Unit;
        }
    }

    public string Subtitle => Unit;

    public string CountLabel => Items.Count == 1 ? "1 hallazgo" : $"{Items.Count} hallazgos";

    public IReadOnlyList<SeverityChip> Chips => Enum.GetValues<Severity>()
        .Select(sev => new SeverityChip(sev, Items.Count(i => i.Severity == sev)))
        .Where(c => c.Count > 0)
        .ToList();

    /// <summary>La más grave del grupo: ordena los grupos, igual que en V3.</summary>
    public Severity WorstSeverity => Items.Count == 0 ? Severity.Baja : Items.Min(i => i.Severity);
}

/// <summary>
/// Una línea del resumen de cierre (V5). Nunca un número suelto: cada contador trae su explicación
/// y el desglose de qué hallazgos o unidades lo componen, desplegable de un clic.
/// <para>
/// F12 §H.1 — el desglose de hallazgos va AGRUPADO POR CLASE, con su recuento por severidad. Salía
/// como una lista corrida: con veinte hallazgos de seis ficheros no había forma de ver de dónde
/// venían, y la vista de Hallazgos ya había resuelto exactamente eso. Un mismo dato se agrupa igual
/// en las dos pantallas o el usuario aprende dos formas de leerlo.
/// </para>
/// </summary>
public sealed partial class SummaryLine : ObservableObject
{
    public required string Label { get; init; }

    public required int Count { get; init; }

    /// <summary>Qué significa el número, en una frase.</summary>
    public required string Explanation { get; init; }

    /// <summary>
    /// Qué unidades o incidencias lo componen. Es el desglose de las líneas que NO hablan de
    /// hallazgos concretos (unidades incompletas, cortes, paradas); las de hallazgos usan
    /// <see cref="Groups"/>.
    /// </summary>
    public List<string> Details { get; init; } = new();

    /// <summary>El desglose agrupado por clase, cuando la línea habla de hallazgos.</summary>
    public IReadOnlyList<SummaryGroup> Groups { get; init; } = Array.Empty<SummaryGroup>();

    /// <summary>Resalta las líneas que piden atención (disputas, degradaciones, incidencias).</summary>
    public bool IsWarning { get; init; }

    public bool HasDetails => Details.Count > 0 || Groups.Count > 0;

    /// <summary>
    /// TODO lo que esta línea nombra, esté agrupado o no. Es la lectura de «ningún número sin
    /// causa»: cuántos casos nombra el desglose frente a lo que dice el contador. Agrupar por clase
    /// (F12 §H.1) no puede hacer que esa comprobación deje de poder hacerse en un sitio.
    /// </summary>
    public IReadOnlyList<string> Named => Groups.Count > 0
        ? Groups.SelectMany(g => g.Items).Select(i => i.Text).ToList()
        : Details;

    public bool HasGroups => Groups.Count > 0;

    /// <summary>Las dos listas nunca se pintan a la vez: o hay grupos, o hay líneas sueltas.</summary>
    public bool HasFlatDetails => Groups.Count == 0 && Details.Count > 0;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Agrupa por unidad y ordena como V3: primero la clase con lo más grave, y dentro de cada una
    /// los hallazgos por severidad.
    /// </summary>
    public static IReadOnlyList<SummaryGroup> GroupOf(IEnumerable<SummaryItem> items)
        => items
            .GroupBy(i => i.Unit, StringComparer.Ordinal)
            .Select(g => new SummaryGroup
            {
                Unit = g.Key,
                Items = g.OrderBy(i => i.Severity).ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase).ToList(),
            })
            .OrderBy(g => g.WorstSeverity)
            .ThenByDescending(g => g.Items.Count)
            .ThenBy(g => g.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// El consumo acumulado de una sesión en vivo, tal y como lo enseña el pie (F16-RETOQUE §1).
/// <para>
/// Los cuatro tipos de token van juntos porque el pie los necesita <b>todos</b> cuando la casa no
/// factura: sin coste que enseñar, lo que dice el peso de la sesión son las llamadas y los tokens,
/// y con Claude Code la caché es el sumando gordo. Y van en un registro y no en siete argumentos
/// sueltos porque cuatro <c>long</c> seguidos se cruzan sin que el compilador se entere.
/// </para>
/// </summary>
/// <param name="Cost">
/// El coste YA RESUELTO: con su número, o con el motivo por el que no lo hay —incluido «esta casa
/// no factura»—. Viaja entero para que la vista no tenga que inventarse la explicación.
/// </param>
/// <param name="Budget">
/// Adónde va lo que se está gastando, mientras se gasta (F18 §1): qué parte de cada llamada es
/// código auditado y cuántas llamadas lleva cada unidad. Va en vivo porque es <b>aquí</b> donde se
/// nota que algo se ha disparado: en el informe se lee cuando ya está pagado. Null en las sesiones
/// que no auditan unidades, donde no hay composición que resumir.
/// </param>
public sealed record LiveUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    CostResult Cost,
    string? Provider,
    int Calls,
    PromptBudget? Budget = null,
    int Turns = 0,

    // PROV-2 §3 — en qué unidad se escribe ese importe. Una sesión en vivo es de UNA casa, así
    // que la lente es la suya: viene de quien la lanzó, que es quien la conoce, y no de una
    // constante del formateador.
    CostLens? Lens = null);
