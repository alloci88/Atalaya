using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Atalaya.App.Controls;

/// <summary>Un tramo de la cinta: un ciclo de una aplicación, ya coloreado y ya explicado.</summary>
/// <param name="Label">«C2 · Seguridad», cuando cabe.</param>
/// <param name="ShortLabel">«C2», cuando solo cabe eso.</param>
/// <param name="From">Inicio del tramo, en hora LOCAL.</param>
/// <param name="To">Fin del tramo, en hora local. Para el ciclo abierto es «hoy».</param>
/// <param name="IsOpen">El ciclo sigue abierto: el tramo llega hasta hoy con remate de «en curso».</param>
/// <param name="EndIsKnown">
/// False cuando la fecha de cierre no se pudo recuperar y el tramo termina donde alcanza el dato
/// (F17 §6, honestidad con el pasado). Se dibuja con el borde derecho a puntos.
/// </param>
/// <param name="TooltipLines">Las líneas del tooltip; la primera va en negrita.</param>
/// <param name="Payload">Lo que se entrega al mando al pulsar. La cinta no sabe qué es.</param>
public sealed record RibbonSpan(
    string Label,
    string ShortLabel,
    Brush Fill,
    DateTime From,
    DateTime To,
    bool IsOpen,
    bool EndIsKnown,
    IReadOnlyList<string> TooltipLines,
    object? Payload = null);

/// <summary>Una banda de la cinta: una aplicación con sus tramos, en orden.</summary>
public sealed record RibbonTrack(string Name, IReadOnlyList<RibbonSpan> Spans);

