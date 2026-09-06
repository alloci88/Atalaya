using Atalaya.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>Quién habla en una línea de la conversación (F30 §3).</summary>
/// <remarks>
/// <b>La voz de enfrente se llama Agente en las tres vistas.</b> El arreglo asistido la llamaba así
/// desde F16 y la auditoría decía «el modelo»: es el mismo interlocutor, y dos nombres para uno
/// obligan a aprender dos pantallas donde hay una.
/// </remarks>
public enum ConversationVoice
{
    /// <summary>La aplicación: un hito, una herramienta que corrió, un aviso.</summary>
    Atalaya,

    /// <summary>El agente, en streaming.</summary>
    Agente,

    /// <summary>El usuario, escribiendo en el campo de entrada (solo en el arreglo).</summary>
    Usuario,
}

/// <summary>
/// <b>De qué clase es una entrada de la conversación</b> (F30 §3). De aquí sale la plantilla con la
/// que se pinta, y hay <b>exactamente una por clase</b>: es lo que impide que la misma cosa —una
/// herramienta, un hito— se dibuje de dos maneras según la pantalla en la que caiga.
/// </summary>
public enum ConversationKind
{
    /// <summary>Lo que el agente escribe, tal cual: su burbuja monoespaciada.</summary>
    Prosa,

    /// <summary>«Razonando…»: el agente piensa y no manda el contenido (F30 §2e).</summary>
    Razonamiento,

    /// <summary>Una herramienta: la que ya corrió y la que se está escribiendo.</summary>
    Herramienta,

    /// <summary>Un hito de la propia Atalaya: un corte, una reanudación, el cierre de una pasada.</summary>
    Hito,

    /// <summary>La entrega del turno: a partir de aquí lo que pase es del agente (F30 §1b).</summary>
    Entrega,

    /// <summary>Un hallazgo nuevo, con su gravedad delante del título.</summary>
    Hallazgo,

    /// <summary>Un veredicto sobre un hallazgo que ya existía: disputa, resolución, degradación.</summary>
    Veredicto,

    /// <summary>El cierre de una unidad, con o sin corte.</summary>
    Unidad,

    /// <summary>Algo salió mal y hay que leerlo.</summary>
    Error,

    /// <summary>Una pregunta esperando respuesta (solo en el arreglo).</summary>
    Pregunta,
}

/// <summary>
/// <b>Una entrada de la conversación de Atalaya</b> (F30 §3): la pieza que comparten el arreglo
/// asistido, la sesión en vivo y la última sesión.
/// <para>
/// <b>Por qué una sola.</b> Había dos implementaciones de lo mismo —<c>FixMessage</c> en el arreglo
/// y <c>ActivityEntry</c> en la auditoría—, cada una con su plantilla, su forma de decir la hora y
/// su manera de pintar un icono. Dos copias de una conversación acaban divergiendo igual que dos
/// cálculos de la misma verdad (F5.14): lo que en una pantalla es una burbuja con su voz, en la
/// otra era una línea en un carril.
/// </para>
/// <para>
/// Lo que viaja aquí es el <b>dato</b> —quién habla, de qué clase es, cuándo, con qué marca y con
/// qué gravedad—; el dibujo vive en <c>Themes/Conversation.xaml</c>, una plantilla por
/// <see cref="ConversationKind"/>. Cambiar de carácter a vector, o de línea a burbuja, no obliga a
/// tocar el modelo ni los tests que cuadran la narración con los contadores (D-1014).
/// </para>
/// </summary>
public abstract partial class ConversationEntry : ObservableObject
{
    /// <summary>
    /// Quién habla. Por defecto Atalaya: la voz de la casa es la que más entradas pone —hitos,
    /// herramientas, avisos— y las otras dos se nombran donde se crean.
    /// </summary>
    public ConversationVoice Voice { get; init; } = ConversationVoice.Atalaya;

    /// <summary>
    /// La clase de evento. Es <b>mutable</b> por un caso concreto: la línea de «se está
    /// escribiendo» se reescribe en su sitio (F30 §2), y pasa de «Razonando…» a «Reportando
    /// hallazgos…» sin dejar de ser la misma línea. Sin esto, o se apila una línea nueva —que
    /// cambia lo que se narra— o la burbuja se queda con la forma de lo anterior.
    /// </summary>
    [ObservableProperty]
    private ConversationKind _kind = ConversationKind.Hito;

    /// <summary>
    /// Cuándo llegó (R11 §1c). La conversación de una sesión larga se lee como un registro, y un
    /// registro sin horas no responde a lo único que se le pregunta cuando algo va lento: «¿cuánto
    /// lleva ahí?». Se sella al crear la entrada, no al pintarla.
    /// </summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>La hora tal y como se lee en la burbuja: <c>14:22:07</c>.</summary>
    public string Time => At.ToLocalTime().ToString("HH:mm:ss", AppCulture.Display);

    /// <summary>
    /// La marca de la línea. Sigue siendo el <b>dato</b> —de ella cuelgan los tests que cuadran la
    /// narración con los contadores de la sesión—; el icono que se dibuja es presentación (D-1014).
    /// </summary>
    public string Glyph { get; init; } = string.Empty;

    /// <summary>Severidad, cuando la entrada narra un hallazgo: da color a la pastilla.</summary>
    public Severity? Severity { get; init; }

    /// <summary>
    /// El texto es <b>mutable</b> porque los deltas del streaming llegan en trozos de pocos
    /// caracteres y se acumulan en la última entrada del agente: una fila por trozo reventaría la
    /// lista con miles de elementos.
    /// </summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Quién habla, tal y como se lee a la izquierda de la burbuja.</summary>
    public string Speaker => Voice switch
    {
        ConversationVoice.Agente => "Agente",
        ConversationVoice.Usuario => "Tú",
        _ => "Atalaya",
    };

    public bool IsAgent => Voice == ConversationVoice.Agente;

    public bool IsUser => Voice == ConversationVoice.Usuario;

    public bool IsAtalaya => Voice == ConversationVoice.Atalaya;
}
