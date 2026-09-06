using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Atalaya.App.Controls;

/// <summary>
/// Una fila de textos de tamaños distintos <b>alineados por su línea base</b>, con un hueco igual
/// entre todos (R10 §6).
/// <para>
/// <b>El defecto que la trae.</b> La identidad de una cabecera son tres piezas de dos tamaños:
/// «Arreglo asistido» en H2, el identificador del hallazgo y el nombre de la aplicación. Vivían en
/// tres celdas de un <see cref="Grid"/>, y WPF <b>no alinea líneas base entre celdas</b> — no
/// existe <c>VerticalAlignment="Baseline"</c>—. Lo único que había era <c>Center</c>, que iguala el
/// centro de las CAJAS: la caja pequeña de «XBLAST» quedaba centrada en la altura de la grande y su
/// texto salía por encima de la base del título, mientras el identificador —que sí compartía
/// párrafo con el título— se leía a su base. Tres piezas de una misma frase, tres alturas.
/// </para>
/// <para>
/// <b>Por qué no basta con meterlo todo en un <c>TextBlock</c>.</b> Es lo que hizo D-983 con el
/// título y el identificador, y funciona: los <c>Run</c> de un párrafo comparten base por
/// construcción. Pero un <c>Run</c> no acepta ni margen ni <c>MaxWidth</c> ni tooltip propio, y el
/// nombre de la aplicación necesita las tres cosas —es lo único de la zona que puede ser largo, y
/// es lo que tiene que ceder—. Este panel da lo uno sin perder lo otro: cada pieza sigue siendo su
/// propio elemento, con su recorte y su tooltip, y la fila las apoya en la misma base.
/// </para>
/// <para>
/// <b>Cómo mide.</b> De izquierda a derecha, cada hijo con el ancho que queda: así, lo que se
/// queda sin sitio es lo ÚLTIMO, que es exactamente el orden en el que esta cabecera quiere
/// perder. La base de la fila es la mayor de las de sus hijos.
/// </para>
/// <para>
/// <b>Y la base de un hijo que NO es texto se busca dentro de él</b> (R11 §2). Un
/// <see cref="TextBlock"/> la sabe decir por sí mismo (<see cref="TextBlock.BaselineOffset"/>);
/// un botón o una pastilla, no — y con ellos apoyados por su borde inferior, «Volver al hallazgo»
/// y el distintivo del proveedor quedaban más altos que el título con el que comparten fila, que
/// es el mismo defecto que este panel vino a arreglar, un nivel más adentro. Así que se busca el
/// PRIMER <see cref="TextBlock"/> de su árbol visual y se mide dónde cae su base dentro del hijo.
/// </para>
/// <para>
/// Eso obliga a <b>colocar dos veces</b>: la plantilla de un control no está montada hasta que se
/// mide, y la posición de ese texto dentro de él no se conoce hasta que el hijo se ha colocado. La
/// primera pasada de <c>Arrange</c> es la que hace aparecer esa geometría; la segunda ya sabe
/// dónde llevar cada pieza. Colocar dos veces en la misma pasada de layout es barato —son cuatro
/// o cinco elementos— y no reentra: <c>Arrange</c> con el mismo tamaño solo traslada.
/// </para>
/// </summary>
public sealed class BaselineRow : Panel
{
    /// <summary>El hueco entre dos piezas. Uno solo, y lo pone la fila: es la lección de <c>Stack.Gap</c>.</summary>
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(
            nameof(Gap),
            typeof(double),
            typeof(BaselineRow),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        double used = 0;
        double above = 0;
        double tallest = 0;
        double below = 0;
        bool first = true;

        foreach (UIElement child in Visible())
        {
            double gap = first ? 0 : Gap;
            double left = double.IsInfinity(available.Width)
                ? double.PositiveInfinity
                : Math.Max(0, available.Width - used - gap);

            child.Measure(new Size(left, available.Height));

            // En la medida solo se conoce con certeza la base de un TextBlock. Para el resto se
            // toma su borde inferior, que es el valor MÁS ALTO que su base puede tener: así la fila
            // se mide de sobra y nadie se queda sin sitio cuando `Arrange` afine.
            double baseline = TextBaseline(child) ?? child.DesiredSize.Height;
            above = Math.Max(above, baseline);
            below = Math.Max(below, child.DesiredSize.Height - baseline);
            tallest = Math.Max(tallest, child.DesiredSize.Height);

            used += gap + child.DesiredSize.Width;
            first = false;
        }

        return new Size(used, Math.Max(above + below, tallest));
    }

