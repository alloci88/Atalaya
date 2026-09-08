using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Atalaya.App.Controls;

/// <summary>Un trozo de un capítulo (F17.1): el periodo en que el ciclo tuvo UNA temática.</summary>
/// <param name="From">Inicio del periodo, en hora local. Decide la proporción del trozo dentro del bloque.</param>
/// <param name="To">Fin del periodo, en hora local (el último llega al fin del ciclo, o a ahora).</param>
/// <param name="TooltipLines">Las líneas del tooltip de ESTE trozo: su temática y sus fechas.</param>
public sealed record RibbonSlice(Brush Fill, DateTime From, DateTime To, IReadOnlyList<string> TooltipLines);

/// <summary>Un capítulo de la secuencia (F17.2): un ciclo de una aplicación, partido por sus temáticas.</summary>
/// <param name="Label">«C2 · Seguridad» —o «C3 · Rendimiento → Seguridad»—. Siempre legible.</param>
/// <param name="Dates">Sus fechas, escritas: «14 ago – 2 sept», o «2 sept» si empezó y acabó el mismo día.</param>
/// <param name="Slices">Los periodos de temática, en orden. Uno solo en el caso normal.</param>
/// <param name="From">Inicio del ciclo, en hora local. Solo sirve para contar el hueco con el anterior.</param>
/// <param name="To">Fin del ciclo (o ahora), en hora local. Solo sirve para contar el hueco con el siguiente.</param>
/// <param name="IsOpen">El ciclo sigue abierto: va el último, con remate de «en curso».</param>
/// <param name="EndIsKnown">False cuando la fecha de cierre no se pudo recuperar: borde derecho a puntos.</param>
/// <param name="TooltipLines">El tooltip del ciclo entero; el de cada trozo lo lleva el trozo.</param>
/// <param name="Payload">Lo que se entrega al mando al pulsar. La cinta no sabe qué es.</param>
/// <param name="ShortLabel">
/// «C4»: lo que se escribe cuando el rótulo entero no cabe en el bloque. Con eje de tiempo el
/// ancho es la duración, así que un ciclo corto sobre un eje largo mide poco por definición y
/// quedarse sin identificador sería quedarse sin poder señalarlo.
/// </param>
/// <param name="Coverage">
/// La cobertura del ciclo, 0..1 —auditadas de auditables, la misma cifra del tooltip (F35-4 §1.2)—,
/// y <b>null cuando no se conserva el inventario</b> del ciclo: entonces no hay relleno ni
/// porcentaje. Un 0 % diría que no se auditó nada, y lo que pasa es que no se sabe (D-318).
/// </param>
public sealed record RibbonSpan(
    string Label,
    string Dates,
    IReadOnlyList<RibbonSlice> Slices,
    DateTime From,
    DateTime To,
    bool IsOpen,
    bool EndIsKnown,
    IReadOnlyList<string> TooltipLines,
    object? Payload = null,
    string ShortLabel = "",
    double? Coverage = null)
{
    /// <summary>El relleno de la temática vigente: la última.</summary>
    public Brush Fill => Slices[^1].Fill;
}

/// <summary>Una fila de la secuencia: una aplicación con sus capítulos, en orden. Sin ninguno, se dice.</summary>
/// <param name="EmptyText">Lo que se escribe en la fila cuando la aplicación no tiene ningún ciclo.</param>
/// <param name="Dot">
/// El color de la aplicación (D-314), para el punto que va delante de su nombre (F35-4 §1.4): el
/// mismo que lleva en la gráfica de coste y en su rosco de cobertura. Null lo omite.
/// </param>
public sealed record RibbonTrack(
    string Name,
    IReadOnlyList<RibbonSpan> Spans,
    string EmptyText = "sin ciclos registrados",
    Brush? Dot = null);

