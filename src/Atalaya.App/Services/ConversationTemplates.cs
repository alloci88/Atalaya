using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Services;

/// <summary>
/// <b>Qué plantilla pinta cada clase de evento</b> (F30 §3). Una por
/// <see cref="ConversationKind"/>, y la misma en las tres vistas.
/// <para>
/// <b>Por qué la correspondencia vive en C# y no en un puñado de <c>DataTrigger</c>.</b> Porque es
/// lo único de esta pieza que puede romperse en silencio: una clase de evento nueva sin plantilla
/// no falla, no avisa y no se ve — se pinta el <c>ToString</c> del objeto en medio de la
/// conversación. Escrita aquí, un test la recorre entera y exige que cada clase tenga la suya
/// declarada en <c>Themes/Conversation.xaml</c>.
/// </para>
/// <para>
/// <b>Varias clases pueden compartir plantilla, y no es un descuido</b>: un hito, una entrega y el
/// cierre de una unidad se leen igual —icono, texto y hora— y lo que los distingue es el icono, que
/// sale del dato. Duplicar la burbuja para cada uno sería exactamente lo que esta fase vino a
/// retirar. Lo que la regla prohíbe es una clase <b>sin</b> plantilla, no dos clases con la misma.
/// </para>
/// </summary>
public sealed class ConversationTemplates : DataTemplateSelector
{
    /// <summary>El prefijo de todas las claves, para que se encuentren juntas en el diccionario.</summary>
    public const string Prefix = "Conversation.";

    /// <summary>La clave de recurso de la plantilla de una clase de evento.</summary>
    public static string KeyFor(ConversationKind kind) => Prefix + kind switch
    {
        // Lo que escribe el agente: su burbuja monoespaciada, sin icono.
        ConversationKind.Prosa => "Prosa",

        // Piensa y no manda el contenido: cursiva y tinta secundaria mientras dura.
        ConversationKind.Razonamiento => "Razonamiento",

        // Un hallazgo nuevo lleva su pastilla de gravedad delante del título.
        ConversationKind.Hallazgo => "Hallazgo",

        // La pregunta no es una burbuja: es una tarjeta con sus botones.
        ConversationKind.Pregunta => "Pregunta",

        // Todo lo demás es un suceso: icono, texto y hora. Lo que los distingue —una herramienta,
        // un veredicto, el cierre de una unidad— es el icono, y ése sale del dato.
        _ => "Evento",
    };

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is ConversationEntry entry && container is FrameworkElement element
            ? element.TryFindResource(KeyFor(entry.Kind)) as DataTemplate
            : null;
}
