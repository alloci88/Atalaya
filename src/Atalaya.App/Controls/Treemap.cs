using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Atalaya.App.Services;

namespace Atalaya.App.Controls;

/// <summary>
/// Una celda del mapa: una unidad de código.
/// </summary>
/// <param name="Weight">Su tamaño (LOC). Es lo que decide el ÁREA, nunca el color.</param>
/// <param name="Fill">
/// El color de su paso en la escala de densidad, o <c>null</c> cuando el valor es DESCONOCIDO.
/// Null no es «pinta el color más frío»: el control dibuja entonces el gris tramado, que es lo
/// único honesto que se puede decir de una unidad que nadie ha mirado (F10 §1).
/// </param>
/// <param name="Qualified">
/// El relleno no cuenta toda la verdad: gris con deuda conocida, o medida de un código que ya
/// cambió. El control lo marca con contorno punteado; el tooltip dice cuál de las dos.
/// </param>
public sealed record HeatCell(
    string Label, double Weight, Brush? Fill, bool Qualified, string Tooltip, object? Payload);

/// <summary>Un grupo del mapa: un módulo, con su banda de cabecera y sus celdas.</summary>
public sealed record HeatGroup(
    string Name,
    string Detail,
    double Weight,
    Brush? Fill,
    bool Qualified,
    string Tooltip,
    object? Payload,
    IReadOnlyList<HeatCell> Cells);

/// <summary>
/// El treemap de dos niveles del mapa de calor (F10 §2). <b>El área es el tamaño (LOC) y el color
/// es la densidad de deuda</b>: son dos canales independientes, y por eso el módulo grande y sucio
/// se distingue de un vistazo del pequeño y sucio, que es justo lo que una lista ordenada no puede
/// enseñar.
/// <para>
/// <b>Por qué se dibuja en <c>OnRender</c> y no con hijos del <c>Canvas</c>.</b> Un clon real trae
/// ~900 unidades: 900 <c>Rectangle</c> con su <c>ToolTip</c> y sus tres manejadores cada uno son
/// 900 elementos vivos en el árbol visual, con su medida y su disposición en cada cambio de
/// tamaño. Dibujando sobre el <c>DrawingContext</c> el coste es una pasada de geometría, y el
/// impacto se resuelve contra la lista de rectángulos —una comparación por celda, solo cuando el
/// ratón se mueve—. Es la misma razón por la que <see cref="ChartPlot"/> dibuja a mano en vez de
/// traerse una librería: el trabajo es geometría trivial y las reglas son nuestras.
/// </para>
/// </summary>
public sealed class Treemap : FrameworkElement
{
    private const double GroupGap = 3;
    private const double CellGap = 1;
    private const double HeaderHeight = 22;
    private const double LabelPadX = 5;
    private const double LabelPadY = 3;

    /// <summary>Un rectángulo ya colocado, con lo que hay que decir y hacer si se pulsa.</summary>
    private sealed record Hit(TreemapRect Rect, string Tooltip, object? Payload, bool IsGroup);

    /// <summary>
    /// La tipografía, una vez. Construir un <c>Typeface</c> por etiqueta es de lo poco caro que
    /// hay en este dibujo, y aquí se dibujan casi mil.
    /// </summary>
    private static readonly Typeface Regular =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <inheritdoc cref="Regular"/>
    private static readonly Typeface Bold =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly DashStyle Dots = new(new double[] { 1.5, 2.5 }, 0);

    private readonly List<Hit> _hits = new();
    private Hit? _hover;

    /// <summary>El punteado del render en curso: uno por pasada, no uno por celda.</summary>
    private Pen? _dotted;