/// <summary>
/// La secuencia de ciclos de Métricas (F17.2): una fila por aplicación y, en cada fila, sus
/// ciclos <b>en orden, uno tras otro, como bloques de ancho fijo</b>. Sin eje temporal.
/// <para>
/// <b>Por qué capítulos y no calendario.</b> Los ciclos son eventos escasos y de duración dispar
/// —unos de horas, otros de semanas, unos pocos al año por aplicación—, y un eje de calendario
/// condenaba la vista a dos meses de vacío para unos milímetros de contenido contra el borde
/// derecho (F17, F17.1). La pregunta que responde esta gráfica es «con qué lupas se ha mirado
/// esta aplicación, en qué orden y con qué resultado», y eso necesita orden, no calendario. El
/// tiempo no desaparece: cada bloque lleva sus fechas debajo, y los huecos entre ciclos se
/// CUENTAN («3 semanas sin auditar») en vez de dibujarse.
/// </para>
/// <para>
/// Se conserva de F17.1 lo que ya estaba bien: la columna de nombres fija fuera del área que se
/// desplaza (la fila es indivisible), la fila vacía rotulada, el desplazamiento propio —jamás el
/// de la página— y la vista arrancando por el final, donde está el ciclo más reciente.
/// </para>
/// </summary>
public sealed class CycleRibbon : Grid
{
    internal const double PadTop = 8;
    internal const double PadRight = 14;
    internal const double RowHeight = 44;
    internal const double RowGap = 10;
    /// <summary>Lo que ocupa el eje debajo de las filas: sus marcas de fecha.</summary>
    internal const double AxisHeight = 16;

    /// <summary>El punto de color de la aplicación, delante de su nombre (F35-4 §1.4).</summary>
    internal const double DotSize = 8;

    private const double DotGap = 6;
    private const double GutterCap = 170;
    private const double GutterPad = 12;
    private const double OpenFade = 16;
    private const double ChangeMark = 2;
    private const double LabelSize = 11;
    private const double DatesSize = 10;


    private readonly Canvas _names = new();
    private readonly Canvas _plot = new();
    private readonly ScrollViewer _scroll;

    /// <summary>
    /// Con qué ancho se dibujó lo que hay, y si quedó algo pendiente. Es la misma guarda que
    /// D-1042 puso en <see cref="ChartPlot"/>, y por el mismo motivo: la cinta nace dentro de un
    /// bloque que empieza colapsado, así que el primer dibujo se encuentra sin ancho y se va. Que
    /// se dibuje o no no puede depender de en qué orden asigne quien llama.
    /// </summary>
    private double _drawnWidth = -1;

