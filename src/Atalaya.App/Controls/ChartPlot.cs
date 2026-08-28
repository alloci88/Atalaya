using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Atalaya.App.Services;

namespace Atalaya.App.Controls;

/// <summary>Cómo se dibuja una serie. No hay más formas en el panel (F5.9: cuatro bien hechas).</summary>
public enum ChartSeriesKind
{
    /// <summary>Línea fina con marcas en los puntos. Magnitudes continuas en el tiempo.</summary>
    Line,

    /// <summary>Barra por cubo. Conteos discretos por semana.</summary>
    Bar,
}

/// <summary>
/// Una serie ya lista para dibujar: sus valores, su color y cómo se llama en la leyenda.
/// <para>
/// El color viene DADO desde el view-model (sale de <see cref="SeriesPalette"/>): la gráfica no
/// elige colores, porque si eligiera, el color de una app dependería del orden en que le llegaran
/// las series y cambiaría al filtrar.
/// </para>
/// </summary>
public sealed record ChartSeries(
    string Key,
    string Name,
    Brush Stroke,
    IReadOnlyList<double> Values,
    ChartSeriesKind Kind = ChartSeriesKind.Line,
    bool Dashed = false);

/// <summary>
/// La gráfica del panel de métricas (F5.9 §2): rejilla discreta, marcas finas, UN eje Y, y un
/// tooltip por cubo que enseña TODAS las series con su color y su valor.
/// <para>
/// <b>Por qué a mano y no con una librería.</b> Ver DECISIONS (F5.9). En corto: las cuatro formas
/// del panel son geometría trivial (polilínea, barras, arcos), mientras que las reglas del §2
/// —color por identidad, severidades reservadas, un solo eje, dos temas— son NUESTRAS y habría
/// que imponerlas una a una sobre los valores por defecto de cualquier librería. Y el tooltip
/// nativo de WPF ya hereda el tema de la aplicación, que es justo lo que un lienzo Skia no hace.
/// </para>
/// <para>
/// El cursor no es un adorno: la banda transparente de cada cubo cubre todo el alto del área de
/// dibujo, así que el tooltip sale apuntando a cualquier altura de esa columna y no hay qué
/// acertarle a una línea de 1,6 px.
/// </para>
/// </summary>
public sealed class ChartPlot : Canvas
{
    private const double PadTop = 10;
    private const double PadRight = 12;
    private const double PadBottom = 22;
    private const double MinGutter = 34;
    private const double LineThickness = 1.6;