    protected override Size ArrangeOverride(Size final)
    {
        // Primera pasada: cada uno en su sitio a lo ancho y arriba del todo. No es la definitiva —
        // es la que monta la geometría interna de los hijos que no son texto para poder leerla.
        Place(final, deep: false);

        // Segunda: ya con la base real de cada uno, incluida la del texto que vive DENTRO de un
        // botón o de una pastilla.
        Place(final, deep: true);
        return final;
    }

    private void Place(Size final, bool deep)
    {
        double above = 0;
        foreach (UIElement child in Visible())
        {
            above = Math.Max(above, BaselineOf(child, deep));
        }

        double x = 0;
        bool first = true;
        foreach (UIElement child in Visible())
        {
            if (!first)
            {
                x += Gap;
            }

            // Cada uno baja lo que le falte para que su base caiga en la de la fila. Es todo lo
            // que hace este panel, y es lo que un Grid no puede hacer. El tope de abajo es el
            // cinturón: la fila se mide de sobra, pero si algún hijo creciera después de medirse,
            // preferimos verlo apoyado en el suelo a verlo cortado.
            double height = child.DesiredSize.Height;
            double y = Math.Max(0, Math.Min(above - BaselineOf(child, deep), final.Height - height));

            child.Arrange(new Rect(x, y, child.DesiredSize.Width, height));
            x += child.DesiredSize.Width;
            first = false;
        }
    }

    private IEnumerable<UIElement> Visible()
    {
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Collapsed)
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// A qué altura tiene su línea base este hijo, medida desde su borde superior. Con
    /// <paramref name="deep"/> se busca también dentro de los que no son texto; sin él —la primera
    /// pasada, cuando todavía no hay geometría que leer— se apoyan por abajo.
    /// </summary>
    private static double BaselineOf(UIElement child, bool deep)
        => (deep ? TextBaseline(child) ?? InnerTextBaseline(child) : TextBaseline(child))
           ?? child.DesiredSize.Height;

    /// <summary>La base de un <see cref="TextBlock"/>, o null si el hijo no lo es.</summary>
    private static double? TextBaseline(UIElement child)
        => child is TextBlock text && !double.IsNaN(text.BaselineOffset) && text.BaselineOffset > 0
            ? text.BaselineOffset
            : null;

    /// <summary>
    /// La base del PRIMER texto que hay dentro de un hijo que no es texto —el rótulo de un botón,
    /// la palabra de una pastilla—, ya trasladada a las coordenadas del hijo. Null si no hay
    /// ninguno o si todavía no se ha colocado.
    /// </summary>
    private static double? InnerTextBaseline(UIElement child)
    {
        TextBlock? inner = FirstText(child);
        if (inner is null || double.IsNaN(inner.BaselineOffset) || inner.BaselineOffset <= 0)
        {
            return null;
        }

        try
        {
            return inner.TransformToAncestor((Visual)child)
                .Transform(new Point(0, inner.BaselineOffset)).Y;
        }
        catch (InvalidOperationException)
        {
            // Todavía no comparten árbol colocado. Se cae al borde inferior, que es lo que había
            // antes de R11: un píxel peor, nunca una excepción en el layout.
            return null;
        }
    }

    private static TextBlock? FirstText(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock { Text.Length: > 0 } text)
            {
                return text;
            }

            if (FirstText(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
