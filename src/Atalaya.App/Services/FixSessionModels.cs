using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>Quién habla en una línea de la conversación de arreglo.</summary>
public enum FixVoice
{
    /// <summary>El agente, en streaming.</summary>
    Agente,

    /// <summary>El usuario, escribiendo en el campo de entrada.</summary>
    Usuario,

    /// <summary>La aplicación: una herramienta que corrió, un límite que se aplicó, un aviso.</summary>
    Sistema,
}

/// <summary>Base de todo lo que aparece en el panel de conversación.</summary>
public abstract partial class FixEntry : ObservableObject
{
    public DateTimeOffset Utc { get; init; } = DateTimeOffset.UtcNow;

    public string Time => Utc.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>
/// Una intervención. El texto es MUTABLE porque los deltas del streaming llegan en trozos de
/// pocos caracteres y se acumulan en la última entrada del agente: una fila por trozo reventaría
/// la lista, exactamente igual que en la sesión en vivo de auditoría (F5.2).
/// </summary>
public sealed partial class FixMessage : FixEntry
{
    public required FixVoice Voice { get; init; }

    /// <summary>Icono de la línea cuando es del sistema; vacío para el texto del agente.</summary>
    public string Glyph { get; init; } = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    public bool IsAgent => Voice == FixVoice.Agente;

    public bool IsUser => Voice == FixVoice.Usuario;

    public bool IsSystem => Voice == FixVoice.Sistema;

    public string Speaker => Voice switch
    {
        FixVoice.Agente => "Agente",
        FixVoice.Usuario => "Tú",
        _ => "Atalaya",
    };

    public static FixMessage Agent(string text)
        => new() { Voice = FixVoice.Agente, Text = text };

    public static FixMessage User(string text)
        => new() { Voice = FixVoice.Usuario, Text = text };

    public static FixMessage System(string glyph, string text)
        => new() { Voice = FixVoice.Sistema, Glyph = glyph, Text = text };
}

/// <summary>Una opción de una pregunta, con su etiqueta tal y como la escribió el agente.</summary>
public sealed record FixChoice(string Label);

/// <summary>Qué clase de pregunta es. Cambia lo que se lee encima de la tarjeta.</summary>
public enum FixAskKind
{
    /// <summary>El agente pregunta (`ask_user`): una decisión de diseño.</summary>
    Decision,

    /// <summary>La aplicación pide autorización para tocar un fichero fuera del hallazgo.</summary>
    Autorizacion,
}

/// <summary>
/// Una pregunta esperando respuesta. Vive en la conversación —no en un diálogo modal— porque la
/// decisión se toma leyendo lo que el agente acaba de explicar, y un modal tapa justo eso.
/// </summary>
public sealed partial class FixQuestion : FixEntry
{
    public required string Text { get; init; }

    public required FixAskKind Kind { get; init; }

    /// <summary>Contexto de la aplicación: qué fichero y por qué, en las autorizaciones.</summary>
    public string Context { get; init; } = string.Empty;

    public IReadOnlyList<FixChoice> Choices { get; init; } = Array.Empty<FixChoice>();

    public bool AllowFreeform { get; init; } = true;

    public bool HasChoices => Choices.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _isAnswered;

    [ObservableProperty]
    private string _answer = string.Empty;

    /// <summary>Lo que el usuario escribe cuando la respuesta es libre.</summary>
    [ObservableProperty]
    private string _draft = string.Empty;

    public bool IsPending => !IsAnswered;

    public string Header => Kind == FixAskKind.Autorizacion
        ? "El agente pide permiso"
        : "El agente necesita que decidas";
}

/// <summary>
/// Un fichero tocado por la sesión, con su diff. La colección de líneas se recalcula en cada
/// edición: el panel enseña siempre el ANTES DE LA SESIÓN contra lo que hay ahora, no el último
/// retoque suelto — que es lo que el usuario tiene que revisar antes de commitear.
/// </summary>
public sealed partial class FixFileChange : ObservableObject
{
    public required string RelativePath { get; init; }

    /// <summary>El contenido previo a la sesión; <c>null</c> si el agente creó el fichero.</summary>
    public string? Before { get; set; }

    /// <summary>El fichero era del hallazgo. Los demás pasaron por una autorización.</summary>
    public required bool InScope { get; init; }

    /// <summary>Por qué el agente lo tocó, en sus palabras.</summary>
    [ObservableProperty]
    private string _reason = string.Empty;

    public ObservableCollection<DiffLine> Lines { get; } = new();

    [ObservableProperty]
    private int _added;

    [ObservableProperty]
    private int _removed;

    [ObservableProperty]
    private bool _isSelected;

    public string FileName => Path.GetFileName(RelativePath);

    public string Tally => $"+{Added} −{Removed}";

    public string ScopeNote => InScope
        ? "Fichero del hallazgo."
        : "Fuera del hallazgo: lo autorizaste durante la sesión.";

    public bool IsNew => Before is null;

    /// <summary>Vuelve a calcular el diff contra el contenido actual del fichero.</summary>
    public void Refresh(string after)
    {
        IReadOnlyList<DiffLine> full = LineDiff.Compute(Before, after);
        (int added, int removed) = LineDiff.Tally(full);
        Added = added;
        Removed = removed;

        Lines.Clear();
        foreach (DiffLine line in LineDiff.Collapse(full))
        {
            Lines.Add(line);
        }
    }
}

/// <summary>
/// La sugerencia de commit, editable in situ (F6.9 §4). Atalaya <b>no commitea</b>: esto solo le
/// ahorra al humano redactar el mensaje, y por eso los dos campos son suyos desde el momento en
/// que aparecen.
/// </summary>
public sealed partial class CommitSuggestion : ObservableObject
{
    /// <summary>El tope de un buen título de commit; la vista avisa al pasarse, no lo corta.</summary>
    public const int MaxTitleLength = 72;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleLengthNote))]
    [NotifyPropertyChangedFor(nameof(TitleIsLong))]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    public bool TitleIsLong => Title.Length > MaxTitleLength;

    public string TitleLengthNote => $"{Title.Length}/{MaxTitleLength}";

    /// <summary>Título y descripción listos para pegar en Visual Studio o en GitHub Desktop.</summary>
    public string ToClipboard()
        => string.IsNullOrWhiteSpace(Description)
            ? Title.Trim()
            : Title.Trim() + Environment.NewLine + Environment.NewLine + Description.Trim();
}