/// <summary>
/// La cinta de ciclos de Métricas (F17 §6): un eje temporal horizontal, una banda por aplicación
/// y un tramo por ciclo, coloreado por su temática.
/// <para>
/// Misma familia que <see cref="ChartPlot"/>: dibujo propio sobre un <c>Canvas</c>, medida real
/// del texto, tooltip nativo (hereda el tema) y ninguna decisión de color aquí — los pinceles
/// llegan dados desde el view-model, que es quien sabe de temáticas y de temas.
/// </para>
/// <para>
/// <b>Muchos ciclos en poco espacio.</b> Cada tramo tiene un ancho mínimo (<see cref="MinSpanWidth"/>);
/// si el periodo no cabe respetándolo, la cinta se hace MÁS ANCHA que su ventana y se desplaza
/// dentro de su tarjeta. Nunca la página: la regla de la casa es que nada provoque scroll
/// horizontal de página, y por eso la cinta vive dentro de un <c>ScrollViewer</c> propio y lee
/// el ancho de su ventana por <see cref="ViewportWidth"/>.
/// </para>
/// </summary>
public sealed class CycleRibbon : Canvas
{
    private const double PadTop = 8;
    private const double PadRight = 14;
    private const double RowHeight = 24;
    private const double RowGap = 10;
    private const double AxisHeight = 24;
    private const double GutterCap = 150;
    private const double GutterPad = 12;
    private const double OpenFade = 16;

    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<RibbonTrack>), typeof(CycleRibbon),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(DateTime), typeof(CycleRibbon),
        new PropertyMetadata(DateTime.MinValue, OnVisualChanged));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(DateTime), typeof(CycleRibbon),
        new PropertyMetadata(DateTime.MinValue, OnVisualChanged));

    public static readonly DependencyProperty ViewportWidthProperty = DependencyProperty.Register(
        nameof(ViewportWidth), typeof(double), typeof(CycleRibbon),
        new PropertyMetadata(0d, OnVisualChanged));

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

    /// <summary>
    /// El ancho de la ventana en la que vive. La cinta ocupa como mínimo eso, y más si sus tramos
    /// no caben con su ancho mínimo — y entonces es su ventana la que se desplaza.
    /// </summary>
    public double ViewportWidth
    {
        get => (double)GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    /// <summary>Ancho mínimo de un tramo en píxeles. Por debajo, un ciclo no se puede ni señalar.</summary>
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

    public CycleRibbon()
    {
        ClipToBounds = true;
        Loaded += (_, _) => Rebuild();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CycleRibbon)d).Rebuild();

    /// <summary>
    /// La geometría de la cinta para unos datos y una ventana: cuánto mide y con qué escala. Está
    /// separada del dibujo para poder afirmarla sin pintar un píxel: es lo que fija que el ancho
    /// mínimo por tramo se respeta y que la cinta nunca es más estrecha que su ventana.
    /// </summary>
    internal static (double Width, double Height, double PixelsPerDay, double Gutter) Geometry(
        IReadOnlyList<RibbonTrack> tracks, DateTime from, DateTime to, double viewport, double minSpan,
        double gutter)
    {
        double totalDays = Math.Max(1, (to - from).TotalDays);
        double plot = Math.Max(0, viewport - gutter - PadRight);
        double scale = plot / totalDays;

        // El tramo más corto VISIBLE en el periodo manda: si a esta escala no llega al mínimo, la
        // cinta crece hasta que llegue. Recortado al periodo, no al ciclo entero: un ciclo de dos
        // años del que solo se ve una semana necesita una semana ancha, no dos años.
        double shortest = tracks
            .SelectMany(t => t.Spans)
            .Select(s => ((s.To < to ? s.To : to) - (s.From > from ? s.From : from)).TotalDays)
            .Where(d => d > 0)
            .DefaultIfEmpty(totalDays)
            .Min();
        double needed = minSpan / Math.Max(shortest, 1.0 / 24);
        if (needed > scale)
        {
            scale = needed;
        }

        double width = Math.Max(viewport, gutter + PadRight + scale * totalDays);
        double height = PadTop + tracks.Count * (RowHeight + RowGap) + AxisHeight;
        return (width, height, scale, gutter);
    }

    private void Rebuild()
    {
        Children.Clear();
        IReadOnlyList<RibbonTrack> tracks = Tracks ?? Array.Empty<RibbonTrack>();
        if (tracks.Count == 0 || To <= From)
        {
            Width = double.NaN;
            Height = 0;
            return;
        }

        double gutter = Math.Min(GutterCap, tracks.Max(t => Measure(t.Name, 12).Width)) + GutterPad;
        double viewport = ViewportWidth > 0 ? ViewportWidth : ActualWidth;
        (double width, double height, double scale, _) = Geometry(tracks, From, To, viewport, MinSpanWidth, gutter);
        Width = width;
        Height = height;

        double plotLeft = gutter;
        double plotRight = width - PadRight;
        double axisTop = PadTop + tracks.Count * (RowHeight + RowGap);

        DrawAxis(plotLeft, plotRight, PadTop, axisTop, scale);

        for (int row = 0; row < tracks.Count; row++)
        {
            RibbonTrack track = tracks[row];
            double top = PadTop + row * (RowHeight + RowGap);

            var name = new TextBlock
            {
                Text = track.Name,
                FontSize = 12,
                Foreground = TextBrush,
                Width = gutter - GutterPad,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = track.Name,
            };
            SetLeft(name, 0);
            SetTop(name, top + (RowHeight - Measure(track.Name, 12).Height) / 2);
            Children.Add(name);

            foreach (RibbonSpan span in track.Spans)
            {
                DrawSpan(span, top, plotLeft, plotRight, scale);
            }
        }
    }

    private void DrawSpan(RibbonSpan span, double top, double plotLeft, double plotRight, double scale)
    {
        DateTime from = span.From < From ? From : span.From;
        DateTime to = span.To > To ? To : span.To;
        if (to <= from)
        {
            return; // fuera del periodo: el filtro recorta el eje, no inventa tramos
        }

        double x0 = plotLeft + (from - From).TotalDays * scale;
        double x1 = Math.Min(plotRight, plotLeft + (to - From).TotalDays * scale);
        double w = Math.Max(2, x1 - x0);

        var rect = new Rectangle
        {
            Width = w,
            Height = RowHeight,
            RadiusX = 3,
            RadiusY = 3,
            Fill = span.IsOpen ? OpenFill(span.Fill, w) : span.Fill,
            Cursor = Cursors.Hand,
            ToolTip = BuildTooltip(span.TooltipLines),
        };
        SetLeft(rect, x0);
        SetTop(rect, top);
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
        Children.Add(rect);

        // El fin sin fecha recuperable se dice también en el dibujo: borde derecho a puntos.
        if (!span.EndIsKnown && !span.IsOpen)
        {
            var edge = new Line
            {
                X1 = x0 + w,
                X2 = x0 + w,
                Y1 = top,
                Y2 = top + RowHeight,
                Stroke = AxisBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection(new double[] { 1.5, 2 }),
                IsHitTestVisible = false,
            };
            Children.Add(edge);
        }

        // La etiqueta, cuando cabe; la corta, cuando cabe solo ella; nada, y el tooltip lo
        // explica, cuando no cabe ni eso. Se MIDE — no se estima por número de caracteres.
        string? text = Fits(span.Label, w) ? span.Label : Fits(span.ShortLabel, w) ? span.ShortLabel : null;
        if (text is not null)
        {
            Size size = Measure(text, 11);
            var label = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = InkFor(span.Fill),
                IsHitTestVisible = false,
            };
            SetLeft(label, x0 + 6);
            SetTop(label, top + (RowHeight - size.Height) / 2);
            Children.Add(label);
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
    /// relleno y no por el tema (F10.1, D-649: la tinta va por paso, medida). Los pasos claros de
    /// la paleta —los del tema oscuro— llevan negro; los oscuros —los del tema claro—, blanco.
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
        Children.Add(baseline);

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
            Children.Add(grid);

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
            SetLeft(text, Math.Max(left, Math.Min(x - size.Width / 2, right - size.Width)));
            SetTop(text, axisTop + 5);
            Children.Add(text);
            lastLabelX = x;
        }
    }

    /// <summary>
    /// Las marcas del eje según lo que abarca: semanas para dos meses o menos, meses hasta poco
    /// más de un año, trimestres de ahí en adelante. Anclado al final —el último día del periodo
    /// es hoy— para que la marca de «hoy» exista siempre.
    /// </summary>
    internal static IReadOnlyList<(DateTime When, string Label)> Ticks(DateTime from, DateTime to)
    {
        var ticks = new List<(DateTime, string)>();
        double days = (to - from).TotalDays;
        CultureInfo culture = CultureInfo.CurrentCulture;
        DateTime last = to.AddDays(-1).Date;

        if (days <= 70)
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