    private Line? _crosshair;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(ChartPlot),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(
        nameof(Labels), typeof(IReadOnlyList<string>), typeof(ChartPlot),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty TooltipLabelsProperty = DependencyProperty.Register(
        nameof(TooltipLabels), typeof(IReadOnlyList<string>), typeof(ChartPlot),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(ChartPlot),
        new PropertyMetadata("0.##", OnVisualChanged));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(ChartPlot),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush), typeof(Brush), typeof(ChartPlot),
        new PropertyMetadata(Brushes.Gray, OnVisualChanged));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(ChartPlot),
        new PropertyMetadata(Brushes.Gray, OnVisualChanged));

    /// <summary>Las series a dibujar. Todas comparten el único eje Y.</summary>
    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>Las etiquetas del eje X, una por cubo.</summary>
    public IReadOnlyList<string>? Labels
    {
        get => (IReadOnlyList<string>?)GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    /// <summary>
    /// Cómo se llama cada cubo en el TOOLTIP, cuando su nombre en el eje no basta. Un cubo semanal
    /// cabe en el eje como «28 ago» y en el tooltip se explica entero: «22–28 ago». Vacía, el
    /// tooltip usa la etiqueta del eje — que es lo correcto cuando el cubo es un solo día.
    /// </summary>
    public IReadOnlyList<string>? TooltipLabels
    {
        get => (IReadOnlyList<string>?)GetValue(TooltipLabelsProperty);
        set => SetValue(TooltipLabelsProperty, value);
    }

    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    /// <summary>La unidad, escrita en el tooltip. Un número sin unidad no es un dato.</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
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

    public ChartPlot()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => Rebuild();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChartPlot)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        _crosshair = null;

        var series = (Series ?? Array.Empty<ChartSeries>()).Where(s => s.Values.Count > 0).ToList();
        var labels = Labels ?? Array.Empty<string>();
        double w = ActualWidth;
        double h = ActualHeight;
        int buckets = series.Count == 0 ? 0 : series.Max(s => s.Values.Count);
        if (w <= 0 || h <= 0 || buckets == 0)
        {
            return;
        }

        double dataMax = series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max();
        AxisScale axis = AxisScale.For(dataMax);

        // El canalón izquierdo se mide sobre la etiqueta más ancha que se vaya a escribir: fijarlo
        // a ojo recorta «1.200» en cuánto los números crecen.
        double gutter = Math.Max(MinGutter, axis.Ticks.Max(t => Measure(Format(t)).Width) + 8);
        double plotLeft = gutter;
        double plotTop = PadTop;
        double plotWidth = Math.Max(1, w - gutter - PadRight);
        double plotHeight = Math.Max(1, h - PadTop - PadBottom);

        double Y(double value) => plotTop + plotHeight * (1 - axis.Fraction(value));
        double slot = plotWidth / buckets;
        double X(int i) => plotLeft + slot * (i + 0.5);

        DrawGrid(axis, plotLeft, plotWidth, Y);
        DrawXLabels(labels, buckets, plotTop + plotHeight, X, slot);

        var bars = series.Where(s => s.Kind == ChartSeriesKind.Bar).ToList();
        DrawBars(bars, buckets, slot, plotLeft, plotTop + plotHeight, Y);

        foreach (ChartSeries s in series.Where(s => s.Kind == ChartSeriesKind.Line))
        {
            DrawLine(s, X, Y, buckets);
        }

        AddCrosshair(plotTop, plotHeight);
        AddHitColumns(series, buckets, slot, plotLeft, plotTop, plotHeight);
    }

    // ---------- Rejilla y ejes ----------

    private void DrawGrid(AxisScale axis, double left, double width, Func<double, double> y)
    {
        foreach (double tick in axis.Ticks)
        {
            double top = y(tick);
            var rule = new Line
            {
                X1 = left,
                X2 = left + width,
                Y1 = top,
                Y2 = top,
                Stroke = GridBrush,
                StrokeThickness = 1,
                // Discreta: la rejilla sitúa, no compite con los datos.
                Opacity = tick == 0 ? 0.45 : 0.18,
                SnapsToDevicePixels = true,
            };
            Children.Add(rule);

            var label = new TextBlock
            {
                Text = Format(tick),
                FontSize = 10,
                Foreground = AxisBrush,
                Opacity = 0.75,
            };
            Size size = Measure(label.Text);
            SetLeft(label, Math.Max(0, left - size.Width - 8));
            SetTop(label, top - size.Height / 2);
            Children.Add(label);
        }
    }

    /// <summary>
    /// Las etiquetas del eje X se DILUYEN cuando no caben: se escribe una de cada n. Escribirlas
    /// todas y dejar que se pisen no es más información.
    /// </summary>
    private void DrawXLabels(IReadOnlyList<string> labels, int buckets, double baseline, Func<int, double> x, double slot)
    {
        if (labels.Count == 0)
        {
            return;
        }

        double widest = labels.Take(buckets).DefaultIfEmpty(string.Empty).Max(l => Measure(l).Width);
        int every = Math.Max(1, (int)Math.Ceiling((widest + 10) / Math.Max(1, slot)));

        for (int i = 0; i < buckets; i++)
        {
            // Se ancla al ULTIMO cubo y se cuenta hacia atras: la marca de «hoy» siempre sale.
            if ((buckets - 1 - i) % every != 0 || i >= labels.Count)
            {
                continue;
            }

            var label = new TextBlock
            {
                Text = labels[i],
                FontSize = 10,
                Foreground = AxisBrush,
                Opacity = 0.75,
            };
            Size size = Measure(label.Text);
            SetLeft(label, x(i) - size.Width / 2);
            SetTop(label, baseline + 5);
            Children.Add(label);
        }
    }

    // ---------- Las series ----------

    private void DrawBars(
        IReadOnlyList<ChartSeries> bars, int buckets, double slot, double left, double baseline, Func<double, double> y)
    {
        if (bars.Count == 0)
        {
            return;
        }

        double group = slot * 0.62;
        double barWidth = Math.Max(2, (group - (bars.Count - 1) * 2) / bars.Count);

        for (int i = 0; i < buckets; i++)
        {
            double groupLeft = left + slot * i + (slot - group) / 2;
            for (int s = 0; s < bars.Count; s++)
            {
                double value = i < bars[s].Values.Count ? bars[s].Values[i] : 0;
                if (value <= 0)
                {
                    continue;
                }

                double top = y(value);
                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = Math.Max(1, baseline - top),
                    Fill = bars[s].Stroke,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                };
                SetLeft(rect, groupLeft + s * (barWidth + 2));
                SetTop(rect, top);
                Children.Add(rect);
            }
        }
    }

    private void DrawLine(ChartSeries series, Func<int, double> x, Func<double, double> y, int buckets)
    {
        var points = new PointCollection();
        for (int i = 0; i < buckets && i < series.Values.Count; i++)
        {
            points.Add(new Point(x(i), y(series.Values[i])));
        }

        if (points.Count == 0)
        {
            return;
        }

        if (points.Count > 1)
        {
            var polyline = new Polyline
            {
                Points = points,
                Stroke = series.Stroke,
                StrokeThickness = LineThickness,
                StrokeLineJoin = PenLineJoin.Round,
                // «Otras» va SIEMPRE a trazos: un agregado de varias apps no puede leerse como
                // una app más por el hecho de tener color.
                StrokeDashArray = series.Dashed ? new DoubleCollection(new double[] { 4, 3 }) : null,
            };
            Children.Add(polyline);
        }

        // Con pocos cubos la línea sola no dice donde están las medidas. Marcas finas, no bolas.
        if (points.Count <= 30)
        {
            foreach (Point p in points)
            {
                var dot = new Ellipse { Width = 4, Height = 4, Fill = series.Stroke };
                SetLeft(dot, p.X - 2);
                SetTop(dot, p.Y - 2);
                Children.Add(dot);
            }
        }
    }

    // ---------- Cursor y tooltips ----------

    private void AddCrosshair(double top, double height)
    {
        _crosshair = new Line
        {
            Y1 = top,
            Y2 = top + height,
            Stroke = AxisBrush,
            StrokeThickness = 1,
            Opacity = 0.35,
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection(new double[] { 3, 3 }),
        };
        Children.Add(_crosshair);
    }

    /// <summary>
    /// Una banda invisible por cubo, de arriba abajo del área de dibujo. Es lo que hace que el
    /// tooltip salga apuntando a cualquier parte de la columna, y lo que permite enseñar TODAS
    /// las series de ese cubo juntas en vez de el valor suelto de la que se haya acertado.
    /// </summary>
    private void AddHitColumns(
        IReadOnlyList<ChartSeries> series, int buckets, double slot, double left, double top, double height)
    {
        // El tooltip tiene sitio para el nombre COMPLETO del cubo; el eje, no. Sin esto, un cubo
        // semanal se explicaba con la etiqueta de un solo día — y así se leía.
        var labels = TooltipLabels is { Count: > 0 } detailed ? detailed : Labels ?? Array.Empty<string>();
        for (int i = 0; i < buckets; i++)
        {
            int index = i;
            var band = new Rectangle
            {
                Width = Math.Max(1, slot),
                Height = height,
                Fill = Brushes.Transparent,
                ToolTip = BuildTooltip(series, index, index < labels.Count ? labels[index] : string.Empty),
            };
            SetLeft(band, left + slot * i);
            SetTop(band, top);

            double centre = left + slot * (i + 0.5);
            band.MouseEnter += (_, _) =>
            {
                if (_crosshair is null)
                {
                    return;
                }

                _crosshair.X1 = centre;
                _crosshair.X2 = centre;
                _crosshair.Visibility = Visibility.Visible;
            };
            band.MouseLeave += (_, _) =>
            {
                if (_crosshair is not null)
                {
                    _crosshair.Visibility = Visibility.Hidden;
                }
            };

            ToolTipService.SetInitialShowDelay(band, 120);
            ToolTipService.SetBetweenShowDelay(band, 0);
            Children.Add(band);
        }
    }

    private object BuildTooltip(IReadOnlyList<ChartSeries> series, int index, string label)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        bool any = false;
        foreach (ChartSeries s in series)
        {
            double value = index < s.Values.Count ? s.Values[index] : 0;
            if (value <= 0)
            {
                continue;
            }

            any = true;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new Rectangle
            {
                Width = 9,
                Height = 9,
                RadiusX = 2,
                RadiusY = 2,
                Fill = s.Stroke,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            });
            row.Children.Add(new TextBlock { Text = s.Name, Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(Unit) ? Format(value) : $"{Format(value)} {Unit}",
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            panel.Children.Add(row);
        }

        if (!any)
        {
            // Un cubo sin actividad lo dice. Un tooltip en blanco se lee como un fallo del programa.
            panel.Children.Add(new TextBlock { Text = "Sin actividad", Opacity = 0.7 });
        }

        return panel;
    }

    // ---------- Utilidades ----------

    private string Format(double value) => value.ToString(ValueFormat, CultureInfo.CurrentCulture);

    private Size Measure(string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return new Size(formatted.Width, formatted.Height);
    }
}
