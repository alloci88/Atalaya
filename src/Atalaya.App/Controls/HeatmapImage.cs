using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Atalaya.App.Services;

namespace Atalaya.App.Controls;

/// <summary>
/// Todo lo que hace falta para que la imagen exportada se explique sola (F10 §3).
/// </summary>
/// <param name="Title">«{App} · mapa de calor · {fecha}».</param>
/// <param name="Subtitle">Qué mide el color y sobre cuánto: la frase sin la cual el mapa no se lee.</param>
/// <param name="Footer">«Atalaya · {organización}», discreto.</param>
/// <param name="Groups">Las celdas del treemap. Vacío en el nivel 1, que se dibuja con tarjetas.</param>
/// <param name="Cards">Las tarjetas de módulo del nivel 1. Vacío cuando lo exportado es un módulo.</param>
/// <param name="Thermometer">La barra de la cabecera: el reparto de la aplicación entera.</param>
public sealed record HeatmapImageRequest(
    string Title,
    string Subtitle,
    string Footer,
    IReadOnlyList<HeatGroup> Groups,
    IReadOnlyList<HeatLegendItem> Legend,
    bool Dark,
    IReadOnlyList<ModuleCard>? Cards = null,
    IReadOnlyList<HeatSegment>? Thermometer = null)
{
    /// <summary>La lámina del nivel 1: tarjetas de módulo en vez del treemap de un módulo.</summary>
    public bool IsOverview => Groups.Count == 0 && Cards is { Count: > 0 };
}

/// <summary>
/// Compone y guarda el PNG del mapa (F10 §3): la diapositiva.
/// <para>
/// <b>Por qué se compone una vista aparte y no se fotografía la de la pantalla.</b> Lo que se ve
/// en la ventana depende de cuánto se haya estirado, de dónde esté el scroll y de qué tapen las
/// barras: una captura de eso es un recorte de pantalla con otro nombre, que es exactamente lo
/// que esta función existe para no tener que hacer. Aquí el lienzo tiene un tamaño fijo, su
/// título, su leyenda y su pie, y sale igual desde una ventana maximizada que desde una de
/// 1366×768.
/// </para>
/// <para>
/// <b>Y por qué con colores explícitos y sin un solo control de WPF-UI.</b> El árbol se mide y se
/// dispone FUERA de la ventana, donde no hay diccionario de temas que resolver: un
/// <c>DynamicResource</c> ahí no falla, se queda en su valor por defecto —negro sobre negro— y el
/// PNG sale mal sin que nada avise. Todos los colores de la imagen se pasan ya resueltos.
/// </para>
/// </summary>
public static class HeatmapImage
{
    /// <summary>El lienzo, en unidades independientes del dispositivo. 16:10, tamaño de lámina.</summary>
    public const int Width = 1600;

    /// <inheritdoc cref="Width"/>
    public const int Height = 1000;

    /// <summary>Se renderiza al doble de resolución: un PNG de 3200 px aguanta un proyector.</summary>
    public const double Scale = 2.0;

    /// <summary>
    /// El nombre con el que se propone guardar. Dice qué es y de cuándo, que es lo que hace
    /// encontrable un fichero dentro de tres meses.
    /// </summary>
    public static string FileName(string slug, DateTimeOffset when)
    {
        string safe = string.Concat((slug.Length == 0 ? "atalaya" : slug)
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        return $"atalaya-{safe}-mapa-de-calor-{when:yyyy-MM-dd}.png";
    }

    /// <summary>Compone el lienzo. Público para poder medirlo y renderizarlo en un test.</summary>
    public static FrameworkElement Compose(HeatmapImageRequest request)
    {
        // La MISMA superficie que la vista (DensityScale.Surface): es contra ese fondo contra el
        // que se verificó el contraste de la rampa, así que la lámina no puede tener otro.
        Brush paper = HeatBrushes.Solid(DensityScale.Surface.For(request.Dark));
        Brush ink = HeatBrushes.Solid(request.Dark ? "#F2F2F4" : "#1A1A1D");
        Brush muted = HeatBrushes.Solid(request.Dark ? "#9DA2AA" : "#65696F");
        Brush hairline = HeatBrushes.Solid(request.Dark ? "#3A3F47" : "#D6D8DC");

        var root = new Grid { Background = paper, Width = Width, Height = Height };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Margin = new Thickness(40, 34, 40, 18) };
        head.Children.Add(Text(request.Title, 30, FontWeights.SemiBold, ink));
        head.Children.Add(Text(request.Subtitle, 14, FontWeights.Normal, muted, new Thickness(0, 8, 0, 0)));

        if (request.Thermometer is { Count: > 0 } segments)
        {
            head.Children.Add(Bar(segments, 26, new Thickness(0, 14, 0, 0), hairline));
        }

        Grid.SetRow(head, 0);
        root.Children.Add(head);

        UIElement body = request.IsOverview
            ? Overview(request, ink, muted, hairline)
            : new Treemap
            {
                Groups = request.Groups,

                // La lámina escribe los nombres ENTEROS: aquí el sitio sobra y quien la recibe por
                // correo no ha visto la declaración del prefijo que sí lleva la cabecera de la vista.
                AllowShortNames = false,
                Margin = new Thickness(40, 0, 40, 0),
                SurfaceBrush = paper,
                StrokeBrush = hairline,
                LabelBrush = ink,
                MutedBrush = muted,
                UnknownFill = HeatBrushes.Solid(DensityScale.Unknown.For(request.Dark)),
            };

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var legend = new WrapPanel { Margin = new Thickness(40, 20, 40, 0) };
        foreach (HeatLegendItem item in request.Legend)
        {
            legend.Children.Add(Swatch(item, ink));
        }

        Grid.SetRow(legend, 2);
        root.Children.Add(legend);

        var foot = Text(request.Footer, 12, FontWeights.Normal, muted, new Thickness(40, 18, 40, 26));
        Grid.SetRow(foot, 3);
        root.Children.Add(foot);

        return root;
    }

