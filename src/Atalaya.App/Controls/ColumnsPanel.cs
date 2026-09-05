using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// Un panel de columnas iguales que <b>decide cuántas caben</b> (F26 Parte B, D-970).
/// <para>
/// <b>El problema que resuelve, y es el principio 1 hecho control.</b> El portafolio usaba un
/// <see cref="WrapPanel"/> con tarjetas de 330 px fijos: a 1920 se veía UNA tarjeta y el resto de
/// la pantalla vacío, y al estrechar la ventana la tarjeta no se enteraba —seguía midiendo 330 y
/// dejaba un margen ridículo—. Un <c>WrapPanel</c> reparte lo que le sobra en el hueco de la
/// derecha; nunca en las tarjetas.
/// </para>
/// <para>
/// <b>Cómo reparte.</b> Cuenta cuántas columnas de <see cref="MinColumnWidth"/> caben con su
/// <see cref="Gap"/>, lo limita a <see cref="MaxColumns"/>, y reparte el ancho ENTERO entre esas
/// columnas. Así una tarjeta a 1920 mide lo que le toca de un tercio y a 1280 lo que le toca de un
/// medio: reorganizar en vez de encoger, y sin dejar un canalón a la derecha.
/// </para>
/// <para>
/// <b>Por qué un panel y no un <c>UniformGrid</c> con las columnas atadas al ancho.</b> Porque el
/// número de columnas depende del ancho DISPONIBLE, que solo se conoce en el <c>Measure</c>. Atarlo
/// desde fuera obliga a un converter con el ancho de la ventana, que es el ancho equivocado —el
/// del contenido es menor, y cambia cuando el raíl se pliega—.
/// </para>
/// <para>
/// <b>El alto lo manda la fila.</b> Todas las tarjetas de una fila miden lo que la más alta: una
/// rejilla de tarjetas desiguales se lee como un montón, no como una rejilla. Es la misma razón por
/// la que las tarjetas de Métricas tienen que alinearse (F26 Parte C).
/// </para>
/// </summary>
public sealed class ColumnsPanel : Panel
{
    /// <summary>Por debajo de esto, una tarjeta deja de poder enseñar sus cifras.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(
            nameof(MinColumnWidth), typeof(double), typeof(ColumnsPanel),
            new FrameworkPropertyMetadata(420d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>El tope. Sin él, un monitor ultrapanorámico daría siete columnas de nada.</summary>
    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(
            nameof(MaxColumns), typeof(int), typeof(ColumnsPanel),
            new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(ColumnsPanel),
            new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>Cuántas columnas caben en <paramref name="available"/>. Nunca menos de una.</summary>
    public int ColumnsFor(double available) => ColumnsFor(available, InternalChildren.Count);

    /// <summary>
    /// La cuenta, con el número de tarjetas delante.
    /// <para>
    /// <b>Y NUNCA MÁS COLUMNAS QUE TARJETAS</b> (UI-0020). Con una sola aplicación, el reparto daba
    /// tres columnas y la tarjeta se quedaba con 531 de 1.635 px: el 68 % de la fila en blanco,
    /// con las mismas cuatro cifras repetidas 200 px más abajo dentro de la propia tarjeta. Una
    /// columna vacía no reparte nada — es hueco reservado para algo que no existe—, y el principio
    /// 2 dice que el espacio se reparte, no se deja.
    /// </para>
    /// </summary>
    public int ColumnsFor(double available, int items)
    {
        if (double.IsInfinity(available) || available <= 0)
        {
            return 1;
        }

        // n columnas ocupan n·min + (n−1)·gap. Se despeja la n más grande que quepa.
        int n = (int)Math.Floor((available + Gap) / (MinColumnWidth + Gap));
        int tope = Math.Max(1, Math.Min(MaxColumns, items));
        return Math.Clamp(n, 1, tope);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = ColumnsFor(availableSize.Width);
        double columnWidth = ColumnWidth(availableSize.Width, columns);

        double total = 0;
        double rowHeight = 0;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            bool endOfRow = (i + 1) % columns == 0 || i == InternalChildren.Count - 1;
            if (endOfRow)
            {
                total += rowHeight + Gap;
                rowHeight = 0;
            }
        }

        double width = double.IsInfinity(availableSize.Width) ? columns * (MinColumnWidth + Gap) : availableSize.Width;
        return new Size(width, Math.Max(0, total - Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = ColumnsFor(finalSize.Width);
        double columnWidth = ColumnWidth(finalSize.Width, columns);

        double y = 0;
        double rowHeight = 0;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            int column = i % columns;

            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            child.Arrange(new Rect(column * (columnWidth + Gap), y, columnWidth, child.DesiredSize.Height));

            bool endOfRow = column == columns - 1 || i == InternalChildren.Count - 1;
            if (endOfRow)
            {
                // Segunda pasada de la fila: todas al alto de la más alta. Se hace aquí y no en el
                // Measure porque hasta terminar la fila no se sabe cuál es.
                for (int j = i - column; j <= i; j++)
                {
                    var sibling = InternalChildren[j];
                    sibling.Arrange(new Rect((j - (i - column)) * (columnWidth + Gap), y, columnWidth, rowHeight));
                }

                y += rowHeight + Gap;
                rowHeight = 0;
            }
        }

        return finalSize;
    }

    private double ColumnWidth(double available, int columns)
    {
        if (double.IsInfinity(available) || available <= 0)
        {
            return MinColumnWidth;
        }

        return Math.Max(1, (available - ((columns - 1) * Gap)) / columns);
    }
}
