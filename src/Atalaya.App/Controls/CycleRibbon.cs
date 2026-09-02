using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Atalaya.App.Controls;

/// <summary>Un trozo de un tramo (F17.1): el periodo en que el ciclo tuvo UNA temática.</summary>
/// <param name="From">Inicio del periodo, en hora local.</param>
/// <param name="To">Fin del periodo, en hora local (el último llega al fin del tramo).</param>
/// <param name="TooltipLines">Las líneas del tooltip de ESTE trozo: su temática y sus fechas.</param>
public sealed record RibbonSlice(Brush Fill, DateTime From, DateTime To, IReadOnlyList<string> TooltipLines);

/// <summary>Un tramo de la cinta: un ciclo de una aplicación, partido por sus temáticas.</summary>
/// <param name="Label">«C2 · Seguridad» —o «C3 · Rendimiento → Seguridad»— cuando cabe.</param>
/// <param name="ShortLabel">«C2», cuando solo cabe eso.</param>
/// <param name="Slices">Los periodos de temática, en orden. Uno solo en el caso normal.</param>
/// <param name="IsOpen">El ciclo sigue abierto: el tramo llega hasta hoy con remate de «en curso».</param>
/// <param name="EndIsKnown">
/// False cuando la fecha de cierre no se pudo recuperar y el tramo termina donde alcanza el dato
/// (F17 §6, honestidad con el pasado). Se dibuja con el borde derecho a puntos.
/// </param>
/// <param name="TooltipLines">El tooltip del ciclo entero; el de cada trozo lo lleva el trozo.</param>
/// <param name="Payload">Lo que se entrega al mando al pulsar. La cinta no sabe qué es.</param>
public sealed record RibbonSpan(
    string Label,
    string ShortLabel,
    IReadOnlyList<RibbonSlice> Slices,
    DateTime From,
    DateTime To,
    bool IsOpen,
    bool EndIsKnown,
    IReadOnlyList<string> TooltipLines,
    object? Payload = null)
{
    /// <summary>El relleno de la temática vigente: la última. Decide la tinta de la etiqueta.</summary>
    public Brush Fill => Slices[^1].Fill;
}

/// <summary>Una banda de la cinta: una aplicación con sus tramos, en orden. Sin tramos, se dice.</summary>
/// <param name="EmptyText">Lo que se escribe en la banda cuando no tiene ningún tramo en el periodo.</param>
public sealed record RibbonTrack(string Name, IReadOnlyList<RibbonSpan> Spans, string EmptyText = "sin ciclos en este periodo");

/// <summary>
/// La cinta de ciclos de Métricas (F17 §6, rehecha en F17.1): un eje temporal horizontal, una
/// banda por aplicación y un tramo por ciclo, partido por temáticas.
/// <para>
/// <b>La fila es la unidad indivisible.</b> El nombre va en una columna FIJA, fuera del área que
/// se desplaza, y la banda en un lienzo dentro de un <c>ScrollViewer</c> propio; los dos se
/// colocan con la misma aritmética de fila (<see cref="RowTop"/>), así que nombre y banda
/// comparten altura a cualquier posición de scroll y a cualquier ancho. En F17 el nombre vivía
/// dentro del mismo lienzo que se desplazaba, y al arrastrar cada nombre acababa a la altura de
/// la banda de otra aplicación: la gráfica atribuía auditorías a quien no las hizo.
/// </para>
/// <para>
/// <b>Una aplicación sin tramos tiene fila igualmente, y lo dice.</b> Una fila vacía y rotulada es
/// información; una fila ausente invita a que otro tramo ocupe su sitio visualmente.
/// </para>
/// <para>
/// <b>Escala honesta.</b> El eje cabe en la tarjeta salvo que dos tramos consecutivos de una misma
/// banda no se puedan distinguir a esa escala; solo entonces la cinta crece y se desplaza. Un
/// tramo más corto que el mínimo se PINTA con el ancho mínimo (anclado a la derecha si está en
/// curso), sin estirar el eje entero por él. Y al cambiar los datos la vista arranca en el final
/// del eje —hoy—, que es donde está lo que importa; si el usuario retrocede, se respeta.
/// </para>
/// </summary>
public sealed class CycleRibbon : Grid
{
    internal const double PadTop = 8;
    internal const double PadRight = 14;
    internal const double RowHeight = 24;
    internal const double RowGap = 10;
    internal const double AxisHeight = 24;
    private const double GutterCap = 170;
    private const double GutterPad = 12;
    private const double OpenFade = 16;
    private const double ChangeMark = 2;

