using System.Collections.ObjectModel;
using Atalaya.Domain;
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

    /// <summary>Nombre con elipsis EN MEDIO: la cola es estrecha y lo que identifica es el final.</summary>
    public string Display => MiddleEllipsis(Path, 34);

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
    /// Recorta por el MEDIO conservando el principio y el final: en rutas de código lo que
    /// identifica es el nombre del fichero, y una elipsis al final se lo come justo.
    /// </summary>
    public static string MiddleEllipsis(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max)
        {
            return path;
        }

        int tail = Math.Max(max / 2, path.Length - path.LastIndexOf('/') - 1);
        if (tail > max - 4)
        {
            tail = max - 4;
        }

        int head = max - tail - 1;
        if (head < 1)
        {
            return "…" + path[^Math.Min(tail, path.Length)..];
        }

        return path[..head] + "…" + path[^tail..];
    }
}

/// <summary>
/// Una línea del resumen de cierre (V5). Nunca un número suelto: cada contador trae su explicación
/// y el desglose de qué hallazgos o unidades lo componen, desplegable de un clic.
/// </summary>
public sealed partial class SummaryLine : ObservableObject
{
    public required string Label { get; init; }

    public required int Count { get; init; }

    /// <summary>Qué significa el número, en una frase.</summary>
    public required string Explanation { get; init; }

    /// <summary>Qué hallazgos o unidades lo componen, y por qué.</summary>
    public List<string> Details { get; init; } = new();

    /// <summary>Resalta las líneas que piden atención (disputas, degradaciones, incidencias).</summary>
    public bool IsWarning { get; init; }

    public bool HasDetails => Details.Count > 0;

    [ObservableProperty]
    private bool _isExpanded;
}
