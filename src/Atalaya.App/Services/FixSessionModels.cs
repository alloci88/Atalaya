using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>
/// <b>Una intervención del arreglo asistido</b>, sobre el modelo común de <see cref="ConversationEntry"/>
/// (F30 §3).
/// <para>
/// Lo único que añade a la base es <b>cómo se decide su clase</b>: el arreglo apunta sus líneas de
/// sistema con una marca —<c>👁</c> leer, <c>⚙</c> compilar, <c>⚠</c> un aviso— desde F16, y la
/// clase se deriva de ella en un solo sitio. Escribirla en los treinta sitios que llaman a
/// <see cref="System"/> sería la forma segura de que un día dos líneas iguales salieran distintas.
/// </para>
/// </summary>
public sealed partial class FixMessage : ConversationEntry
{
    public static FixMessage Agent(string text)
        => new() { Voice = ConversationVoice.Agente, Kind = ConversationKind.Prosa, Text = text };

    public static FixMessage User(string text)
        => new() { Voice = ConversationVoice.Usuario, Kind = ConversationKind.Prosa, Text = text };

    public static FixMessage System(string glyph, string text)
        => new()
        {
            Voice = ConversationVoice.Atalaya,
            Kind = KindOf(glyph),
            Glyph = glyph,
            Text = text,
        };

    /// <summary>
    /// La clase de evento que le corresponde a una marca del arreglo. Lo que no reconoce es un
    /// hito: es la clase neutra, y una marca nueva sin caso sale en el hilo con su forma de
    /// siempre en vez de desaparecer.
    /// </summary>
    internal static ConversationKind KindOf(string glyph) => glyph switch
    {
        // Herramientas del agente: leer, editar, compilar, buscar llamadores.
        "👁" or "✎" or "⚙" or "⌕" => ConversationKind.Herramienta,

        // El resultado de una compilación es un veredicto sobre lo que el agente acaba de tocar.
        "✓" => ConversationKind.Veredicto,

        // Lo que hay que leer: un aviso, un permiso denegado, un build roto.
        "⚠" or "⛔" or "✗" => ConversationKind.Error,

        // Lo que sale hacia el agente.
        "→" => ConversationKind.Entrega,

        _ => ConversationKind.Hito,
    };
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
public sealed partial class FixQuestion : ConversationEntry
{
    /// <summary>La pregunta es del agente y tiene clase propia: su tarjeta no es una burbuja.</summary>
    public FixQuestion()
    {
        Voice = ConversationVoice.Agente;
        Kind = ConversationKind.Pregunta;
    }

    /// <summary>Qué clase de pregunta es. No es la clase de EVENTO, que la pone la base.</summary>
    public required FixAskKind Ask { get; init; }

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

    public string Header => Ask == FixAskKind.Autorizacion
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
