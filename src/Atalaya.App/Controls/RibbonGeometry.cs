using System.Globalization;

namespace Atalaya.App.Controls;

/// <summary>Un bloque colocado sobre el eje: qué ciclo, en qué fila, y dónde empieza y acaba.</summary>
public sealed record RibbonBlock(int Row, RibbonSpan Span, double Left, double Width)
{
    public double Right => Left + Width;

    /// <summary>Lo que ocupa el relleno de cobertura, de izquierda a derecha. 0 sin inventario.</summary>
    public double FilledWidth => Span.Coverage is { } c ? Width * Math.Clamp(c, 0, 1) : 0;
}

/// <summary>
/// El tramo <b>sin auditar</b> entre dos ciclos consecutivos de una aplicación (F35-4 §1.3). El
/// subtítulo decía que los huecos se cuentan; ahora, con eje de verdad, además se ven.
/// </summary>
/// <param name="Days">Los días del hueco, redondeados. Cero cuando dura menos de un día.</param>
/// <param name="Text">«12 d», o vacío por debajo del día: no hay número que valga la pena escribir.</param>
public sealed record RibbonGap(int Row, double Left, double Width, int Days, string Text)
{
    public double Right => Left + Width;
}

/// <summary>Una marca del eje: una fecha redonda con su sitio y su rótulo.</summary>
public sealed record RibbonTick(DateTime When, double X, string Text);

/// <summary>
/// <b>Dónde va cada cosa de la cinta</b> (F35-4 §1.1), calculado sin pintar un píxel.
/// <para>
/// Hasta aquí la cinta era una lista de capítulos: bloques de ancho fijo, uno tras otro, sin eje.
/// Contestaba «con qué lupas y en qué orden», y para eso servía; lo que no podía contestar es
/// «cuánto duró cada uno y cuánto se tardó en volver», porque un ciclo de cuatro días y uno de seis
/// meses medían lo mismo y estaban en el mismo sitio. Ahora hay <b>un eje de tiempo real,
/// compartido por todas las aplicaciones</b>: cada bloque empieza en su fecha y mide su duración,
/// y los huecos ocupan lo que duraron.
/// </para>
/// <para>
/// <b>Separado del dibujo</b>, por lo mismo que la geometría de F17.2 (D-832): así se puede afirmar
/// la posición y el ancho de cada bloque sin montar una ventana — y desde D-1042, con una gráfica
/// se comprueba lo que se dibuja, no solo lo que se calcula.
/// </para>
/// </summary>
public sealed class RibbonGeometry
{
    /// <summary>
    /// Lo mínimo que mide el eje: <b>siete días</b>, la misma regla que el resto de ejes del panel
    /// (D-1040). Sin suelo, una aplicación con un solo ciclo de dos horas llenaría la pantalla de
    /// lado a lado y la escala diría lo contrario de lo que hay; con más suelo del necesario, un
    /// historial corto se dibujaría contra el borde derecho de un desierto. El eje empieza donde
    /// empieza el primer tramo, y solo se alarga hacia atrás cuando ese inicio queda a menos de
    /// una semana de hoy.
    /// </summary>
    public const double MinAxisDays = 7;

    /// <summary>
    /// Lo mínimo que mide un bloque. No es un ancho «legible» como el de F17.2 —el ancho es la
    /// duración y se respeta—: es que un ciclo de minutos sobre un eje de meses caería por debajo
    /// del píxel y no habría dónde pulsar.
    /// </summary>
    public const double MinBlockWidth = 3;

    private RibbonGeometry(
        DateTime from,
        DateTime to,
        double width,
        IReadOnlyList<RibbonBlock> blocks,
        IReadOnlyList<RibbonGap> gaps,
        IReadOnlyList<RibbonTick> ticks)
    {
        From = from;
        To = to;
        Width = width;
        Blocks = blocks;
        Gaps = gaps;
        Ticks = ticks;
    }

    /// <summary>El extremo izquierdo: el inicio del primer tramo, o el suelo de una semana.</summary>
    public DateTime From { get; }

    /// <summary>El extremo derecho: HOY. La cinta llega hasta hoy siempre (D-593).</summary>
    public DateTime To { get; }

    /// <summary>Lo que mide el área de dibujo.</summary>
    public double Width { get; }

    public IReadOnlyList<RibbonBlock> Blocks { get; }

    public IReadOnlyList<RibbonGap> Gaps { get; }

    public IReadOnlyList<RibbonTick> Ticks { get; }

    /// <summary>Dónde cae hoy: el extremo derecho, donde va la línea vertical.</summary>
    public double TodayX => X(To);

    /// <summary>Cuántos días abarca el eje. Nunca menos de <see cref="MinAxisDays"/>.</summary>
    public double Days => (To - From).TotalDays;

