namespace Atalaya.App.Services;

/// <summary>
/// Un rectángulo del reparto, en las mismas unidades que el contenedor que se le dio. Es propio y
/// no <c>System.Windows.Rect</c> para que el algoritmo —que es aritmética y nada más— se pueda
/// probar sin arrastrar WPF.
/// </summary>
public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>Dos rectángulos que se pisan. La tolerancia absorbe el error de coma flotante.</summary>
    public bool Overlaps(TreemapRect other, double tolerance = 1e-6)
        => X + tolerance < other.Right
           && other.X + tolerance < Right
           && Y + tolerance < other.Bottom
           && other.Y + tolerance < Bottom;
}

/// <summary>Lo que sale del reparto: el elemento, su valor y el rectángulo que le tocó.</summary>
public sealed record TreemapTile<T>(T Item, double Value, TreemapRect Rect);

/// <summary>
/// El reparto <b>squarified</b> de Bruls, Huizing y van Wijk (2000): coloca los elementos por
/// tamaño decreciente en filas, eligiendo el corte que deja las celdas <b>lo más cuadradas
/// posible</b>.
/// <para>
/// <b>Por qué squarified y no el reparto por rebanadas.</b> El reparto ingenuo («slice and dice»)
/// también da áreas exactas, pero las produce como tiras larguísimas de un píxel de ancho: dos
/// celdas de la misma área se ven distintas, ninguna admite etiqueta y comparar dos tiras a ojo es
/// imposible. Squarified conserva la propiedad que hace legible un treemap —el área ES el dato— y
/// además hace que el área se pueda estimar de un vistazo.
/// </para>
/// <para>
/// <b>Las dos invariantes que se prueban.</b> Cada área es proporcional a su valor, y dos celdas
/// nunca se solapan. Un reparto que rompa cualquiera de las dos está mintiendo sobre el tamaño del
/// código, que es la mitad de lo que dice esta vista.
/// </para>
/// </summary>
public static class TreemapLayout
{
    /// <summary>Un elemento con su valor y el área que le corresponde en el contenedor.</summary>
    private readonly record struct Entry<T>(T Item, double Value, double Area);

    /// <summary>
    /// Reparte <paramref name="items"/> dentro de <paramref name="bounds"/> proporcionalmente a
    /// <paramref name="value"/>.
    /// <para>
    /// Los elementos con valor ≤ 0 <b>se descartan</b>: un rectángulo de área cero no se ve y sí
    /// consume una fila. Devuelve las celdas en el orden en que se colocaron (de mayor a menor),
    /// que es también el orden en que conviene dibujarlas.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TreemapTile<T>> Squarify<T>(
        IEnumerable<T> items, Func<T, double> value, TreemapRect bounds)
    {
        var ordered = items
            .Select(i => (Item: i, Value: value(i)))
            .Where(p => p.Value > 0)
            .OrderByDescending(p => p.Value)
            .ToList();

        var tiles = new List<TreemapTile<T>>(ordered.Count);
        if (ordered.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return tiles;
        }

        double scale = bounds.Area / ordered.Sum(p => p.Value);

        TreemapRect free = bounds;
        var row = new List<Entry<T>>();

        foreach ((T item, double v) in ordered)
        {
            var candidate = new Entry<T>(item, v, v * scale);
            double side = Math.Min(free.Width, free.Height);

            // Se añade a la fila mientras la relación de aspecto MEJORE (o no empeore). En cuanto
            // empeora, la fila se cierra: es la única decisión que toma el algoritmo.
            if (row.Count > 0 && Worst(row, side) < WorstWith(row, candidate, side))
            {
                free = Place(row, free, tiles);
                row.Clear();
            }

            row.Add(candidate);
        }

        if (row.Count > 0)
        {
            Place(row, free, tiles);
        }

        return tiles;
    }

    /// <summary>
    /// La peor relación de aspecto de una fila colocada contra un lado de longitud
    /// <paramref name="side"/>. Es la función objetivo del artículo:
    /// <c>max(side²·max / suma², suma² / (side²·min))</c>, con todo en ÁREAS.
    /// </summary>
    private static double Worst<T>(IReadOnlyList<Entry<T>> row, double side)
    {
        double sum = 0;
        double max = 0;
        double min = double.MaxValue;
        foreach (Entry<T> entry in row)
        {
            sum += entry.Area;
            max = Math.Max(max, entry.Area);
            min = Math.Min(min, entry.Area);
        }

        if (sum <= 0 || min <= 0 || side <= 0)
        {
            return double.MaxValue;
        }

        double side2 = side * side;
        double sum2 = sum * sum;
        return Math.Max(side2 * max / sum2, sum2 / (side2 * min));
    }

    /// <summary>Lo mismo, con un candidato más, sin tocar la fila.</summary>
    private static double WorstWith<T>(List<Entry<T>> row, Entry<T> candidate, double side)
    {
        row.Add(candidate);
        double worst = Worst(row, side);
        row.RemoveAt(row.Count - 1);
        return worst;
    }

    /// <summary>
    /// Coloca la fila cerrada contra el lado corto del espacio libre y devuelve lo que queda.
    /// </summary>
    private static TreemapRect Place<T>(
        IReadOnlyList<Entry<T>> row, TreemapRect free, List<TreemapTile<T>> tiles)
    {
        double rowArea = row.Sum(e => e.Area);

        // La fila se apoya en el lado CORTO: si el hueco es más ancho que alto, la fila es una
        // columna a la izquierda; si es más alto que ancho, es una banda arriba.
        bool column = free.Width >= free.Height;
        double along = column ? free.Height : free.Width;
        double across = column ? free.Width : free.Height;

        double thickness = along <= 0 ? across : Math.Min(rowArea / along, across);

        double offset = 0;
        for (int i = 0; i < row.Count; i++)
        {
            Entry<T> entry = row[i];
            double share = rowArea <= 0 ? 0 : along * entry.Area / rowArea;

            // La última celda cierra la fila exactamente: sin esto, la suma de los redondeos deja
            // una rendija de fondo al final de cada fila.
            if (i == row.Count - 1)
            {
                share = Math.Max(0, along - offset);
            }

            tiles.Add(new TreemapTile<T>(entry.Item, entry.Value, column
                ? new TreemapRect(free.X, free.Y + offset, thickness, share)
                : new TreemapRect(free.X + offset, free.Y, share, thickness)));
            offset += share;
        }

        return column
            ? new TreemapRect(free.X + thickness, free.Y, Math.Max(0, free.Width - thickness), free.Height)
            : new TreemapRect(free.X, free.Y + thickness, free.Width, Math.Max(0, free.Height - thickness));
    }
}
