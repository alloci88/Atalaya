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
public sealed record RibbonSpan(
    string Label,
    string Dates,
    IReadOnlyList<RibbonSlice> Slices,
    DateTime From,
    DateTime To,
    bool IsOpen,
    bool EndIsKnown,
    IReadOnlyList<string> TooltipLines,
    object? Payload = null)
{
    /// <summary>El relleno de la temática vigente: la última.</summary>
    public Brush Fill => Slices[^1].Fill;
}

/// <summary>Una fila de la secuencia: una aplicación con sus capítulos, en orden. Sin ninguno, se dice.</summary>
/// <param name="EmptyText">Lo que se escribe en la fila cuando no tiene ningún capítulo en el periodo.</param>
/// <param name="Notice">Lo que el periodo dejó fuera («2 ciclos anteriores fuera del periodo»), o vacío.</param>
public sealed record RibbonTrack(
    string Name,
    IReadOnlyList<RibbonSpan> Spans,
    string EmptyText = "sin ciclos en este periodo",
    string Notice = "");

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
    internal const double BlockWidth = 168;
    internal const double BlockGap = 8;
    internal const double MinSliceWidth = 6;
    private const double GutterCap = 170;
    private const double GutterPad = 12;
    private const double OpenFade = 16;
    private const double ChangeMark = 2;
    private const double LabelSize = 11;
    private const double DatesSize = 10;

    /// <summary>
    /// A partir de cuánto tiempo sin auditar se escribe el hueco entre dos ciclos: una semana.
    /// Por debajo, dos ciclos seguidos son continuación —el cierre abre el siguiente el mismo
    /// día, o al día siguiente— y anotar «2 días sin auditar» sería ruido; a partir de una semana
    /// ya es un dato que explica algo del historial.
    /// </summary>
    public static readonly TimeSpan GapThreshold = TimeSpan.FromDays(7);

    private readonly Canvas _names = new();
    private readonly Canvas _plot = new();
    private readonly ScrollViewer _scroll;
    private bool _scrollToEndPending;

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

    /// <summary>El aviso de «fuera del periodo» de cada fila que lo lleva.</summary>
    internal IReadOnlyList<(int Row, TextBlock Text)> NoticeLabels { get; private set; } = Array.Empty<(int, TextBlock)>();

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
        // Datos nuevos: la vista arranca por el final. Un cambio de tamaño no toca la posición.
        ribbon._scrollToEndPending = true;
        ribbon.Rebuild();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CycleRibbon)d).Rebuild();

    /// <summary>Dónde empieza la fila <paramref name="row"/>. La misma cuenta para el nombre y para los bloques.</summary>
    internal static double RowTop(int row) => PadTop + row * (RowHeight + RowGap);

    /// <summary>
    /// El hueco entre dos ciclos, escrito: «3 semanas sin auditar». Null por debajo del umbral.
    /// Días hasta dos semanas, semanas hasta dos meses, meses después: la unidad que se lee de un
    /// vistazo sin tener que dividir.
    /// </summary>
    internal static string? GapText(DateTime previousEnd, DateTime nextStart)
    {
        TimeSpan gap = nextStart - previousEnd;
        if (gap < GapThreshold)
        {
            return null;
        }

        int days = (int)Math.Round(gap.TotalDays);
        if (days < 14)
        {
            return $"{days} días sin auditar";
        }

        if (days < 61)
        {
            int weeks = (int)Math.Round(days / 7.0);
            return $"{weeks} semanas sin auditar";
        }

        int months = (int)Math.Round(days / 30.44);
        return months == 1 ? "1 mes sin auditar" : $"{months} meses sin auditar";
    }

    private void Rebuild()
    {
        _names.Children.Clear();
        _plot.Children.Clear();
        var names = new List<TextBlock>();
        var shapes = new List<(int, RibbonSpan, Rectangle)>();
        var labels = new List<(RibbonSpan, TextBlock, TextBlock?)>();
        var gaps = new List<(int, TextBlock)>();
        var empties = new List<(int, TextBlock)>();
        var notices = new List<(int, TextBlock)>();

        IReadOnlyList<RibbonTrack> tracks = Tracks ?? Array.Empty<RibbonTrack>();
        if (tracks.Count == 0)
        {
            _names.Width = 0;
            _names.Height = 0;
            _plot.Width = 0;
            _plot.Height = 0;
            Publish();
            return;
        }

        double gutter = Math.Min(GutterCap, tracks.Max(t => Measure(t.Name, 12).Width)) + GutterPad;
        double height = RowTop(tracks.Count) - RowGap + PadTop;

        // LAS FILAS SE ALINEAN POR EL FINAL. El ciclo más reciente de cada aplicación queda en el
        // mismo borde derecho, así que al arrancar por el final —que es donde está lo que
        // importa— se ve el último capítulo de TODAS las filas, la fila vacía y su rótulo, y no
        // solo la cola de la fila más larga. Una fila corta que empezara por la izquierda quedaría
        // fuera de la vista inicial en cuanto otra fila tuviera más ciclos.
        var rowWidths = tracks.Select(RowContentWidth).ToList();
        double widest = Math.Max(1, rowWidths.Max());

        for (int row = 0; row < tracks.Count; row++)
        {
            RibbonTrack track = tracks[row];
            double top = RowTop(row);

            TextBlock name = NameLabel(track.Name, gutter - GutterPad);
            Canvas.SetLeft(name, 0);
            Canvas.SetTop(name, top + (RowHeight - Measure(track.Name, 12).Height) / 2);
            _names.Children.Add(name);
            names.Add(name);

            double x = 4 + (widest - rowWidths[row]);
            if (track.Notice.Length > 0)
            {
                TextBlock notice = Muted(track.Notice, 11);
                Canvas.SetLeft(notice, x);
                Canvas.SetTop(notice, top + (RowHeight - Measure(track.Notice, 11).Height) / 2);
                _plot.Children.Add(notice);
                notices.Add((row, notice));
                x += Measure(track.Notice, 11).Width + 16;
            }

            if (track.Spans.Count == 0)
            {
                TextBlock empty = Muted(track.EmptyText, 11.5);
                empty.FontStyle = FontStyles.Italic;
                Canvas.SetLeft(empty, x);
                Canvas.SetTop(empty, top + (RowHeight - Measure(track.EmptyText, 11.5).Height) / 2);
                _plot.Children.Add(empty);
                empties.Add((row, empty));
                continue;
            }

            RibbonSpan? previous = null;
            foreach (RibbonSpan span in track.Spans)
            {
                if (previous is not null && GapText(previous.To, span.From) is { } gapText)
                {
                    x = DrawGap(row, gapText, x, top, gaps);
                }

                DrawBlock(row, span, x, top, shapes, labels);
                x += BlockWidth + BlockGap;
                previous = span;
            }
        }

        _names.Width = gutter;
        _names.Height = height;
        _plot.Width = 4 + widest + PadRight;
        _plot.Height = height;
        Publish();

        void Publish()
        {
            NameLabels = names;
            SpanShapes = shapes;
            BlockLabels = labels;
            GapLabels = gaps;
            EmptyLabels = empties;
            NoticeLabels = notices;
        }
    }

    /// <summary>Cuánto ocupa el contenido de una fila: aviso, bloques y separadores, o el rótulo de vacío.</summary>
    private double RowContentWidth(RibbonTrack track)
    {
        double width = track.Notice.Length > 0 ? Measure(track.Notice, 11).Width + 16 : 0;
        if (track.Spans.Count == 0)
        {
            return width + Measure(track.EmptyText, 11.5).Width;
        }

        RibbonSpan? previous = null;
        foreach (RibbonSpan span in track.Spans)
        {
            if (previous is not null && GapText(previous.To, span.From) is { } gapText)
            {
                width += Measure(gapText, 10).Width + 20 + BlockGap;
            }

            width += BlockWidth + BlockGap;
            previous = span;
        }

        return width - BlockGap;
    }

    /// <summary>Al cambiar los datos, la vista arranca por el final. Se aplica en cuanto el scroll sabe cuánto mide.</summary>
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

    /// <summary>El separador de hueco: una línea a puntos con el dato encima. Devuelve la x siguiente.</summary>
    private double DrawGap(int row, string text, double x, double top, List<(int, TextBlock)> gaps)
    {
        Size size = Measure(text, 10);
        double width = size.Width + 20;

        var line = new Line
        {
            X1 = x + 4,
            X2 = x + width - 4,
            Y1 = top + RowHeight / 2 + 8,
            Y2 = top + RowHeight / 2 + 8,
            Stroke = AxisBrush,
            StrokeThickness = 1,
            Opacity = 0.5,
            StrokeDashArray = new DoubleCollection(new double[] { 2, 3 }),
            IsHitTestVisible = false,
        };
        _plot.Children.Add(line);

        TextBlock label = Muted(text, 10);
        Canvas.SetLeft(label, x + 10);
        Canvas.SetTop(label, top + RowHeight / 2 - size.Height - 1);
        _plot.Children.Add(label);
        gaps.Add((row, label));

        return x + width + BlockGap;
    }

    private void DrawBlock(int row, RibbonSpan span, double x, double top,
        List<(int, RibbonSpan, Rectangle)> shapes, List<(RibbonSpan, TextBlock, TextBlock?)> labels)
    {
        // Los trozos, proporcionales a la DURACIÓN de cada periodo dentro del ciclo. El bloque es
        // de ancho fijo —un ciclo de tres horas y uno de tres semanas cuentan lo mismo como
        // capítulo—, pero dentro de él la historia de temáticas sí se reparte por lo que duró cada
        // una, con un mínimo para que un cambio reciente no desaparezca.
        double spanDays = Math.Max(1.0 / 1440, (span.To - span.From).TotalDays);
        var widths = new double[span.Slices.Count];
        double sum = 0;
        for (int i = 0; i < span.Slices.Count; i++)
        {
            RibbonSlice slice = span.Slices[i];
            double days = Math.Max(0, (slice.To - slice.From).TotalDays);
            widths[i] = Math.Max(MinSliceWidth, BlockWidth * days / spanDays);
            sum += widths[i];
        }

        double cursor = x;
        for (int i = 0; i < span.Slices.Count; i++)
        {
            RibbonSlice slice = span.Slices[i];
            bool last = i == span.Slices.Count - 1;
            double w = last ? Math.Max(MinSliceWidth, x + BlockWidth - cursor) : widths[i] * BlockWidth / sum;

            var rect = new Rectangle
            {
                Width = w,
                Height = RowHeight,
                RadiusX = i == 0 || last ? 4 : 0,
                RadiusY = i == 0 || last ? 4 : 0,
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
                X1 = x + BlockWidth,
                X2 = x + BlockWidth,
                Y1 = top,
                Y2 = top + RowHeight,
                Stroke = AxisBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection(new double[] { 1.5, 2 }),
                IsHitTestVisible = false,
            };
            _plot.Children.Add(edge);
        }

        // El rótulo: identificador y temática SIEMPRE, envolviendo a dos líneas si hace falta; las
        // fechas debajo, y son ellas las que caen si el rótulo se lleva las dos líneas. El tooltip
        // las trae. Nada se recorta a media palabra.
        double inner = BlockWidth - 12;
        Brush ink = InkFor(span.Slices[0].Fill);
        FormattedText labelText = Format(span.Label, LabelSize, FontWeights.SemiBold, inner);
        var label = new TextBlock
        {
            Text = span.Label,
            FontSize = LabelSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            TextWrapping = TextWrapping.Wrap,
            Width = inner,
            IsHitTestVisible = false,
        };
        double oneLine = Format("Ag", LabelSize, FontWeights.SemiBold, inner).Height;
        bool wrapped = labelText.Height > oneLine * 1.5;
        double datesHeight = Measure(span.Dates, DatesSize).Height;
        bool showDates = !wrapped && span.Dates.Length > 0 && labelText.Height + datesHeight + 2 <= RowHeight - 6;

        double block = (showDates ? labelText.Height + 2 + datesHeight : labelText.Height);
        double labelTop = top + Math.Max(3, (RowHeight - block) / 2);
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
            Canvas.SetTop(dates, labelTop + labelText.Height + 2);
            _plot.Children.Add(dates);
        }

        labels.Add((span, label, dates));
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
