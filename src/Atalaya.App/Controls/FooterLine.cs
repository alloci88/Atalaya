using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Atalaya.App.Services;

namespace Atalaya.App.Controls;

/// <summary>
/// El pie de una sesión, en una línea que <b>nunca trunca</b> (F17-RETOQUE).
/// <para>
/// <b>El defecto que lo motiva.</b> El pie era un <c>StackPanel</c> horizontal: con el segmento
/// de Claude Code —llamadas, tokens con la caché desglosada y la frase del coste— el bloque
/// antiguo de tokens que venía detrás se salía por el borde y se veía cortado a media palabra.
/// Un StackPanel no reparte: apila, y lo que no cabe se recorta sin avisar.
/// </para>
/// <para>
/// <b>La regla.</b> Cada segmento trae sus formas de más larga a más corta y una prioridad
/// (<see cref="FooterSegment"/>). Se empieza con todo en su forma completa; mientras no quepa, se
/// abrevia el segmento de MAYOR prioridad que todavía pueda abreviarse —y cuando agota sus formas,
/// se retira—; los de prioridad 0 no ceden nunca. El texto que se pinta es siempre una forma
/// entera, y el detalle completo está en el tooltip. Con ancho infinito (dentro de un panel que
/// no acota) se pinta todo entero.
/// </para>
/// <para>
/// Se dibuja a mano, como <see cref="ChartPlot"/>: lo que hay que poder afirmar en un test es qué
/// forma se eligió a cada ancho, y eso es <see cref="Chosen"/>, calculado en la medida y sin
/// pintar un píxel.
/// </para>
/// </summary>
public sealed class FooterLine : FrameworkElement
{
    private const string Separator = "·";
    private const double SeparatorGap = 10;
    private const double SeparatorOpacity = 0.4;

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<FooterSegment>), typeof(FooterLine),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentsChanged));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(FooterLine),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(FooterLine),
        new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly List<(FooterSegment Segment, string? Text)> _chosen = new();

    public IReadOnlyList<FooterSegment>? Segments
    {
        get => (IReadOnlyList<FooterSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>
    /// Lo que se eligió pintar de cada segmento en la última medida: la forma que cupo, o
    /// <c>null</c> si el segmento se retiró. Es lo que los tests afirman.
    /// </summary>
    public IReadOnlyList<string?> Chosen => _chosen.Select(c => c.Text).ToList();

    /// <summary>El detalle entero, siempre: es lo que hay detrás de cualquier forma abreviada.</summary>
    public string FullText => string.Join(" · ", (Segments ?? Array.Empty<FooterSegment>()).Select(s => s.Full));

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var line = (FooterLine)d;
        line.ToolTip = line.FullText.Length > 0 ? line.FullText : null;
    }

    /// <summary>
    /// La elección, separada de WPF para poder afirmarla sin montar nada: qué forma de cada
    /// segmento cabe en <paramref name="width"/> dado cómo mide cada texto.
    /// </summary>
    internal static IReadOnlyList<string?> Choose(
        IReadOnlyList<FooterSegment> segments, double width, Func<string, bool, double> measure, double separatorWidth)
    {
        int[] level = new int[segments.Count];
        bool[] hidden = new bool[segments.Count];

        // Los trozos que son solo para el tooltip nacen ocultos y no vuelven: no compiten por el
        // sitio de la línea ni cuando sobra (F23 §6).
        for (int i = 0; i < segments.Count; i++)
        {
            hidden[i] = segments[i].TooltipOnly;
        }

        double Total()
        {
            double total = 0;
            int visible = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                if (hidden[i])
                {
                    continue;
                }

                total += measure(segments[i].Candidates[level[i]], segments[i].Bold);
                visible++;
            }

            return total + Math.Max(0, visible - 1) * separatorWidth;
        }

        while (!double.IsInfinity(width) && Total() > width + 0.5)
        {
            // El que más prioridad tiene entre los que aún pueden ceder: primero abreviando, y
            // agotadas sus formas, retirándose. Prioridad 0 no cede nunca.
            int pick = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                if (hidden[i] || segments[i].Priority <= 0)
                {
                    continue;
                }

                if (pick < 0 || segments[i].Priority > segments[pick].Priority)
                {
                    pick = i;
                }
            }

            if (pick < 0)
            {
                break; // nada puede ceder: lo que hay es lo mínimo, y se pinta entero aunque no quepa
            }

            if (level[pick] + 1 < segments[pick].Candidates.Count)
            {
                level[pick]++;
            }
            else
            {
                hidden[pick] = true;
            }
        }

        var chosen = new string?[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            chosen[i] = hidden[i] ? null : segments[i].Candidates[level[i]];
        }

        return chosen;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _chosen.Clear();
        IReadOnlyList<FooterSegment> segments = Segments ?? Array.Empty<FooterSegment>();
        if (segments.Count == 0)
        {
            return new Size(0, 0);
        }

        double separator = Format(Separator, false).Width + 2 * SeparatorGap;
        IReadOnlyList<string?> chosen = Choose(segments, availableSize.Width, (t, b) => Format(t, b).Width, separator);

        double width = 0;
        double height = 0;
        int visible = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            _chosen.Add((segments[i], chosen[i]));
            if (chosen[i] is null)
            {
                continue;
            }

            FormattedText text = Format(chosen[i]!, segments[i].Bold);
            width += text.Width;
            height = Math.Max(height, text.Height);
            visible++;
        }

        width += Math.Max(0, visible - 1) * separator;
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext dc)
    {
        double x = 0;
        bool first = true;
        double lineHeight = Format("Ag", false).Height;
        foreach ((FooterSegment segment, string? text) in _chosen)
        {
            if (text is null)
            {
                continue;
            }

            if (!first)
            {
                FormattedText sep = Format(Separator, false, SeparatorOpacity);
                x += SeparatorGap;
                dc.DrawText(sep, new Point(x, (lineHeight - sep.Height) / 2));
                x += sep.Width + SeparatorGap;
            }

            FormattedText t = Format(text, segment.Bold, segment.Opacity);
            dc.DrawText(t, new Point(x, (lineHeight - t.Height) / 2));
            x += t.Width;
            first = false;
        }
    }

    private FormattedText Format(string text, bool bold, double opacity = 1.0)
    {
        Brush brush = Foreground;
        if (opacity < 1.0)
        {
            brush = brush.Clone();
            brush.Opacity = opacity;
            brush.Freeze();
        }

        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            FontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
