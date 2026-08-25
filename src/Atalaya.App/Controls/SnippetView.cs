using System.Globalization;
using System.Windows;
using System.Windows.Documents;
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

    static SnippetView() => CodePalette.LightenForDarkSurface(HighlightingManager.Instance.GetDefinition("C#"));

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
        Background = CodePalette.Surface;
        Foreground = CodePalette.Foreground;
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
/// La paleta del panel de código.
/// <para>
/// Las definiciones que trae AvalonEdit están pensadas para papel blanco: azul marino, negro,
/// verde oscuro. Atalaya abre en tema oscuro, así que ese coloreado dejaba las palabras clave
/// invisibles. En vez de escribir una definición nueva —y tener que mantenerla— se <b>aclara</b>
/// la que ya hay: cada color se sube de luminosidad hasta pasar de un umbral legible sobre fondo
/// oscuro, conservando su tono, que es lo que hace reconocible el resaltado.
/// </para>
/// <para>
/// El panel lleva su propia superficie oscura en los dos temas. Un bloque de código con fondo
/// propio es una convención que se lee igual de bien en claro, y evita tener que mantener dos
/// paletas para el mismo texto.
/// </para>
/// </summary>
internal static class CodePalette
{
    /// <summary>La superficie del panel, la misma en tema claro y oscuro.</summary>
    public static readonly Brush Surface = Frozen(Color.FromRgb(0x1B, 0x1B, 0x20));

    /// <summary>El texto sin token reconocido.</summary>
    public static readonly Brush Foreground = Frozen(Color.FromRgb(0xD6, 0xD6, 0xDD));

    /// <summary>Por debajo de esta luminosidad un color no se lee sobre la superficie.</summary>
    private const double MinLuminance = 0.66;

    public static void LightenForDarkSurface(IHighlightingDefinition? definition)
    {
        if (definition is null)
        {
            return;
        }

        try
        {
            foreach (HighlightingColor color in definition.NamedHighlightingColors)
            {
                if (color.Foreground?.GetColor(null) is not { } rgb)
                {
                    continue;
                }

                color.Foreground = new SimpleHighlightingBrush(Lighten(rgb));
            }
        }
        catch (Exception)
        {
            // Una definición congelada —o un pincel que no sabe dar su color— se queda como está:
            // mejor el coloreado de fábrica que reventar la ficha por un matiz.
        }
    }

    /// <summary>Sube la luminosidad conservando el tono. El gris puro acaba en gris claro.</summary>
    internal static Color Lighten(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        if (l >= MinLuminance)
        {
            return color;
        }

        double delta = max - min;
        double s = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * l - 1));
        double h = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta + 6) % 6)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);

        return FromHsl(h, Math.Min(s, 0.85), MinLuminance, color.A);
    }

    private static Color FromHsl(double h, double s, double l, byte alpha)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
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