    /// <summary>Dónde cae una fecha. Fuera del eje se recorta a sus extremos.</summary>
    public double X(DateTime when)
    {
        double days = Days;
        if (days <= 0)
        {
            return 0;
        }

        return Math.Clamp((when - From).TotalDays / days, 0, 1) * Width;
    }

    /// <summary>
    /// La geometría de una cinta. <paramref name="today"/> es el extremo derecho —no se toma del
    /// reloj aquí, para que se pueda fijar—, y <paramref name="width"/> es el área de dibujo.
    /// </summary>
    public static RibbonGeometry For(IReadOnlyList<RibbonTrack> tracks, DateTime today, double width)
    {
        var spans = tracks.SelectMany(t => t.Spans).ToList();

        // El eje termina en HOY. Un tramo no puede acabar después, pero si el reloj y el dato no
        // cuadraran, manda el dato: recortar un bloque para que el eje quede redondo sería mentir.
        DateTime to = spans.Count == 0 ? today : spans.Max(s => s.To) > today ? spans.Max(s => s.To) : today;

        // Y empieza en el primer tramo de CUALQUIER aplicación —el eje es uno solo, compartido—,
        // alargándose hacia atrás solo si ese inicio no llega al suelo de una semana.
        DateTime earliest = spans.Count == 0 ? to : spans.Min(s => s.From);
        DateTime floor = to.AddDays(-MinAxisDays);
        DateTime from = earliest < floor ? earliest : floor;

        var geometry = new RibbonGeometry(
            from, to, Math.Max(0, width),
            Array.Empty<RibbonBlock>(), Array.Empty<RibbonGap>(), Array.Empty<RibbonTick>());

        var blocks = new List<RibbonBlock>();
        var gaps = new List<RibbonGap>();
        for (int row = 0; row < tracks.Count; row++)
        {
            RibbonSpan? previous = null;
            foreach (RibbonSpan span in tracks[row].Spans)
            {
                if (previous is not null && span.From > previous.To)
                {
                    double gapLeft = geometry.X(previous.To);
                    int days = (int)Math.Round((span.From - previous.To).TotalDays);
                    gaps.Add(new RibbonGap(
                        row,
                        gapLeft,
                        Math.Max(0, geometry.X(span.From) - gapLeft),
                        days,
                        days >= 1 ? $"{days} d" : string.Empty));
                }

                // El mínimo se gana por la IZQUIERDA: el fin del bloque es la fecha de fin, y ésa
                // no se mueve — un ciclo abierto tiene que morir en la línea de hoy, no pasarse.
                double right = geometry.X(span.To);
                double left = Math.Min(geometry.X(span.From), Math.Max(0, right - MinBlockWidth));
                blocks.Add(new RibbonBlock(row, span, left, Math.Max(MinBlockWidth, right - left)));
                previous = span;
            }
        }

        return new RibbonGeometry(from, to, geometry.Width, blocks, gaps, TicksFor(geometry));
    }

    /// <summary>
    /// Las marcas del eje, en fechas <b>redondas</b>: lunes mientras el eje no pase de dos meses,
    /// día 1 de cada mes hasta poco más de un año, y trimestres después. Es la misma escalera de
    /// grano de F17.2, con la diferencia de que ahora las marcas caen donde cae la fecha.
    /// <para>
    /// Hoy NO es una marca: es una línea propia, porque no es una fecha redonda — es el ancla.
    /// </para>
    /// </summary>
    private static IReadOnlyList<RibbonTick> TicksFor(RibbonGeometry axis)
    {
        var ticks = new List<RibbonTick>();
        if (axis.Width <= 0 || axis.Days <= 0)
        {
            return ticks;
        }

        double days = axis.Days;
        DateTime cursor;
        Func<DateTime, DateTime> next;
        string format;

        if (days <= 62)
        {
            int back = ((int)axis.From.DayOfWeek + 6) % 7;   // al lunes de esa semana
            cursor = axis.From.Date.AddDays(-back);
            next = d => d.AddDays(7);
            format = "d MMM";
        }
        else if (days <= 400)
        {
            cursor = new DateTime(axis.From.Year, axis.From.Month, 1);
            next = d => d.AddMonths(1);
            format = "MMM yy";
        }
        else
        {
            int quarter = ((axis.From.Month - 1) / 3 * 3) + 1;
            cursor = new DateTime(axis.From.Year, quarter, 1);
            next = d => d.AddMonths(3);
            format = "MMM yy";
        }

        while (cursor <= axis.To && ticks.Count < 200)
        {
            if (cursor >= axis.From)
            {
                ticks.Add(new RibbonTick(
                    cursor, axis.X(cursor), cursor.ToString(format, CultureInfo.CurrentCulture)));
            }

            cursor = next(cursor);
        }

        return ticks;
    }
}