    /// <summary>Guarda el PNG. Debe llamarse desde el hilo de UI: WPF solo renderiza en STA.</summary>
    public static void Save(HeatmapImageRequest request, string path)
    {
        FrameworkElement visual = Compose(request);
        visual.Measure(new Size(Width, Height));
        visual.Arrange(new Rect(0, 0, Width, Height));
        visual.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(Width * Scale),
            (int)Math.Round(Height * Scale),
            96 * Scale,
            96 * Scale,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>
    /// La lámina del nivel 1: las tarjetas de módulo en una rejilla. Es lo que se ve en pantalla,
    /// no un treemap distinto — una diapositiva que no se parece a la vista de la que salió obliga
    /// a explicar dos cosas en vez de una.
    /// </summary>
    private static UIElement Overview(
        HeatmapImageRequest request, Brush ink, Brush muted, Brush hairline)
    {
        var panel = new WrapPanel { Margin = new Thickness(32, 0, 32, 0) };
        foreach (ModuleCard card in request.Cards!)
        {
            panel.Children.Add(Card(card, ink, muted, hairline, request.Dark));
        }

        return panel;
    }

    private static UIElement Card(
        ModuleCard card, Brush ink, Brush muted, Brush hairline, bool dark)
    {
        var body = new StackPanel { Margin = new Thickness(12, 9, 12, 10) };

        // El título NO se parte en dos líneas: «XBLASTCustomRibbonContro / l» descuadra la tarjeta
        // y no se lee mejor que recortado.
        TextBlock title = Text(card.FullName, 15, FontWeights.SemiBold, ink);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(title);
        body.Children.Add(Text(card.Size, 11.5, FontWeights.Normal, muted, new Thickness(0, 3, 0, 0)));

        body.Children.Add(Bar(
            new[]
            {
                new HeatSegment("auditado", 0, card.Audited, card.Density ?? ink, string.Empty),
                new HeatSegment("sin auditar", 0, card.Pending,
                    HeatBrushes.Solid(DensityScale.Unknown.For(dark)), string.Empty),
            },
            8,
            new Thickness(0, 9, 0, 0),
            hairline));

        body.Children.Add(Text(card.CoverageText, 11, FontWeights.Normal, muted, new Thickness(0, 5, 0, 0)));
        body.Children.Add(Text(
            $"{card.DensityText} · {card.DensityCaption}", 11.5, FontWeights.Normal, ink,
            new Thickness(0, 6, 0, 0)));

        if (card.HasFindings)
        {
            body.Children.Add(Bar(card.Severities, 6, new Thickness(0, 7, 0, 0), hairline));
            body.Children.Add(Text(card.SeverityText, 11, FontWeights.Normal, muted, new Thickness(0, 4, 0, 0)));
        }

        var frame = new Grid { Width = 232, MinHeight = 150, Margin = new Thickness(8) };
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // La franja de densidad va al borde, no de fondo: el relleno completo teñiría el texto y
        // haría competir la densidad con todo lo demás de la tarjeta.
        var stripe = new Border
        {
            Width = 5,
            Background = card.Density ?? HeatBrushes.Solid(DensityScale.Unknown.For(dark)),
            CornerRadius = new CornerRadius(3, 0, 0, 3),
        };
        Grid.SetColumn(stripe, 0);
        frame.Children.Add(stripe);

        var chrome = new Border
        {
            BorderBrush = hairline,
            BorderThickness = new Thickness(0, 1, 1, 1),
            CornerRadius = new CornerRadius(0, 3, 3, 0),
            Child = body,
        };
        Grid.SetColumn(chrome, 1);
        frame.Children.Add(chrome);
        return frame;
    }

    /// <summary>Una barra apilada: el termómetro, la cobertura de una tarjeta y su tira C/A/M/B.</summary>
    private static UIElement Bar(
        IReadOnlyList<HeatSegment> segments, double height, Thickness margin, Brush hairline)
    {
        var grid = new Grid { Height = height };
        var visible = segments.Where(seg => seg.Share > 0).ToList();

        for (int i = 0; i < visible.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(visible[i].Share, GridUnitType.Star),
            });

            var piece = new Border { Background = visible[i].Brush };
            Grid.SetColumn(piece, i);
            grid.Children.Add(piece);
        }

        return new Border
        {
            BorderBrush = hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = grid,
            Margin = margin,
        };
    }

    private static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static TextBlock Text(
        string text, double size, FontWeight weight, Brush brush, Thickness? margin = null)
        => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? default,
        };

    private static UIElement Swatch(HeatLegendItem item, Brush ink)
    {
        var box = new System.Windows.Shapes.Rectangle
        {
            Width = 22,
            Height = 14,
            RadiusX = 2,
            RadiusY = 2,
            Fill = item.Fill,
            Stroke = ink,
            StrokeThickness = item.Dotted ? 1.4 : 0.6,
            StrokeDashArray = item.Dotted ? new DoubleCollection(new double[] { 2, 2 }) : null,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 8) };
        if (item.HasSwatch)
        {
            row.Children.Add(box);
        }

        var label = Text(item.Label, 12.5, FontWeights.Normal, ink, new Thickness(item.HasSwatch ? 8 : 0, 0, 0, 0));
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        return row;
    }
}