    public static readonly DependencyProperty GroupsProperty = DependencyProperty.Register(
        nameof(Groups), typeof(IReadOnlyList<HeatGroup>), typeof(Treemap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnknownFillProperty = DependencyProperty.Register(
        nameof(UnknownFill), typeof(Brush), typeof(Treemap),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(SurfaceBrush), typeof(Brush), typeof(Treemap),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(Brush), typeof(Treemap),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(Treemap),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(Treemap),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TileCommandProperty = DependencyProperty.Register(
        nameof(TileCommand), typeof(ICommand), typeof(Treemap), new PropertyMetadata(null));

    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand), typeof(ICommand), typeof(Treemap), new PropertyMetadata(null));

    /// <summary>Los módulos y sus unidades. Con uno solo, el mapa está ampliado a ese módulo.</summary>
    public IReadOnlyList<HeatGroup>? Groups
    {
        get => (IReadOnlyList<HeatGroup>?)GetValue(GroupsProperty);
        set => SetValue(GroupsProperty, value);
    }

    /// <inheritdoc cref="DensityScale.Unknown"/>
    public Brush UnknownFill
    {
        get => (Brush)GetValue(UnknownFillProperty);
        set => SetValue(UnknownFillProperty, value);
    }

    /// <summary>El fondo sobre el que se recortan las celdas (el de la tarjeta que lo contiene).</summary>
    public Brush SurfaceBrush
    {
        get => (Brush)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush MutedBrush
    {
        get => (Brush)GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    /// <summary>Un clic: entrega el <c>Payload</c> de lo que se pulsó (módulo o unidad).</summary>
    public ICommand? TileCommand
    {
        get => (ICommand?)GetValue(TileCommandProperty);
        set => SetValue(TileCommandProperty, value);
    }

    /// <summary>Doble clic sobre una unidad: el gesto de auditarla.</summary>
    public ICommand? ActivateCommand
    {
        get => (ICommand?)GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    public Treemap()
    {
        ClipToBounds = true;
        Focusable = false;
        ToolTipService.SetInitialShowDelay(this, 120);
        ToolTipService.SetShowDuration(this, 30000);
        ToolTipService.SetBetweenShowDelay(this, 0);
    }

    /// <summary>
    /// Lo colocado en el último dibujo, para poder afirmar en un test que las áreas son
    /// proporcionales y que nada se solapa sin tener que mirar píxeles.
    /// </summary>
    internal IReadOnlyList<(TreemapRect Rect, object? Payload, bool IsGroup)> Placed
        => _hits.Select(h => (h.Rect, h.Payload, h.IsGroup)).ToList();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hits.Clear();

        var bounds = new TreemapRect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(SurfaceBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var groups = Groups ?? Array.Empty<HeatGroup>();
        if (groups.Count == 0 || bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        var pen = new Pen(StrokeBrush, 1);
        pen.Freeze();

        // El punteado es un MATIZ, no un grito: a plena tinta, las 34 unidades grandes de un clon
        // real convertían el mapa en una rejilla de rectángulos discontinuos y tapaban lo único
        // que el mapa tenía que enseñar de un vistazo. Atenuado sigue viéndose al mirar la celda.
        _dotted = new Pen(Fade(LabelBrush, 0x66), 1) { DashStyle = Dots };
        _dotted.Freeze();

        foreach (TreemapTile<HeatGroup> tile in TreemapLayout.Squarify(groups, g => g.Weight, bounds))
        {
            DrawGroup(dc, tile.Item, Deflate(tile.Rect, GroupGap / 2), pen);
        }
    }

    private void DrawGroup(DrawingContext dc, HeatGroup group, TreemapRect rect, Pen pen)
    {
        if (rect.Width <= 1 || rect.Height <= 1)
        {
            return;
        }

        double header = Math.Min(HeaderHeight, rect.Height * 0.5);
        var band = new TreemapRect(rect.X, rect.Y, rect.Width, header);
        var inner = new TreemapRect(rect.X, rect.Y + header, rect.Width, rect.Height - header);

        // La banda va del color del MÓDULO; las celdas, del de cada unidad. Es el mismo dato en
        // dos escalas, no dos datos.
        Fill(dc, band, group.Fill, pen, group.Qualified);
        _hits.Add(new Hit(band, group.Tooltip, group.Payload, IsGroup: true));

        Brush ink = Contrast(group.Fill);
        double used = DrawText(dc, group.Name, band, ink, 12, bold: true);
        if (used > 0 && group.Detail.Length > 0)
        {
            // El detalle va en la MISMA tinta atenuada, no en el gris del tema: sobre la banda
            // clara de un módulo del paso 5, un gris fijo se vuelve ilegible justo en el módulo
            // que más importa leer.
            DrawText(
                dc,
                group.Detail,
                new TreemapRect(band.X + used + 8, band.Y, band.Width - used - 8, band.Height),
                Faded(ink),
                11,
                bold: false);
        }

        if (inner.Height <= 1)
        {
            return;
        }

        foreach (TreemapTile<HeatCell> tile in TreemapLayout.Squarify(group.Cells, c => c.Weight, inner))
        {
            TreemapRect cell = Deflate(tile.Rect, CellGap / 2);
            if (cell.Width <= 0.5 || cell.Height <= 0.5)
            {
                continue;
            }

            Fill(dc, cell, tile.Item.Fill, pen, tile.Item.Qualified);
            _hits.Add(new Hit(cell, tile.Item.Tooltip, tile.Item.Payload, IsGroup: false));

            // La etiqueta de la unidad SOLO si cabe entera. Un nombre recortado a «Contro…» no
            // identifica nada y ensucia la celda de al lado.
            DrawText(dc, tile.Item.Label, cell, Contrast(tile.Item.Fill), 11, bold: false, onlyIfItFits: true);
        }
    }

    /// <summary>
    /// Pinta el fondo de una celda. Sin color —valor desconocido— va el gris tramado, y el
    /// contorno punteado marca que el relleno no lo cuenta todo.
    /// </summary>
    private void Fill(DrawingContext dc, TreemapRect rect, Brush? fill, Pen pen, bool qualified)
    {
        var r = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        dc.DrawRectangle(fill ?? UnknownFill, pen, r);

        if (!qualified || rect.Width < 6 || rect.Height < 6)
        {
            return;
        }

        dc.DrawRectangle(null, _dotted, Rect.Inflate(r, -1, -1));
    }

    /// <summary>
    /// Escribe si cabe, y devuelve cuánto ocupó (0 si no escribió nada). El texto se mide antes:
    /// una etiqueta recortada es ruido, no información.
    /// </summary>
    private double DrawText(
        DrawingContext dc,
        string text,
        TreemapRect rect,
        Brush ink,
        double size,
        bool bold,
        bool muted = false,
        bool onlyIfItFits = false)
    {
        double room = rect.Width - 2 * LabelPadX;

        // Se descarta ANTES de medir: en un clon real casi todas las celdas son demasiado
        // pequeñas para una etiqueta, y construir un FormattedText para cada una de las 900 que
        // se van a descartar es la mitad del coste de la pasada. La altura mínima de una línea de
        // `size` puntos es `size` * 1,3 largo; con menos que eso no cabe nada.
        if (string.IsNullOrEmpty(text) || room < size || rect.Height < size * 1.3 + 2 * LabelPadY)
        {
            return 0;
        }

        var formatted = new FormattedText(
            text,
            AppCulture.Display,
            FlowDirection.LeftToRight,
            bold ? Bold : Regular,
            size,
            muted ? MutedBrush : ink,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (formatted.Height + 2 * LabelPadY > rect.Height || (onlyIfItFits && formatted.Width > room))
        {
            return 0;
        }

        formatted.MaxTextWidth = Math.Max(1, room);
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(formatted, new Point(rect.X + LabelPadX, rect.Y + (rect.Height - formatted.Height) / 2));
        return Math.Min(formatted.Width, room) + LabelPadX;
    }

    /// <summary>
    /// Tinta que se lee sobre ese relleno. Se decide por la luminancia del color y no por el tema:
    /// el paso 5 es oscuro en tema claro y claro en tema oscuro, así que un color de texto fijo
    /// dejaría ilegible justo la celda más importante del mapa.
    /// </summary>
    private Brush Contrast(Brush? fill)
    {
        if (fill is not SolidColorBrush solid)
        {
            return LabelBrush;
        }

        Color c = solid.Color;
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        return luminance < 0.55 ? Brushes.White : Brushes.Black;
    }

    /// <summary>La misma tinta, a media voz. Para lo que acompaña sin competir.</summary>
    private static Brush Faded(Brush ink) => Fade(ink, 0xB4);

    private static Brush Fade(Brush brush, byte alpha)
    {
        if (brush is not SolidColorBrush solid)
        {
            return brush;
        }

        Color c = solid.Color;
        var faded = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        faded.Freeze();
        return faded;
    }

    private static TreemapRect Deflate(TreemapRect rect, double by)
        => new(rect.X + by, rect.Y + by, Math.Max(0, rect.Width - 2 * by), Math.Max(0, rect.Height - 2 * by));

    // ---------- Impacto: una pasada por la lista, y solo cuando el ratón se mueve ----------

    private Hit? At(Point p)
    {
        // Al revés: las celdas se añadieron después que su banda, así que la última que contiene
        // el punto es la más específica.
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            TreemapRect r = _hits[i].Rect;
            if (p.X >= r.X && p.X < r.Right && p.Y >= r.Y && p.Y < r.Bottom)
            {
                return _hits[i];
            }
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Hit? hit = At(e.GetPosition(this));
        if (ReferenceEquals(hit, _hover))
        {
            return;
        }

        _hover = hit;
        ToolTip = hit?.Tooltip;
        Cursor = hit is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        ToolTip = null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // Doble clic: el segundo «arriba» no debe repetir el gesto simple. La activación va por
        // OnMouseDoubleClick, que llega antes con ClickCount = 2.
        if (e.ClickCount > 1)
        {
            return;
        }

        Invoke(TileCommand, At(e.GetPosition(this)));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            e.Handled = true;
            Invoke(ActivateCommand, At(e.GetPosition(this)));
        }
    }

    private static void Invoke(ICommand? command, Hit? hit)
    {
        if (command is null || hit?.Payload is not { } payload || !command.CanExecute(payload))
        {
            return;
        }

        command.Execute(payload);
    }
}