    private bool _stale = true;



    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<RibbonTrack>), typeof(CycleRibbon),
        new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush), typeof(Brush), typeof(CycleRibbon),
        new PropertyMetadata(Brushes.Gray, OnVisualChanged));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(CycleRibbon),
        new PropertyMetadata(Brushes.Black, OnVisualChanged));

    public static readonly DependencyProperty SpanCommandProperty = DependencyProperty.Register(
        nameof(SpanCommand), typeof(ICommand), typeof(CycleRibbon),
        new PropertyMetadata(null));

    /// <summary>
    /// El neutro de <b>lo que queda por auditar</b> dentro de un bloque (F35-4 §1.2): el mismo
    /// relleno apagado que usa el rosco de cobertura para lo pendiente. Llega de fuera porque
    /// dentro de este control no se decide ningún color (D-832).
    /// </summary>
    public static readonly DependencyProperty PendingBrushProperty = DependencyProperty.Register(
        nameof(PendingBrush), typeof(Brush), typeof(CycleRibbon),
        new PropertyMetadata(Brushes.LightGray, OnVisualChanged));

    /// <summary>
    /// El extremo derecho del eje. Lo pone quien tiene el reloj, no el control: así se puede fijar
    /// en un test y la cinta no depende de la hora de la máquina para poder afirmarse.
    /// </summary>
    public static readonly DependencyProperty TodayProperty = DependencyProperty.Register(
        nameof(Today), typeof(DateTime), typeof(CycleRibbon),
        new PropertyMetadata(DateTime.Now, OnDataChanged));

    public IReadOnlyList<RibbonTrack>? Tracks
    {
        get => (IReadOnlyList<RibbonTrack>?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    /// <summary>El color de los separadores de hueco y de la marca de cambio. Del tema.</summary>
    public Brush AxisBrush
    {
        get => (Brush)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <summary>El color del nombre de cada fila. Del tema, no de la cinta.</summary>
    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <summary>Lo que se ejecuta al pulsar un bloque, con su <see cref="RibbonSpan.Payload"/>.</summary>
    public ICommand? SpanCommand
    {
        get => (ICommand?)GetValue(SpanCommandProperty);
        set => SetValue(SpanCommandProperty, value);
    }

    /// <inheritdoc cref="PendingBrushProperty"/>
    public Brush PendingBrush
    {
        get => (Brush)GetValue(PendingBrushProperty);
        set => SetValue(PendingBrushProperty, value);
    }

    /// <inheritdoc cref="TodayProperty"/>
    public DateTime Today
    {
        get => (DateTime)GetValue(TodayProperty);
        set => SetValue(TodayProperty, value);
    }

    /// <summary>La tinta sobre el neutro de lo pendiente: la del tema, no una del control.</summary>
    private Brush PendingInk => TextBrush;

    /// <summary>Dónde quedó cada cosa. Se puede afirmar sin mirar un solo pincel.</summary>
    internal RibbonGeometry Geometry { get; private set; } = RibbonGeometry.For(Array.Empty<RibbonTrack>(), DateTime.Now, 0);

    /// <summary>La línea vertical de hoy, para poder afirmar que está y dónde.</summary>
    internal Line? TodayLine { get; private set; }

    /// <summary>Los tramos sin auditar dibujados, con su geometría.</summary>
    internal List<(int Row, RibbonGap Gap, Line Line)> GapLines { get; } = new();

    /// <summary>Los rótulos del eje que SÍ se escribieron, después de diluir los que no caben.</summary>
    internal List<TextBlock> AxisLabels { get; } = new();

    /// <summary>El área desplazable, para poder afirmar dónde arranca y qué se ve.</summary>
    internal ScrollViewer Scroll => _scroll;

    /// <summary>La columna fija de nombres.</summary>
    internal Canvas Names => _names;

    /// <summary>El lienzo de las filas.</summary>
    internal Canvas Plot => _plot;

    /// <summary>El rótulo de cada fila, por índice.</summary>
    internal IReadOnlyList<TextBlock> NameLabels { get; private set; } = Array.Empty<TextBlock>();

    /// <summary>Las formas de cada capítulo (una por trozo), con la fila a la que pertenecen.</summary>
    internal IReadOnlyList<(int Row, RibbonSpan Span, Rectangle Shape)> SpanShapes { get; private set; }
        = Array.Empty<(int, RibbonSpan, Rectangle)>();

    /// <summary>Los rótulos de cada bloque (etiqueta y fechas, si se pintaron), por capítulo.</summary>
    internal IReadOnlyList<(RibbonSpan Span, TextBlock Label, TextBlock? Dates)> BlockLabels { get; private set; }
        = Array.Empty<(RibbonSpan, TextBlock, TextBlock?)>();

    /// <summary>Los separadores de hueco pintados, con su fila y su texto.</summary>
    internal IReadOnlyList<(int Row, TextBlock Text)> GapLabels { get; private set; } = Array.Empty<(int, TextBlock)>();

    /// <summary>El rótulo de «sin ciclos» de cada fila vacía, con su fila.</summary>
    internal IReadOnlyList<(int Row, TextBlock Text)> EmptyLabels { get; private set; } = Array.Empty<(int, TextBlock)>();


    public CycleRibbon()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _names.VerticalAlignment = VerticalAlignment.Top;
        SetColumn(_names, 0);
        Children.Add(_names);

        _plot.ClipToBounds = true;
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Top,
            Content = _plot,
        };
        SetColumn(_scroll, 1);
        Children.Add(_scroll);

        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => Rebuild();

        // La misma guarda que D-1042 puso en `ChartPlot`: mientras quede algo por dibujar —o el
        // ancho haya cambiado—, la siguiente pasada de layout lo dibuja. La cinta vive dentro de
        // un bloque que empieza colapsado, así que el primer dibujo se encuentra sin ancho.
        LayoutUpdated += (_, _) =>
        {
            double width = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : 0;
            if (_stale || Math.Abs(_drawnWidth - Math.Max(0, width - PadRight)) > 0.5)
            {
                Rebuild();
            }
        };
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CycleRibbon)d).Rebuild();

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CycleRibbon)d).Rebuild();

    /// <summary>Dónde empieza la fila <paramref name="row"/>. La misma cuenta para el nombre y para los bloques.</summary>
    internal static double RowTop(int row) => PadTop + row * (RowHeight + RowGap);

    private void Rebuild()
    {
        _names.Children.Clear();
        _plot.Children.Clear();
        var names = new List<TextBlock>();
        var shapes = new List<(int, RibbonSpan, Rectangle)>();
        var labels = new List<(RibbonSpan, TextBlock, TextBlock?)>();
        var gaps = new List<(int, TextBlock)>();
        var empties = new List<(int, TextBlock)>();
        GapLines.Clear();
        AxisLabels.Clear();
        TodayLine = null;
        _stale = true;

        IReadOnlyList<RibbonTrack> tracks = Tracks ?? Array.Empty<RibbonTrack>();
        if (tracks.Count == 0)
        {
            _names.Width = 0;
            _names.Height = 0;
            _plot.Width = 0;
            _plot.Height = 0;
            Geometry = RibbonGeometry.For(tracks, Today, 0);
            Publish();
            return;
        }

        double gutter = Math.Min(GutterCap, tracks.Max(t => Measure(t.Name, 12).Width)) + DotSize + DotGap + GutterPad;
        double height = RowTop(tracks.Count) - RowGap + PadTop + AxisHeight;

        // EL ÁREA DE DIBUJO ES LA QUE HAY: con eje de tiempo la cinta entera cabe siempre —va del
        // primer ciclo a hoy—, así que no hay nada que desplazar. El ancho llega por el viewport, y
        // hasta que el layout no lo dice vale 0: por eso se vuelve a dibujar cuando lo sabe.
        double plotWidth = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth - gutter;
        plotWidth = Math.Max(0, plotWidth - PadRight);
        if (plotWidth <= 0)
        {
            _names.Width = gutter;
            _names.Height = height;
            Geometry = RibbonGeometry.For(tracks, Today, 0);
            Publish();
            return;
        }

        RibbonGeometry geometry = RibbonGeometry.For(tracks, Today, plotWidth);
        Geometry = geometry;

        double axisTop = RowTop(tracks.Count) - RowGap + 2;
        DrawAxis(geometry, axisTop, height);

        for (int row = 0; row < tracks.Count; row++)
        {
            RibbonTrack track = tracks[row];
            double top = RowTop(row);

            TextBlock name = NameLabel(track.Name, gutter - GutterPad - DotSize - DotGap);
            Canvas.SetLeft(name, DotSize + DotGap);
            Canvas.SetTop(name, top + (RowHeight - Measure(track.Name, 12).Height) / 2);
            _names.Children.Add(name);
            names.Add(name);

            // EL PUNTO DE COLOR DE LA APLICACIÓN (D-314), delante de su nombre: el mismo color con
            // el que sale en la gráfica de coste y en su rosco.
            if (track.Dot is { } dot)
            {
                var bullet = new Rectangle
                {
                    Width = DotSize,
                    Height = DotSize,
                    RadiusX = DotSize / 2,
                    RadiusY = DotSize / 2,
                    Fill = dot,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(bullet, 0);
                Canvas.SetTop(bullet, top + (RowHeight - DotSize) / 2);
                _names.Children.Add(bullet);
            }

            if (track.Spans.Count == 0)
            {
                TextBlock empty = Muted(track.EmptyText, 11.5);
                empty.FontStyle = FontStyles.Italic;
                Canvas.SetLeft(empty, 4);
                Canvas.SetTop(empty, top + (RowHeight - Measure(track.EmptyText, 11.5).Height) / 2);
                _plot.Children.Add(empty);
                empties.Add((row, empty));
                continue;
            }

            foreach (RibbonGap gap in geometry.Gaps.Where(g => g.Row == row))
            {
                DrawGap(row, gap, top, gaps);
            }

            foreach (RibbonBlock block in geometry.Blocks.Where(b => b.Row == row))
            {
                DrawBlock(block, top, shapes, labels);
            }
        }

        _names.Width = gutter;
        _names.Height = height;
        _plot.Width = plotWidth + PadRight;
        _plot.Height = height;
        _stale = false;
        _drawnWidth = plotWidth;
        Publish();

        void Publish()
        {
            NameLabels = names;
            SpanShapes = shapes;
            BlockLabels = labels;
            GapLabels = gaps;
            EmptyLabels = empties;
        }
    }

    /// <summary>
    /// El eje: marcas de fecha redondas abajo, sus guías verticales apagadas, y la <b>línea de
    /// hoy</b>, que es el ancla de quien mira (D-593). Hoy no es una marca más: lleva línea propia.
    /// </summary>
    private void DrawAxis(RibbonGeometry geometry, double axisTop, double height)
    {
        // Los rótulos se DILUYEN cuando no caben: se escribe uno de cada n, y se cuenta desde el
        // último hacia atrás para que la marca más reciente salga siempre. Las guías se dibujan
        // todas — son las que sitúan—; lo que se ahorra es la tinta que se pisaría. Es la misma
        // regla que el eje de `ChartPlot`, y medida, no estimada por caracteres (D-832).
        double widest = geometry.Ticks.Count == 0
            ? 0
            : geometry.Ticks.Max(t => Measure(t.Text, DatesSize).Width);
        double slot = geometry.Ticks.Count > 1
            ? geometry.Width / (geometry.Ticks.Count - 1)
            : geometry.Width;
        int every = Math.Max(1, (int)Math.Ceiling((widest + 10) / Math.Max(1, slot)));

        for (int i = 0; i < geometry.Ticks.Count; i++)
        {
            RibbonTick tick = geometry.Ticks[i];
            bool writes = (geometry.Ticks.Count - 1 - i) % every == 0;
            var guide = new Line
            {
                X1 = tick.X,
                X2 = tick.X,
                Y1 = PadTop,
                Y2 = axisTop,
                Stroke = AxisBrush,
                StrokeThickness = 1,
                Opacity = 0.14,
                IsHitTestVisible = false,
            };
            _plot.Children.Add(guide);

            if (!writes)
            {
                continue;
            }

            TextBlock label = Muted(tick.Text, DatesSize);
            Size size = Measure(tick.Text, DatesSize);
            Canvas.SetLeft(label, Math.Max(0, tick.X - size.Width / 2));
            Canvas.SetTop(label, axisTop + 2);
            _plot.Children.Add(label);
            AxisLabels.Add(label);
        }

        var today = new Line
        {
            X1 = geometry.TodayX,
            X2 = geometry.TodayX,
            Y1 = PadTop - 2,
            Y2 = axisTop,
            Stroke = AxisBrush,
            StrokeThickness = 1,
            Opacity = 0.7,
            StrokeDashArray = new DoubleCollection(new double[] { 3, 2 }),
            IsHitTestVisible = false,
        };
        _plot.Children.Add(today);
        TodayLine = today;
    }

    /// <summary>
    /// El hueco sin auditar: el tramo entre dos ciclos, a trazos y en gris, con sus días encima
    /// <b>si caben</b> (F35-4 §1.3). Ocupa lo que duró, que es de lo que se trata.
    /// </summary>
    private void DrawGap(int row, RibbonGap gap, double top, List<(int, TextBlock)> gaps)
    {
        var line = new Line
        {
            X1 = gap.Left,
            X2 = gap.Right,
            Y1 = top + RowHeight / 2,
            Y2 = top + RowHeight / 2,
            Stroke = AxisBrush,
            StrokeThickness = 1,
            Opacity = 0.5,
            StrokeDashArray = new DoubleCollection(new double[] { 2, 3 }),
            IsHitTestVisible = false,
        };
        _plot.Children.Add(line);
        GapLines.Add((row, gap, line));

        if (gap.Text.Length == 0)
        {
            return;
        }

        Size size = Measure(gap.Text, DatesSize);
        if (size.Width + 4 > gap.Width)
        {
            return;   // no cabe: el hueco se ve igual, y el tooltip del bloque trae las fechas
        }

        TextBlock label = Muted(gap.Text, DatesSize);
        Canvas.SetLeft(label, gap.Left + (gap.Width - size.Width) / 2);
        Canvas.SetTop(label, top + RowHeight / 2 - size.Height - 2);
        _plot.Children.Add(label);
        gaps.Add((row, label));
    }

    private void DrawBlock(RibbonBlock block, double top,
        List<(int, RibbonSpan, Rectangle)> shapes, List<(RibbonSpan, TextBlock, TextBlock?)> labels)
    {
        RibbonSpan span = block.Span;
        double x = block.Left;
        double width = block.Width;

        // EL FONDO: lo que queda por auditar, en el neutro apagado del rosco de cobertura (D-316).
        // Un rosco de cobertura no habla de gravedad y esto tampoco: aquí el color dice la lupa y
        // el relleno dice cuánto se miró.
        var track = new Rectangle
        {
            Width = width,
            Height = RowHeight,
            RadiusX = 4,
            RadiusY = 4,
            Fill = PendingBrush,
            Cursor = Cursors.Hand,
            ToolTip = BuildTooltip(span.TooltipLines),
            Tag = span,
        };
        Canvas.SetLeft(track, x);
        Canvas.SetTop(track, top);
        Wire(track, span);
        _plot.Children.Add(track);
        shapes.Add((block.Row, span, track));

        // EL RELLENO, de izquierda a derecha y proporcional a la cobertura, con el color de la
        // temática. Los trozos de lupa siguen en su sitio del TIEMPO —el bloque ya es una escala
        // temporal—, así que un ciclo que cambió de lupa enseña las dos donde tocan, y el relleno
        // recorta por la derecha lo que todavía no se ha auditado.
        double filled = block.FilledWidth;
        if (filled > 0)
        {
            double spanDays = Math.Max(1.0 / 1440, (span.To - span.From).TotalDays);
            double cursor = 0;
            for (int i = 0; i < span.Slices.Count; i++)
            {
                RibbonSlice slice = span.Slices[i];
                bool last = i == span.Slices.Count - 1;
                double sliceWidth = last
                    ? width - cursor
                    : Math.Max(0, width * Math.Max(0, (slice.To - slice.From).TotalDays) / spanDays);
                double drawn = Math.Min(sliceWidth, filled - cursor);
                if (drawn > 0)
                {
                    var fill = new Rectangle
                    {
                        Width = drawn,
                        Height = RowHeight,
                        RadiusX = i == 0 ? 4 : 0,
                        RadiusY = i == 0 ? 4 : 0,
                        Fill = span.IsOpen && last ? OpenFill(slice.Fill, drawn) : slice.Fill,
                        Cursor = Cursors.Hand,
                        ToolTip = BuildTooltip(span.Slices.Count > 1
                            ? slice.TooltipLines.Concat(span.TooltipLines).ToList()
                            : span.TooltipLines),
                        Tag = span,
                    };
                    Canvas.SetLeft(fill, x + cursor);
                    Canvas.SetTop(fill, top);
                    Wire(fill, span);
                    _plot.Children.Add(fill);
                    shapes.Add((block.Row, span, fill));
                }

                cursor += sliceWidth;
                if (cursor >= filled)
                {
                    break;
                }
            }

            // La marca del cambio de lupa, en su sitio del tiempo y de alto completo: si cayera
            // solo dentro del relleno desaparecería en cuanto la cobertura fuera corta.
            double mark = 0;
            for (int i = 0; i < span.Slices.Count - 1; i++)
            {
                mark += width * Math.Max(0, (span.Slices[i].To - span.Slices[i].From).TotalDays) / spanDays;
                var notch = new Rectangle
                {
                    Width = ChangeMark,
                    Height = RowHeight,
                    Fill = AxisBrush,
                    Opacity = 0.9,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(notch, x + Math.Min(width - ChangeMark, mark) - (ChangeMark / 2));
                Canvas.SetTop(notch, top);
                _plot.Children.Add(notch);
            }
        }

        // El fin sin fecha recuperable se dice también en el dibujo: borde derecho a puntos (D-831).
        if (!span.EndIsKnown && !span.IsOpen)
        {
            var edge = new Line
            {
                X1 = x + width,
                X2 = x + width,
                Y1 = top,
                Y2 = top + RowHeight,
                Stroke = AxisBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection(new double[] { 1.5, 2 }),
                IsHitTestVisible = false,
            };
            _plot.Children.Add(edge);
        }

        // EL RÓTULO. Con eje de tiempo el ancho es la duración, así que hay bloques estrechos por
        // definición: se escribe el rótulo entero si cabe, el corto («C4») si solo cabe él, y nada
        // —con el tooltip— cuando ni eso. Medido, no estimado por caracteres (D-832).
        double inner = width - 12;
        Brush ink = filled > Measure(span.Label, LabelSize).Width + 12
            ? InkFor(span.Slices[0].Fill)
            : PendingInk;

        string? text = Fits(span.Label, inner) ? span.Label
            : span.ShortLabel.Length > 0 && Fits(span.ShortLabel, inner) ? span.ShortLabel
            : null;
        if (text is null)
        {
            // Ni el corto cabe: se calla y queda el tooltip. Se apunta igual, con un rótulo vacío,
            // para que se pueda afirmar que este bloque NO escribió nada.
            labels.Add((span, new TextBlock { Text = string.Empty }, null));
            return;
        }

        Size labelSize = Measure(text, LabelSize);
        var label = new TextBlock
        {
            Text = text,
            FontSize = LabelSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            IsHitTestVisible = false,
        };

        double datesHeight = Measure(span.Dates, DatesSize).Height;
        bool showDates = ReferenceEquals(text, span.Label)
            && span.Dates.Length > 0
            && Fits(span.Dates, inner, DatesSize)
            && labelSize.Height + datesHeight + 2 <= RowHeight - 6;

        double blockHeight = showDates ? labelSize.Height + 2 + datesHeight : labelSize.Height;
        double labelTop = top + Math.Max(3, (RowHeight - blockHeight) / 2);
        Canvas.SetLeft(label, x + 6);
        Canvas.SetTop(label, labelTop);
        _plot.Children.Add(label);

        TextBlock? dates = null;
        if (showDates)
        {
            dates = new TextBlock
            {
                Text = span.Dates,
                FontSize = DatesSize,
                Foreground = ink,
                Opacity = 0.85,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(dates, x + 6);
            Canvas.SetTop(dates, labelTop + labelSize.Height + 2);
            _plot.Children.Add(dates);
        }

        labels.Add((span, label, dates));

        bool Fits(string t, double available, double size = LabelSize)
            => available > 0 && Measure(t, size).Width <= available;
    }

    /// <summary>El clic de un bloque: el mismo mando y la misma carga de siempre (D-831).</summary>
    private void Wire(Rectangle shape, RibbonSpan span)
    {
        ToolTipService.SetInitialShowDelay(shape, 120);
        ToolTipService.SetBetweenShowDelay(shape, 0);
        object? payload = span.Payload;
        shape.MouseLeftButtonUp += (_, e) =>
        {
            if (SpanCommand?.CanExecute(payload) == true)
            {
                SpanCommand.Execute(payload);
                e.Handled = true;
            }
        };
    }

    private TextBlock Muted(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = 0.55,
        Foreground = TextBrush,
        IsHitTestVisible = false,
    };

    /// <summary>
    /// El nombre entero si cabe; si no, con elipsis por el MEDIO —el final de un nombre suele ser lo
    /// que lo distingue— y el nombre completo en el tooltip. Nunca cortado a secas.
    /// </summary>
    private TextBlock NameLabel(string name, double maxWidth)
    {
        string text = name;
        if (Measure(text, 12).Width > maxWidth)
        {
            for (int max = name.Length - 1; max >= 3; max--)
            {
                text = MiddleEllipsis(name, max);
                if (Measure(text, 12).Width <= maxWidth)
                {
                    break;
                }
            }
        }

        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = TextBrush,
            ToolTip = name,
        };
    }

    internal static string MiddleEllipsis(string name, int max)
    {
        if (name.Length <= max)
        {
            return name;
        }

        int keep = Math.Max(1, max - 1);
        int head = (keep + 1) / 2;
        int tail = keep - head;
        return name[..head] + "…" + (tail > 0 ? name[^tail..] : string.Empty);
    }

    /// <summary>
    /// El remate de «en curso»: el bloque se desvanece en sus últimos píxeles. Un ciclo abierto no
    /// tiene borde derecho porque no ha terminado; dibujarle uno lo haría igual que un cerrado.
    /// </summary>
    private static Brush OpenFill(Brush fill, double width)
    {
        if (fill is not SolidColorBrush solid || width <= OpenFade * 2)
        {
            return fill;
        }

        double stop = 1 - OpenFade / width;
        Color faded = solid.Color;
        faded.A = 0x40;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };
        brush.GradientStops.Add(new GradientStop(solid.Color, 0));
        brush.GradientStops.Add(new GradientStop(solid.Color, stop));
        brush.GradientStops.Add(new GradientStop(faded, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// La tinta del rótulo sobre su bloque: negro o blanco, decidido por la luminancia del relleno
    /// y no por el tema (F10.1, D-649: la tinta va por paso, medida).
    /// </summary>
    internal static Brush InkFor(Brush fill)
        => fill is SolidColorBrush solid && Luminance(solid.Color) > 0.3 ? Brushes.Black : Brushes.White;

    internal static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static object BuildTooltip(IReadOnlyList<string> lines)
    {
        var panel = new StackPanel();
        for (int i = 0; i < lines.Count; i++)
        {
            panel.Children.Add(new TextBlock
            {
                Text = lines[i],
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, i == 0 ? 0 : 1, 0, i == 0 ? 4 : 1),
                MaxWidth = 360,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return panel;
    }

    private Size Measure(string text, double size) => new(Format(text, size, FontWeights.Normal, null).Width, Format(text, size, FontWeights.Normal, null).Height);

    private FormattedText Format(string text, double size, FontWeight weight, double? maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            size,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth is { } w)
        {
            formatted.MaxTextWidth = w;
        }

        return formatted;
    }
}
