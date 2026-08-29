using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Atalaya.App.Controls;

/// <summary>
/// Un texto de una línea que, cuando no cabe, se acorta <b>por el medio</b> y se explica en su
/// tooltip (F10.1 §2 y §3).
/// <para>
/// <b>Por qué un control y no <c>TextTrimming</c>.</b> WPF solo sabe recortar por el final, y por
/// el final es justo donde estos textos se distinguen: <c>ControllerConfiguration.cs</c> y
/// <c>ControllerMain.cs</c> comparten los diez primeros caracteres, así que un recorte trasero los
/// deja idénticos y encima se lleva la extensión. Y «No auditada · excluida por ta…» es una frase
/// cortada a mitad de palabra, que es lo que el usuario reportó.
/// </para>
/// <para>
/// El tooltip se pone <b>solo cuando hay algo que aclarar</b>. Un tooltip que repite el texto que
/// ya se está leyendo entero es ruido, y además enseña a ignorarlos.
/// </para>
/// </summary>
public sealed class MiddleEllipsisText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MiddleEllipsisText),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FullTextProperty = DependencyProperty.Register(
        nameof(FullText), typeof(string), typeof(MiddleEllipsisText),
        new PropertyMetadata(null));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(MiddleEllipsisText),
        new FrameworkPropertyMetadata(
            12.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(MiddleEllipsisText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(MiddleEllipsisText),
        new FrameworkPropertyMetadata(
            FontWeights.Normal,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Lo que dice el tooltip cuando el texto no cabe. Vacío usa <see cref="Text"/>; se pone
    /// aparte cuando el dato completo es otro — la celda enseña el nombre del fichero y el tooltip
    /// tiene que decir la ruta entera.
    /// </summary>
    public string? FullText
    {
        get => (string?)GetValue(FullTextProperty);
        set => SetValue(FullTextProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public MiddleEllipsisText()
    {
        ClipToBounds = true;
        ToolTipService.SetInitialShowDelay(this, 200);
    }

    /// <summary>Lo que de verdad se está pintando. Lo lee un test para afirmar que no hay cortes.</summary>
    internal string? Rendered { get; private set; }

    private Typeface Face
        => new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeight, FontStretches.Normal);

    /// <summary>
    /// Pide solo el alto de su línea. El ancho lo decide la columna: si pidiera el del texto
    /// completo, una ruta larga ensancharía la tabla entera y no habría nada que acortar.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
        => new(0, Math.Ceiling(FontSize * 1.4));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Rendered = null;
        ToolTip = null;

        string text = Text ?? string.Empty;
        if (text.Length == 0 || ActualWidth <= 0)
        {
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Retención 0: en una tabla la celda siempre dice algo — el tooltip lleva el resto. Es lo
        // contrario del treemap, donde una celda con media palabra ensucia a sus vecinas.
        string? fitted = TextFit.Fit(text, Face, FontSize, dpi, ActualWidth, retention: 0);
        if (fitted is null)
        {
            ToolTip = FullText ?? text;
            return;
        }

        Rendered = fitted;
        if (fitted.Length != text.Length || !string.IsNullOrEmpty(FullText))
        {
            ToolTip = FullText ?? text;
        }

        FormattedText formatted = TextFit.Format(fitted, Face, FontSize, dpi, Foreground);
        dc.DrawText(formatted, new Point(0, (ActualHeight - formatted.Height) / 2));
    }
}