    private readonly Canvas _names = new();
    private readonly Canvas _plot = new();
    private readonly ScrollViewer _scroll;
    private bool _scrollToEndPending;

    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<RibbonTrack>), typeof(CycleRibbon),
        new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(DateTime), typeof(CycleRibbon),
        new PropertyMetadata(DateTime.MinValue, OnDataChanged));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(DateTime), typeof(CycleRibbon),
        new PropertyMetadata(DateTime.MinValue, OnDataChanged));

    public static readonly DependencyProperty MinSpanWidthProperty = DependencyProperty.Register(
        nameof(MinSpanWidth), typeof(double), typeof(CycleRibbon),
        new PropertyMetadata(28d, OnVisualChanged));

    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush), typeof(Brush), typeof(CycleRibbon),
        new PropertyMetadata(Brushes.Gray, OnVisualChanged));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(CycleRibbon),
        new PropertyMetadata(Brushes.Gray, OnVisualChanged));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(CycleRibbon),
        new PropertyMetadata(Brushes.Black, OnVisualChanged));

    public static readonly DependencyProperty SpanCommandProperty = DependencyProperty.Register(
        nameof(SpanCommand), typeof(ICommand), typeof(CycleRibbon),
        new PropertyMetadata(null));

    public IReadOnlyList<RibbonTrack>? Tracks
    {
        get => (IReadOnlyList<RibbonTrack>?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    /// <summary>El extremo izquierdo del eje, en hora local.</summary>
    public DateTime From
    {
        get => (DateTime)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    /// <summary>El extremo derecho del eje: la medianoche de mañana, como en el resto del panel.</summary>
    public DateTime To
    {
        get => (DateTime)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    /// <summary>Ancho mínimo con el que se PINTA un tramo. Por debajo, un ciclo no se puede ni señalar.</summary>
    public double MinSpanWidth
    {
        get => (double)GetValue(MinSpanWidthProperty);
        set => SetValue(MinSpanWidthProperty, value);
    }

    public Brush AxisBrush
    {
        get => (Brush)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <summary>El color del nombre de cada banda. Del tema, no de la cinta.</summary>
    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <summary>Lo que se ejecuta al pulsar un tramo, con su <see cref="RibbonSpan.Payload"/>.</summary>
    public ICommand? SpanCommand
    {
        get => (ICommand?)GetValue(SpanCommandProperty);
        set => SetValue(SpanCommandProperty, value);
    }

    /// <summary>El área desplazable, para poder afirmar dónde arranca y qué se ve.</summary>
    internal ScrollViewer Scroll => _scroll;

    /// <summary>La columna fija de nombres.</summary>
    internal Canvas Names => _names;

    /// <summary>El lienzo de las bandas.</summary>
    internal Canvas Plot => _plot;

    /// <summary>El rótulo de cada fila, por índice de banda.</summary>
    internal IReadOnlyList<TextBlock> NameLabels { get; private set; } = Array.Empty<TextBlock>();

    /// <summary>Las formas de cada tramo (una por trozo), con la fila a la que pertenecen.</summary>
    internal IReadOnlyList<(int Row, RibbonSpan Span, Rectangle Shape)> SpanShapes { get; private set; }
        = Array.Empty<(int, RibbonSpan, Rectangle)>();

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
        _scroll.LayoutUpdated += (_, _) => ApplyPendingScroll();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ribbon = (CycleRibbon)d;
        // Datos nuevos: la vista arranca en el presente. Un cambio de tamaño no toca la posición.
        ribbon._scrollToEndPending = true;
        ribbon.Rebuild();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CycleRibbon)d).Rebuild();

    /// <summary>Dónde empieza la fila <paramref name="row"/>. La misma cuenta para el nombre y para la banda.</summary>
    internal static double RowTop(int row) => PadTop + row * (RowHeight + RowGap);

    /// <summary>
    /// La geometría del área de bandas para unos datos y una ventana: cuánto mide y con qué escala.
    /// Separada del dibujo para poder afirmarla sin pintar un píxel.
    /// <para>
    /// La escala nace de la ventana. Solo crece cuando dos tramos CONSECUTIVOS de una misma banda
    /// quedarían a menos del ancho mínimo uno del otro —entonces no se distinguirían—; un tramo
    /// corto suelto no estira el eje: se pinta con el ancho mínimo donde está.
    /// </para>
    /// </summary>
    internal static (double Width, double Height, double PixelsPerDay) Geometry(
        IReadOnlyList<RibbonTrack> tracks, DateTime from, DateTime to, double viewport, double minSpan)
    {
        double totalDays = Math.Max(1.0 / 24, (to - from).TotalDays);
        double plot = Math.Max(1, viewport - PadRight);
        double scale = plot / totalDays;

        double closestStarts = double.PositiveInfinity;
        foreach (RibbonTrack track in tracks)
        {
            var starts = track.Spans
                .Where(s => s.To > from && s.From < to)
                .Select(s => (s.From > from ? s.From : from))
                .OrderBy(d => d)
                .ToList();
            for (int i = 1; i < starts.Count; i++)
            {
                closestStarts = Math.Min(closestStarts, (starts[i] - starts[i - 1]).TotalDays);
            }
        }

        if (!double.IsPositiveInfinity(closestStarts))
        {
            double needed = minSpan / Math.Max(closestStarts, 1.0 / 1440);
            if (needed > scale)
            {
                scale = needed;
            }
        }

        double width = Math.Max(viewport, scale * totalDays + PadRight);
        double height = PadTop + tracks.Count * (RowHeight + RowGap) + AxisHeight;
        return (width, height, scale);
    }

    private void Rebuild()
    {
        _names.Children.Clear();
        _plot.Children.Clear();
        var names = new List<TextBlock>();
        var shapes = new List<(int, RibbonSpan, Rectangle)>();
        var empties = new List<(int, TextBlock)>();

        IReadOnlyList<RibbonTrack> tracks = Tracks ?? Array.Empty<RibbonTrack>();
        if (tracks.Count == 0 || To <= From)
        {
            _names.Width = 0;
            _names.Height = 0;
            _plot.Width = 0;
            _plot.Height = 0;
            NameLabels = names;
            SpanShapes = shapes;
            EmptyLabels = empties;
            return;
        }

        double gutter = Math.Min(GutterCap, tracks.Max(t => Measure(t.Name, 12).Width)) + GutterPad;
        double viewport = Math.Max(1, ActualWidth - gutter);
        (double width, double height, double scale) = Geometry(tracks, From, To, viewport, MinSpanWidth);

        _names.Width = gutter;
        _names.Height = height;
        _plot.Width = width;
        _plot.Height = height;

        double plotRight = width - PadRight;
        double axisTop = RowTop(tracks.Count);
        DrawAxis(0, plotRight, PadTop, axisTop, scale);

        for (int row = 0; row < tracks.Count; row++)
        {
            RibbonTrack track = tracks[row];
            double top = RowTop(row);

            TextBlock name = NameLabel(track.Name, gutter - GutterPad);
            Canvas.SetLeft(name, 0);
            Canvas.SetTop(name, top + (RowHeight - Measure(track.Name, 12).Height) / 2);
            _names.Children.Add(name);
            names.Add(name);

            if (track.Spans.Count == 0 || !track.Spans.Any(s => s.To > From && s.From < To))
            {
                var empty = new TextBlock
                {
                    Text = track.EmptyText,
                    FontSize = 11.5,
                    FontStyle = FontStyles.Italic,
                    Opacity = 0.55,
                    Foreground = TextBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(empty, 4);
                Canvas.SetTop(empty, top + (RowHeight - Measure(track.EmptyText, 11.5).Height) / 2);
                _plot.Children.Add(empty);
                empties.Add((row, empty));
                continue;
            }

            foreach (RibbonSpan span in track.Spans)
            {
                DrawSpan(row, span, top, plotRight, scale, shapes);
            }
        }

        NameLabels = names;
        SpanShapes = shapes;
        EmptyLabels = empties;
    }

    /// <summary>Al cambiar los datos, la vista arranca en el presente. Se aplica en cuanto el scroll sabe cuánto mide.</summary>
    private void ApplyPendingScroll()
    {
        if (!_scrollToEndPending || _scroll.ViewportWidth <= 0)
        {
            return;
        }

        _scrollToEndPending = false;
        if (_scroll.ScrollableWidth > 0)
        {
            _scroll.ScrollToRightEnd();
        }
    }

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

    private void DrawSpan(int row, RibbonSpan span, double top, double plotRight, double scale, List<(int, RibbonSpan, Rectangle)> shapes)
    {
        DateTime from = span.From < From ? From : span.From;
        DateTime to = span.To > To ? To : span.To;
        if (to <= from)
        {
            return; // fuera del periodo: el filtro recorta el eje, no inventa tramos
        }

        double x0 = (from - From).TotalDays * scale;
        double x1 = Math.Min(plotRight, (to - From).TotalDays * scale);

        // Un tramo más corto que el mínimo se PINTA con el mínimo, sin estirar el eje: hacia la
        // derecha si está cerrado; anclado al presente, hacia la izquierda, si está en curso.
        if (x1 - x0 < MinSpanWidth)
        {
            if (span.IsOpen || x0 + MinSpanWidth > plotRight)
            {
                x0 = Math.Max(0, x1 - MinSpanWidth);
            }
            else
            {
                x1 = x0 + MinSpanWidth;
            }
        }

        double total = Math.Max(2, x1 - x0);
        double spanDays = Math.Max(1.0 / 1440, (to - from).TotalDays);
        double cursor = x0;
        for (int i = 0; i < span.Slices.Count; i++)
        {
            RibbonSlice slice = span.Slices[i];
            DateTime sFrom = slice.From < from ? from : slice.From;
            DateTime sTo = slice.To > to ? to : slice.To;
            if (sTo <= sFrom)
            {
                continue;
            }

            bool last = i == span.Slices.Count - 1;
            double w = last
                ? Math.Max(1, x0 + total - cursor)
                : Math.Max(1, total * (sTo - sFrom).TotalDays / spanDays);

            var rect = new Rectangle
            {
                Width = w,
                Height = RowHeight,
                Fill = span.IsOpen && last ? OpenFill(slice.Fill, w) : slice.Fill,
                Cursor = Cursors.Hand,
                ToolTip = BuildTooltip(span.Slices.Count > 1 ? slice.TooltipLines.Concat(span.TooltipLines).ToList() : span.TooltipLines),
                Tag = span,
            };
            Canvas.SetLeft(rect, cursor);
            Canvas.SetTop(rect, top);
            ToolTipService.SetInitialShowDelay(rect, 120);
            ToolTipService.SetBetweenShowDelay(rect, 0);
            object? payload = span.Payload;
            rect.MouseLeftButtonUp += (_, e) =>
            {
                if (SpanCommand?.CanExecute(payload) == true)
                {
                    SpanCommand.Execute(payload);
                    e.Handled = true;
                }
            };
            _plot.Children.Add(rect);
            shapes.Add((row, span, rect));

            // La marca del cambio de temática: una muesca clara entre un trozo y el siguiente.
            if (!last)
            {
                var mark = new Rectangle
                {
                    Width = ChangeMark,
                    Height = RowHeight,
                    Fill = AxisBrush,
                    Opacity = 0.9,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(mark, cursor + w - ChangeMark / 2);
                Canvas.SetTop(mark, top);
                _plot.Children.Add(mark);
            }

            cursor += w;
        }

        // El fin sin fecha recuperable se dice también en el dibujo: borde derecho a puntos.
        if (!span.EndIsKnown && !span.IsOpen)
        {
            var edge = new Line
            {
                X1 = x0 + total,
                X2 = x0 + total,
                Y1 = top,
                Y2 = top + RowHeight,
                Stroke = AxisBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection(new double[] { 1.5, 2 }),
                IsHitTestVisible = false,
            };
            _plot.Children.Add(edge);
        }

        // La etiqueta, cuando cabe; la corta, cuando cabe solo ella; nada, y el tooltip lo
        // explica, cuando no cabe ni eso. Se MIDE — no se estima por número de caracteres.
        string? text = Fits(span.Label, total) ? span.Label : Fits(span.ShortLabel, total) ? span.ShortLabel : null;
        if (text is not null)
        {
            Size size = Measure(text, 11);
            var label = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = InkFor(span.Slices[0].Fill),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, x0 + 6);
            Canvas.SetTop(label, top + (RowHeight - size.Height) / 2);
            _plot.Children.Add(label);
        }
    }

    private bool Fits(string text, double width) => Measure(text, 11).Width + 12 <= width;

    /// <summary>
    /// El remate de «en curso»: el tramo se desvanece en sus últimos píxeles. Un ciclo abierto no
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
    /// La tinta de la etiqueta sobre su tramo: negro o blanco, decidido por la luminancia del
    /// relleno y no por el tema (F10.1, D-649: la tinta va por paso, medida).
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

    private void DrawAxis(double left, double right, double top, double axisTop, double scale)
    {
        var baseline = new Line
        {
            X1 = left,
            X2 = right,
            Y1 = axisTop,
            Y2 = axisTop,
            Stroke = AxisBrush,
            StrokeThickness = 1,
            Opacity = 0.6,
        };
        _plot.Children.Add(baseline);

        IReadOnlyList<(DateTime When, string Label)> ticks = Ticks(From, To);
        double widest = ticks.Count == 0 ? 0 : ticks.Max(t => Measure(t.Label, 10).Width);
        double minGap = widest + 10;

        // Las etiquetas se saltan de n en n cuando no caben, ancladas a la ÚLTIMA (la regla de
        // ChartPlot): «hoy» siempre se lee.
        double lastLabelX = double.PositiveInfinity;
        for (int i = ticks.Count - 1; i >= 0; i--)
        {
            (DateTime when, string label) = ticks[i];
            double x = left + (when - From).TotalDays * scale;
            if (x < left - 0.5 || x > right + 0.5)
            {
                continue;
            }

            var grid = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = top,
                Y2 = axisTop,
                Stroke = GridBrush,
                StrokeThickness = 1,
                Opacity = 0.25,
                IsHitTestVisible = false,
            };
            _plot.Children.Add(grid);

            if (lastLabelX - x < minGap)
            {
                continue;
            }

            Size size = Measure(label, 10);
            var text = new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = AxisBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(text, Math.Max(left, Math.Min(x - size.Width / 2, right - size.Width)));
            Canvas.SetTop(text, axisTop + 5);
            _plot.Children.Add(text);
            lastLabelX = x;
        }
    }

    /// <summary>
    /// Las marcas del eje según lo que abarca: días para una semana o menos, semanas para dos
    /// meses o menos, meses hasta poco más de un año, trimestres de ahí en adelante. Anclado al
    /// final —el último día del periodo es hoy— para que la marca de «hoy» exista siempre.
    /// </summary>
    internal static IReadOnlyList<(DateTime When, string Label)> Ticks(DateTime from, DateTime to)
    {
        var ticks = new List<(DateTime, string)>();
        double days = (to - from).TotalDays;
        CultureInfo culture = CultureInfo.CurrentCulture;
        DateTime last = to.AddDays(-1).Date;

        if (days <= 8)
        {
            for (DateTime d = last; d >= from; d = d.AddDays(-1))
            {
                ticks.Add((d, d.ToString("d MMM", culture)));
            }
        }
        else if (days <= 70)
        {
            for (DateTime d = last; d >= from; d = d.AddDays(-7))
            {
                ticks.Add((d, d.ToString("d MMM", culture)));
            }
        }
        else if (days <= 400)
        {
            ticks.Add((last, last.ToString("d MMM", culture)));
            for (DateTime d = new DateTime(last.Year, last.Month, 1); d >= from; d = d.AddMonths(-1))
            {
                ticks.Add((d, d.ToString("MMM yy", culture)));
            }
        }
        else
        {
            ticks.Add((last, last.ToString("d MMM yy", culture)));
            int quarterMonth = last.Month - (last.Month - 1) % 3;
            for (DateTime d = new DateTime(last.Year, quarterMonth, 1); d >= from; d = d.AddMonths(-3))
            {
                ticks.Add((d, d.Month == 1 ? d.ToString("yyyy", culture) : d.ToString("MMM yy", culture)));
            }
        }

        ticks.Reverse();
        return ticks;
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

    private Size Measure(string text, double size)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return new Size(formatted.Width, formatted.Height);
    }
}
