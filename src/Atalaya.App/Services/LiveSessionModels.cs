using System.Collections.ObjectModel;
using Atalaya.Domain;
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

/// <summary>Qué es una línea de la columna de actividad.</summary>
public enum ActivityKind
{
    /// <summary>Texto tal cual lo emite el agente.</summary>
    Texto,

    /// <summary>Un evento de herramienta, de una línea y con icono.</summary>
    Evento,
}

/// <summary>
/// Una línea de la columna de actividad (V5, columna 2).
/// <para>
/// El texto es mutable a propósito: los deltas del streaming llegan en trozos diminutos y se
/// acumulan en la ÚLTIMA entrada de texto en vez de crear una fila por trozo, que reventaría la
/// lista con miles de elementos.
/// </para>
/// </summary>
public sealed partial class ActivityEntry : ObservableObject
{
    public required ActivityKind Kind { get; init; }

    /// <summary>Icono del evento; vacío para el texto del agente.</summary>
    public string Glyph { get; init; } = string.Empty;

    /// <summary>Severidad, cuando la línea narra un hallazgo: da color al chip.</summary>
    public Severity? Severity { get; init; }

    [ObservableProperty]
    private string _text = string.Empty;

    public bool IsEvent => Kind == ActivityKind.Evento;

    public static ActivityEntry Event(string glyph, string text, Severity? severity = null)
        => new() { Kind = ActivityKind.Evento, Glyph = glyph, Severity = severity, Text = text };

    public static ActivityEntry Text_(string text)
        => new() { Kind = ActivityKind.Texto, Text = text };
}

/// <summary>Una pasada del barrido, como sección colapsable de la columna de actividad.</summary>
public sealed partial class PassProgress : ObservableObject
{
    public required int Index { get; init; }

    public ObservableCollection<ActivityEntry> Entries { get; } = new();

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;
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

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Resumen de una línea para la fila de la cola, ya cerrada la unidad.</summary>
    [ObservableProperty]
    private string _resultLine = string.Empty;

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
    }

    partial void OnCurrentPassChanged(int value) => OnPropertyChanged(nameof(StateLabel));

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
    public string Label => $"{Count} {SeverityNames.Display(Severity)}";
}

/// <summary>
/// Un hallazgo dentro del desglose de una línea del resumen (F12 §H.1): además de la frase que se
/// lee, de qué UNIDAD es y con qué severidad. Sin esas dos cosas el desglose no se puede agrupar,
/// que es exactamente por lo que salía como una lista corrida.
/// </summary>
public sealed record SummaryItem(string Unit, Severity Severity, string Text);

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
public sealed record LiveUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    CostResult Cost,
    string? Provider,
    int Calls);
