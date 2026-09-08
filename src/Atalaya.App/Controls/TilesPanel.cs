using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// <b>La fila de azulejos de un informe</b> (F36-2b §1.1): reparte el ancho ENTERO entre lo que hay,
/// y una tarjeta puede valer dos.
/// <para>
/// <b>Por qué no vale <see cref="ColumnsPanel"/> aquí.</b> Aquél cuenta cuántas columnas de su ancho
/// mínimo caben y reparte entre ESAS columnas, sin mirar cuántas tarjetas hay — que es lo correcto
/// en el portafolio, donde las tarjetas son una lista que crece—. En la fila de un informe las
/// tarjetas son un cuadro fijo, así que con cinco tarjetas y seis columnas quedaba un canalón a la
/// derecha: es lo que se vio en el <c>dist</c>. Aquí el reparto se hace entre las UNIDADES que hay,
/// no entre las que caben.
/// </para>
/// <para>
/// <b>Unidades y no tarjetas.</b> Cada hijo declara cuánto vale con <see cref="SpanProperty"/> —una
/// unidad por defecto, dos la de «Ficheros tocados» y la del veredicto—, y el ancho se divide entre
/// la suma de la fila. Así una tarjeta doble mide exactamente el doble más el hueco, y no «lo que
/// sobre».
/// </para>
/// <para>
/// <b>Cuando no caben todas.</b> Se reparten en varias filas, y <b>equilibradas</b>: siete unidades
/// en dos filas son cuatro y tres, no seis y una. Cada fila vuelve a repartirse el ancho entero, así
/// que ninguna deja hueco a la derecha. Y todas las tarjetas de una fila miden lo que la más alta
/// (D-990), como en <see cref="ColumnsPanel"/>.
/// </para>
/// </summary>
public sealed class TilesPanel : Panel
{
    /// <summary>
    /// Cuánto vale este hijo. Dos unidades es lo máximo que se usa hoy; el panel no lo limita, pero
    /// una tarjeta más ancha que la fila se recorta a la fila.
    /// </summary>
    public static readonly DependencyProperty SpanProperty =
        DependencyProperty.RegisterAttached(
            "Span", typeof(int), typeof(TilesPanel),
            new FrameworkPropertyMetadata(
                1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static void SetSpan(UIElement element, int value) => element.SetValue(SpanProperty, value);

    public static int GetSpan(UIElement element) => (int)element.GetValue(SpanProperty);

    /// <summary>Por debajo de esto, una unidad deja de poder enseñar lo que lleva dentro.</summary>
    public static readonly DependencyProperty MinUnitWidthProperty =
        DependencyProperty.Register(
            nameof(MinUnitWidth), typeof(double), typeof(TilesPanel),
            new FrameworkPropertyMetadata(
                ReportLayout.TileMinWidth, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(TilesPanel),
            new FrameworkPropertyMetadata(
                ReportLayout.Gap, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinUnitWidth
    {
        get => (double)GetValue(MinUnitWidthProperty);
        set => SetValue(MinUnitWidthProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>Cuántas unidades caben de ancho, nunca menos de una.</summary>
    public int UnitsFor(double available)
    {
        if (double.IsInfinity(available) || available <= 0)
        {
            return 1;
        }

        int n = (int)Math.Floor((available + Gap) / (MinUnitWidth + Gap));
        return Math.Max(1, n);
    }

    /// <summary>
    /// <b>El reparto en filas</b>, en una función pura para poder comprobarlo sin montar la vista.
    /// Devuelve, por fila, cuántos hijos van en ella.
    /// </summary>
    /// <param name="spans">Lo que vale cada hijo, en orden.</param>
    /// <param name="perRow">Cuántas unidades caben de ancho.</param>
    public static IReadOnlyList<int> Rows(IReadOnlyList<int> spans, int perRow)
    {
        var rows = new List<int>();
        if (spans.Count == 0)
        {
            return rows;
        }

        int cap = Math.Max(1, perRow);
        int total = spans.Sum(s => Math.Max(1, s));
        if (total <= cap)
        {
            rows.Add(spans.Count);
            return rows;
        }

        // Equilibradas: siete unidades en dos filas son cuatro y tres. El objetivo se recalcula en
        // cada fila con lo que queda, así que la última no se queda con las sobras.
        int pending = total;
        int left = (int)Math.Ceiling(total / (double)cap);
        int i = 0;
        while (i < spans.Count)
        {
            int target = Math.Min(cap, (int)Math.Ceiling(pending / (double)Math.Max(1, left)));
            int used = 0;
            int count = 0;
            while (i < spans.Count)
            {
                int span = Math.Max(1, spans[i]);

                // Siempre entra al menos uno: una tarjeta más ancha que la fila se recorta, no se
                // queda sin colocar.
                if (count > 0 && used + span > target)
                {
                    break;
                }

                used += span;
                count++;
                i++;
            }

            rows.Add(count);
            pending -= used;
            left = Math.Max(1, left - 1);
        }

        return rows;
    }

    private IReadOnlyList<int> Spans()
        => InternalChildren.OfType<UIElement>().Select(GetSpan).ToList();

    protected override Size MeasureOverride(Size available)
    {
        var spans = Spans();
        var rows = Rows(spans, UnitsFor(available.Width));
        double width = double.IsInfinity(available.Width)
            ? spans.Sum(s => Math.Max(1, s)) * (MinUnitWidth + Gap)
            : available.Width;

        double total = 0;
        int at = 0;
        foreach (int count in rows)
        {
            double rowHeight = 0;
            int units = 0;
            for (int k = 0; k < count; k++)
            {
                units += Math.Max(1, spans[at + k]);
            }

            double unit = UnitWidth(width, units);
            for (int k = 0; k < count; k++)
            {
                UIElement child = InternalChildren[at + k];
                child.Measure(new Size(SpanWidth(unit, Math.Max(1, spans[at + k])), double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            total += rowHeight + Gap;
            at += count;
        }

        return new Size(width, Math.Max(0, total - Gap));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var spans = Spans();
        var rows = Rows(spans, UnitsFor(final.Width));

        double y = 0;
        int at = 0;
        foreach (int count in rows)
        {
            int units = 0;
            double rowHeight = 0;
            for (int k = 0; k < count; k++)
            {
                units += Math.Max(1, spans[at + k]);
                rowHeight = Math.Max(rowHeight, InternalChildren[at + k].DesiredSize.Height);
            }

            double unit = UnitWidth(final.Width, units);
            double x = 0;
            for (int k = 0; k < count; k++)
            {
                double w = SpanWidth(unit, Math.Max(1, spans[at + k]));
                InternalChildren[at + k].Arrange(new Rect(x, y, w, rowHeight));
                x += w + Gap;
            }

            y += rowHeight + Gap;
            at += count;
        }

        return final;
    }

    /// <summary>
    /// Lo que mide UNA unidad en una fila de <paramref name="units"/> unidades. La fila entera son
    /// <c>units</c> unidades y <c>units − 1</c> huecos, los lleve una tarjeta o siete: un hueco
    /// dentro de una tarjeta doble mide lo mismo que uno entre dos tarjetas, y así las columnas de
    /// dos filas distintas caen en la misma retícula.
    /// </summary>
    private double UnitWidth(double available, int units)
    {
        if (double.IsInfinity(available) || available <= 0 || units <= 0)
        {
            return MinUnitWidth;
        }

        return Math.Max(1, (available - ((units - 1) * Gap)) / units);
    }

    /// <summary>Lo que mide una tarjeta de <paramref name="span"/> unidades: los huecos van dentro.</summary>
    private double SpanWidth(double unit, int span) => (unit * span) + ((span - 1) * Gap);
}
