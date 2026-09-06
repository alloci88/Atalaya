using System.Windows;
using System.Windows.Controls;

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
/// perder. La base de la fila es la mayor de las de sus hijos —para un <see cref="TextBlock"/>, su
/// <see cref="TextBlock.BaselineOffset"/>; para cualquier otra cosa, su borde inferior, que es lo
/// que WPF asume cuando un elemento no sabe decir dónde tiene la suya—.
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
        double below = 0;
        bool first = true;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            double gap = first ? 0 : Gap;
            double left = double.IsInfinity(available.Width)
                ? double.PositiveInfinity
                : Math.Max(0, available.Width - used - gap);

            child.Measure(new Size(left, available.Height));

            double baseline = BaselineOf(child);
            above = Math.Max(above, baseline);
            below = Math.Max(below, child.DesiredSize.Height - baseline);

            used += gap + child.DesiredSize.Width;
            first = false;
        }

        return new Size(used, above + below);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double above = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Collapsed)
            {
                above = Math.Max(above, BaselineOf(child));
            }
        }

        double x = 0;
        bool first = true;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            if (!first)
            {
                x += Gap;
            }

            // Cada uno baja lo que le falte para que su base caiga en la de la fila. Es todo lo
            // que hace este panel, y es lo que un Grid no puede hacer.
            child.Arrange(new Rect(
                x,
                above - BaselineOf(child),
                child.DesiredSize.Width,
                child.DesiredSize.Height));

            x += child.DesiredSize.Width;
            first = false;
        }

        return final;
    }

    /// <summary>
    /// A qué altura tiene su línea base este hijo, medida desde su borde superior. Un
    /// <see cref="TextBlock"/> la sabe decir; lo demás se apoya por abajo, que es lo que hace WPF
    /// con un elemento que no la declara.
    /// </summary>
    private static double BaselineOf(UIElement child)
        => child is TextBlock { BaselineOffset: var offset } && !double.IsNaN(offset)
            ? offset
            : child.DesiredSize.Height;
}
