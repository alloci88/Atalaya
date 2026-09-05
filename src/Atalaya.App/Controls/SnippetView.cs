using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Atalaya.App.Controls;

/// <summary>
/// El panel de código de la ficha (F5.5 §3): números de línea <b>del fichero</b>, la línea del
/// hallazgo resaltada y un marcador en el margen.
/// <para>
/// <b>Por qué hace falta un control.</b> AvalonEdit numera siempre desde 1 el documento que le
/// des. Como el panel enseña un recorte —el miembro que contiene el hallazgo—, su numeración
/// nativa escribía «1..7» debajo de un hallazgo que vive en la línea 412: números que no sirven
/// para nada y que además contradicen la ubicación que la propia ficha muestra al lado. El margen
/// de aquí pinta <c>PrimeraLinea + n - 1</c>, así que lo que se lee es la línea real del fichero.
/// </para>
/// </summary>
public sealed class SnippetView : TextEditor
{
    /// <summary>Color de la banda que resalta la línea del hallazgo.</summary>
    private static readonly Brush HighlightFill = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xE0, 0xA0, 0x30)));

    /// <summary>El marcador del margen y el número resaltado: el mismo ámbar, ya sin transparencia.</summary>
    internal static readonly Brush HighlightMarker = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)));

    private static readonly Brush GutterBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x86)));

    private readonly FileLineNumberMargin _gutter = new();
    private readonly HighlightedLineRenderer _highlight = new();

    static SnippetView() => CodePalette.Adopt(HighlightingManager.Instance.GetDefinition("C#"));

    public SnippetView()
    {
        IsReadOnly = true;
        ShowLineNumbers = false;      // el margen de abajo sustituye al nativo
        WordWrap = false;
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New");
        FontSize = 12;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.HighlightCurrentLine = false;
        // Del TEMA, no de una constante (D-980): con `DynamicResource` el panel se repinta al
        // cambiar de tema sin reconstruir la ficha, igual que todo lo demás.
        SetResourceReference(BackgroundProperty, "Brush.Code.Surface");
        SetResourceReference(ForegroundProperty, "Brush.Code.Ink");
        BorderThickness = new Thickness(0);

        _gutter.Margin = new Thickness(0, 0, 8, 0);
        TextArea.LeftMargins.Add(_gutter);
        TextArea.TextView.BackgroundRenderers.Add(_highlight);
        TextArea.SelectionCornerRadius = 2;
    }

    /// <summary>El código a mostrar. Es la propiedad enlazable que <c>Text</c> nunca fue.</summary>
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(SnippetView),
        new FrameworkPropertyMetadata(string.Empty, OnCodeChanged));

    /// <summary>Qué línea del FICHERO es la primera del recorte (1-based).</summary>
    public static readonly DependencyProperty FirstLineProperty = DependencyProperty.Register(
        nameof(FirstLine), typeof(int), typeof(SnippetView),
        new FrameworkPropertyMetadata(1, OnAnchorChanged));

    /// <summary>La línea del FICHERO donde está el hallazgo. 0 = no resaltar nada.</summary>
    public static readonly DependencyProperty HighlightLineProperty = DependencyProperty.Register(
        nameof(HighlightLine), typeof(int), typeof(SnippetView),
        new FrameworkPropertyMetadata(0, OnAnchorChanged));

    /// <summary>La ruta del fichero: de ella sale el coloreado de sintaxis.</summary>
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(SnippetView),
        new FrameworkPropertyMetadata(string.Empty, OnSourcePathChanged));

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public int FirstLine
    {
        get => (int)GetValue(FirstLineProperty);
        set => SetValue(FirstLineProperty, value);
    }

    public int HighlightLine
    {
        get => (int)GetValue(HighlightLineProperty);
        set => SetValue(HighlightLineProperty, value);
    }

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    private static void OnCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SnippetView)d;
        view.Text = e.NewValue as string ?? string.Empty;
        view.ApplyAnchor();
    }

    private static void OnAnchorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SnippetView)d).ApplyAnchor();

    private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SnippetView)d;
        string path = e.NewValue as string ?? string.Empty;
        string ext = Path.GetExtension(path);
        view.SyntaxHighlighting = string.IsNullOrEmpty(ext)
            ? null
            : HighlightingManager.Instance.GetDefinitionByExtension(ext);
    }

    /// <summary>
    /// Traduce la línea del fichero a línea del documento y deja el resaltado donde toca. Fuera
    /// del recorte no se resalta nada: sería una banda ámbar sobre una línea que no es.
    /// </summary>
    private void ApplyAnchor()
    {
        _gutter.FirstLine = Math.Max(1, FirstLine);
        _gutter.HighlightLine = HighlightLine;

        int documentLine = HighlightLine <= 0 ? 0 : HighlightLine - Math.Max(1, FirstLine) + 1;
        _highlight.DocumentLine = documentLine >= 1 && documentLine <= Math.Max(Document?.LineCount ?? 0, 0)
            ? documentLine
            : 0;

        _gutter.InvalidateMeasure();
        _gutter.InvalidateVisual();
        TextArea.TextView.InvalidateLayer(KnownLayer.Background);

        if (_highlight.DocumentLine > 0)
        {
            // Con un miembro largo, la línea del hallazgo puede caer fuera de la primera pantalla.
            Dispatcher.BeginInvoke(new Action(() => ScrollToVerticalOffset(
                Math.Max(0, (_highlight.DocumentLine - 4) * TextArea.TextView.DefaultLineHeight))));
        }
    }

    /// <summary>
    /// La rueda del ratón deja de pelearse con la página (F5.6 §4, D-230).
    /// <para>
    /// <c>TextEditor</c> lleva dentro un <see cref="System.Windows.Controls.ScrollViewer"/> que se
    /// queda SIEMPRE el <c>MouseWheel</c>, esté o no en su tope. Resultado: bajar por la ficha
    /// pasando el cursor por encima del código era una lotería. Aquí solo se lo queda mientras
    /// pueda desplazarse en esa dirección; en el tope re-emite el evento hacia el padre, que es lo
    /// que WPF habría hecho si el interno no lo hubiera marcado como tratado.
    /// </para>
    /// </summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (SnippetScroll.ShouldBubble(e.Delta, VerticalOffset, ViewportHeight, ExtentHeight))
        {
            e.Handled = true;
            if (Parent is UIElement parent)
            {
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = MouseWheelEvent,
                    Source = this,
                });
            }

            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>Pinta la banda de fondo de la línea del hallazgo, debajo del texto.</summary>
    private sealed class HighlightedLineRenderer : IBackgroundRenderer
    {
        /// <summary>Línea del DOCUMENTO (no del fichero) a resaltar. 0 = ninguna.</summary>
        public int DocumentLine { get; set; }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (DocumentLine <= 0 || textView.Document is null || DocumentLine > textView.Document.LineCount)
            {
                return;
            }

            textView.EnsureVisualLines();
            var line = textView.Document.GetLineByNumber(DocumentLine);
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line))
            {
                drawingContext.DrawRectangle(
                    HighlightFill, null,
                    new Rect(0, rect.Top, Math.Max(textView.ActualWidth, rect.Right), rect.Height));
            }
        }
    }

    /// <summary>
    /// El margen de números: pinta la línea del FICHERO, no la del recorte, y marca en ámbar la
    /// del hallazgo.
    /// </summary>
    private sealed class FileLineNumberMargin : AbstractMargin
    {
        private int _firstLine = 1;
        private int _highlightLine;

        public int FirstLine
        {
            get => _firstLine;
            set
            {
                _firstLine = Math.Max(1, value);
                InvalidateMeasure();
            }
        }

        public int HighlightLine
        {
            get => _highlightLine;
            set => _highlightLine = value;
        }

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            }

            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += OnVisualLinesChanged;
            }

            base.OnTextViewChanged(oldTextView, newTextView);
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        protected override Size MeasureOverride(Size availableSize)
        {
            int widest = FirstLine + Math.Max(Document?.LineCount ?? 1, 1) - 1;
            FormattedText sample = Format(widest.ToString(CultureInfo.InvariantCulture), GutterBrush);
            return new Size(sample.Width + 10, 0);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            TextView? view = TextView;
            if (view is null || !view.VisualLinesValid)
            {
                return;
            }

            foreach (VisualLine visual in view.VisualLines)
            {
                int fileLine = FirstLine + visual.FirstDocumentLine.LineNumber - 1;
                bool marked = fileLine == HighlightLine;
                FormattedText text = Format(
                    fileLine.ToString(CultureInfo.InvariantCulture),
                    marked ? HighlightMarker : GutterBrush,
                    marked);

                double y = visual.GetTextLineVisualYPosition(visual.TextLines[0], VisualYPosition.TextTop)
                           - view.VerticalOffset;
                drawingContext.DrawText(text, new Point(RenderSize.Width - text.Width, y));

                if (marked)
                {
                    // El marcador del margen: la banda de fondo se pierde si la línea está en
                    // blanco, el marcador no.
                    drawingContext.DrawRectangle(
                        HighlightMarker, null, new Rect(0, y, 3, Math.Max(visual.Height, 1)));
                }
            }
        }

        private FormattedText Format(string value, Brush brush, bool bold = false)
        {
            var typeface = new Typeface(
                (FontFamily)GetValue(TextElement.FontFamilyProperty),
                FontStyles.Normal,
                bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStretches.Normal);

            return new FormattedText(
                value,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                (double)GetValue(TextElement.FontSizeProperty),
                brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
    }
}

