using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// Reparte el ancho entre sus hijos en proporción a la <see cref="HeatSegment.Share"/> de cada
/// uno: el termómetro de la cabecera y las tiras de las tarjetas (F10.2 §1 y §3).
/// <para>
/// <b>Por qué un panel y no columnas de un <c>Grid</c>.</b> Los tramos salen de una colección que
/// cambia con los datos —un paso de la rampa sin ninguna unidad no se dibuja—, y un <c>Grid</c>
/// necesita sus <c>ColumnDefinition</c> escritas de antemano. Con el panel, la barra es
/// exactamente lo que hay.
/// </para>
/// <para>
/// El último tramo absorbe el redondeo: sin eso, la suma de los anchos deja una rendija de fondo
/// al final de la barra que se lee como un tramo más.
/// </para>
/// </summary>
public sealed class ShareBar : Panel
{
    /// <summary>
    /// Lo mínimo que ocupa un tramo que NO es cero. Sin esto, las dos unidades auditadas de un
    /// clon de 925 son el 0,2 % de la barra —medio píxel— y desaparecen: el termómetro diría que
    /// no se ha auditado nada, que es distinto de «casi nada». Es la misma regla que el grado
    /// mínimo de un tramo del rosco (<see cref="DonutRing"/>): lo que existe se ve.
    /// </summary>
    private const double MinSegment = 3;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(availableSize);
        }

        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shares = new double[InternalChildren.Count];
        double total = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            shares[i] = Math.Max(0, ShareOf(InternalChildren[i]));
            total += shares[i];
        }

        var widths = new double[InternalChildren.Count];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = total <= 0
                ? finalSize.Width / Math.Max(1, widths.Length)
                : finalSize.Width * shares[i] / total;
        }

        Promote(widths, shares, finalSize.Width);

        double x = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            double width = i == InternalChildren.Count - 1
                ? Math.Max(0, finalSize.Width - x)
                : widths[i];

            InternalChildren[i].Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    /// <summary>
    /// Sube a <see cref="MinSegment"/> los tramos que existen y no se verían, y le quita lo
    /// prestado a los que tienen de sobra. Si no hay de dónde quitarlo —una barra estrechísima—
    /// se deja como estaba: mejor una barra imprecisa que una que no cabe.
    /// </summary>
    private static void Promote(double[] widths, double[] shares, double available)
    {
        double owed = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            if (shares[i] > 0 && widths[i] < MinSegment)
            {
                owed += MinSegment - widths[i];
                widths[i] = MinSegment;
            }
        }

        if (owed <= 0)
        {
            return;
        }

        double spare = widths.Where(w => w > MinSegment).Sum(w => w - MinSegment);
        if (spare < owed)
        {
            return;
        }

        for (int i = 0; i < widths.Length; i++)
        {
            if (widths[i] > MinSegment)
            {
                widths[i] -= owed * (widths[i] - MinSegment) / spare;
            }
        }
    }

    /// <summary>Lo que ocupa un hijo. Sale de su dato, que es quien lo sabe.</summary>
    private static double ShareOf(UIElement child)
        => child is FrameworkElement { DataContext: HeatSegment segment } ? segment.Share : 0;
}