/// <summary>
/// Los colores del panel de código, uno por tema (F26 Parte B, D-980).
/// <para>
/// <b>Antes había una sola superficie, oscura en los dos temas</b>, con el argumento de que un
/// bloque de código con fondo propio es una convención que se lee igual de bien en claro. Sobre el
/// crema de F26 no se lee igual: es un ladrillo negro en una pantalla cálida, y sobre todo
/// contradice el principio 5 — el tema cambia TODOS los recursos, no los que no pasaron por aquí.
/// </para>
/// <para>
/// <b>La sintaxis se ajusta a la superficie que toque, y se ajusta MIDIENDO.</b> El criterio
/// anterior era una luminosidad HSL mínima (0,66), que es una aproximación: dos colores con la
/// misma luminosidad HSL contrastan distinto contra el mismo fondo, porque el ojo no pesa igual el
/// rojo, el verde y el azul. Ahora se usa la razón de contraste de WCAG, la misma que gobierna la
/// paleta (D-947), y se camina la luminosidad hasta pasar 4,5:1. Sobre superficie oscura eso
/// significa aclarar; sobre clara, oscurecer — el mismo método, en las dos direcciones.
/// </para>
/// <para>
/// <b>Los colores de fábrica se guardan la primera vez.</b> Ajustar es destructivo: una vez
/// aclarado un color no se puede recuperar el original, y al cambiar de tema habría que ajustar
/// sobre lo ya ajustado, que a los dos cambios deja la sintaxis en blanco. Con la copia, cada tema
/// se deriva siempre del original.
/// </para>
/// </summary>
internal static class CodePalette
{
    /// <summary>Lo que hace falta para leer texto pequeño, que es lo que es el código.</summary>
    private const double MinContrast = 4.5;

    /// <summary>Los colores de fábrica de la definición, antes de ajustar nada.</summary>
    private static readonly Dictionary<string, Color> Original = new(StringComparer.Ordinal);

    private static IHighlightingDefinition? _definition;

    /// <summary>La superficie del panel en el tema vigente. La pone <see cref="Apply"/>.</summary>
    public static Brush Surface { get; private set; } = Frozen(Color.FromRgb(0x1B, 0x1B, 0x20));

    /// <summary>El texto sin token reconocido.</summary>
    public static Brush Foreground { get; private set; } = Frozen(Color.FromRgb(0xD6, 0xD6, 0xDD));

    /// <summary>El color de la superficie vigente, para poder medir contra él.</summary>
    public static Color SurfaceColor { get; private set; } = Color.FromRgb(0x1B, 0x1B, 0x20);

    /// <summary>
    /// Pone la paleta del tema y reajusta la sintaxis contra su superficie. Lo llama
    /// <c>ThemeService</c> después de cambiar el diccionario, que es el único sitio que sabe
    /// cuándo cambia el tema.
    /// </summary>
    public static void Apply(Color surface, Color ink)
    {
        SurfaceColor = surface;
        Surface = Frozen(surface);
        Foreground = Frozen(ink);
        Refit();
    }

    /// <summary>
    /// Toma la definición de coloreado y guarda sus colores de fábrica. Se llama una vez; las
    /// siguientes no hacen nada, que es lo que permite que <see cref="Apply"/> se pueda llamar en
    /// cada cambio de tema sin degradar los colores.
    /// </summary>
    public static void Adopt(IHighlightingDefinition? definition)
    {
        if (definition is null || _definition is not null)
        {
            return;
        }

        _definition = definition;

        try
        {
            foreach (HighlightingColor color in definition.NamedHighlightingColors)
            {
                if (color.Foreground?.GetColor(null) is { } rgb && color.Name is { Length: > 0 })
                {
                    Original[color.Name] = rgb;
                }
            }
        }
        catch (Exception)
        {
            // Una definición congelada —o un pincel que no sabe dar su color— se queda como está:
            // mejor el coloreado de fábrica que reventar la ficha por un matiz.
        }

        Refit();
    }

    private static void Refit()
    {
        if (_definition is null)
        {
            return;
        }

        try
        {
            foreach (HighlightingColor color in _definition.NamedHighlightingColors)
            {
                if (color.Name is not { Length: > 0 } name || !Original.TryGetValue(name, out Color rgb))
                {
                    continue;
                }

                color.Foreground = new SimpleHighlightingBrush(Fit(rgb, SurfaceColor));
            }
        }
        catch (Exception)
        {
            // Igual que arriba: el coloreado es un extra, no la ficha.
        }
    }

    /// <summary>
    /// Lleva un color hasta AA contra la superficie, conservando su tono. Camina la luminosidad en
    /// la dirección que separa: hacia el blanco sobre fondo oscuro, hacia el negro sobre claro.
    /// Si ni el extremo llega —un amarillo puro sobre blanco no llega nunca— devuelve lo mejor que
    /// encontró: peor un color un poco justo que uno inventado de otro tono.
    /// </summary>
    internal static Color Fit(Color color, Color surface)
    {
        if (Contrast(color, surface) >= MinContrast)
        {
            return color;
        }

        (double h, double s, double l) = ToHsl(color);
        bool darkSurface = Luminance(surface) < 0.5;

        Color best = color;
        double bestRatio = Contrast(color, surface);

        for (int step = 1; step <= 100; step++)
        {
            double next = darkSurface ? l + (step * 0.01) : l - (step * 0.01);
            if (next is < 0 or > 1)
            {
                break;
            }

            Color candidate = FromHsl(h, Math.Min(s, 0.85), next, color.A);
            double ratio = Contrast(candidate, surface);
            if (ratio > bestRatio)
            {
                best = candidate;
                bestRatio = ratio;
            }

            if (ratio >= MinContrast)
            {
                return candidate;
            }
        }

        return best;
    }

    /// <summary>La razón de contraste de WCAG 2.1, la misma que gobierna la paleta (D-947).</summary>
    internal static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static (double H, double S, double L) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        double delta = max - min;
        double s = delta == 0 ? 0 : delta / (1 - Math.Abs((2 * l) - 1));
        double h = delta == 0 ? 0
            : max == r ? 60 * ((((g - b) / delta) + 6) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);

        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l, byte alpha)
    {
        double c = (1 - Math.Abs((2 * l) - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        double m = l - (c / 2);
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(
            alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
