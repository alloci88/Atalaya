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
/// <param name="Ink">
/// Con qué se escribe encima. Sale del paso de la escala (<c>HeatStep.InkFor</c>), no de una
/// fórmula de luminancia: la fórmula daba blanco sobre el coral del paso 4, donde lo legible es
/// el negro. <c>null</c> —celda gris— usa la tinta del tema.
/// </param>
/// <param name="ShortLabel">
/// Cómo se llama esta celda cuando el nombre largo no cabe. No es un recorte —eso lo hace el
/// control midiendo— sino <b>otra forma de decir lo mismo</b>: «+60 unidades» acortado por el
/// medio da «+60 u…ades», que no es más corto, es peor. Null cuando no hay forma breve.
/// </param>
public sealed record HeatCell(
    string Label,
    double Weight,
    Brush? Fill,
    Brush? Ink,
    bool Qualified,
    string Tooltip,
    object? Payload,
    string? ShortLabel = null);

/// <summary>Un grupo del mapa: un módulo, con su banda de cabecera y sus celdas.</summary>
public sealed record HeatGroup(
    string Name,
    string Detail,
    double Weight,
    Brush? Fill,
    Brush? Ink,
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

    /// <summary>
    /// Por debajo de esta área una celda no es una celda: es un punto con borde. Se funden en una
    /// sola («+N unidades») en vez de dibujar cuarenta ilegibles — el equivalente honesto de lo
    /// que ya hace la leyenda de las gráficas con «Otras».
    /// </summary>
    private const double MinCellArea = 90;

    /// <summary>Y por debajo de este lado, tampoco: 40x2 px tiene área de sobra y no se ve.</summary>
    private const double MinCellSide = 7;

    /// <summary>Agrupar dos celdas no ahorra nada; a partir de tres empieza a limpiar.</summary>
    private const int MinClusterSize = 3;

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

    public static readonly DependencyProperty ClusterFactoryProperty = DependencyProperty.Register(
        nameof(ClusterFactory), typeof(Func<IReadOnlyList<HeatCell>, HeatCell>), typeof(Treemap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

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

    /// <summary>
    /// Cómo se resume un puñado de celdas demasiado pequeñas para dibujarse (F10.1 §2). El control
    /// decide <b>cuáles</b> —es el único que conoce la geometría— y quien la pone decide <b>qué
    /// significa</b> la celda que las sustituye: su color, su texto y su tooltip. Sin fábrica no
    /// se agrupa nada y se dibujan todas, por diminutas que salgan.
    /// </summary>
    public Func<IReadOnlyList<HeatCell>, HeatCell>? ClusterFactory
    {
        get => (Func<IReadOnlyList<HeatCell>, HeatCell>?)GetValue(ClusterFactoryProperty);
        set => SetValue(ClusterFactoryProperty, value);
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

        DrawHeader(dc, group, band);

        if (inner.Height <= 1)
        {
            return;
        }

        foreach (TreemapTile<HeatCell> tile in TreemapLayout.Squarify(Cells(group, inner), c => c.Weight, inner))
        {
            TreemapRect cell = Deflate(tile.Rect, CellGap / 2);
            if (cell.Width <= 0.5 || cell.Height <= 0.5)
            {
                continue;
            }

            Fill(dc, cell, tile.Item.Fill, pen, tile.Item.Qualified);
            _hits.Add(new Hit(cell, tile.Item.Tooltip, tile.Item.Payload, IsGroup: false));

            // Una celda con forma breve NO se recorta: o cabe su nombre entero, o se escribe la
            // forma corta. «+60 u…ades» tiene los mismos caracteres que «+60 unidades» menos tres
            // y dice bastante menos que «+60»: recortar ahí no ahorra sitio, estropea el texto.
            Brush ink = Ink(tile.Item.Ink);
            if (tile.Item.ShortLabel is { Length: > 0 } brief)
            {
                if (!DrawLabel(dc, tile.Item.Label, cell, ink, 11, bold: false, retention: 1))
                {
                    DrawLabel(dc, brief, cell, ink, 11, bold: false, retention: 1);
                }
            }
            else
            {
                DrawLabel(dc, tile.Item.Label, cell, ink, 11, bold: false);
            }
        }
    }

    /// <summary>
    /// La cabecera del módulo. <b>El nombre es lo último que cae</b>: primero se retira el detalle
    /// («598 u · 0 % auditado») y solo después el nombre se acorta por el medio. Un módulo cuya
    /// banda dice «0 % auditado» pero no dice de quién no sirve para nada.
    /// </summary>
    private void DrawHeader(DrawingContext dc, HeatGroup group, TreemapRect band)
    {
        if (band.Width <= 2 * LabelPadX || band.Height <= 2 * LabelPadY)
        {
            return;
        }

        Brush ink = Ink(group.Ink);
        double room = band.Width - 2 * LabelPadX;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // ¿Cabe nombre + detalle? Se mide la pareja entera antes de decidir; si no cabe, el
        // detalle no se dibuja y el nombre se queda con todo el ancho.
        const double gap = 8;
        double nameWidth = TextFit.Width(group.Name, Bold, 12, dpi);
        bool withDetail = group.Detail.Length > 0
                          && nameWidth + gap + TextFit.Width(group.Detail, Regular, 11, dpi) <= room;

        double nameRoom = withDetail ? nameWidth : room;

        // Antes de mutilar el nombre se prueba un cuerpo más pequeño: «XBLASTQuickUtils» entero a
        // 10,5 px se lee mucho mejor que «XBLASTQ…kUtils» a 12. Solo cuando tampoco así cabe se
        // recorta —con retención 0, porque el nombre de un módulo no puede desaparecer—.
        double headerSize = 12;
        string? name = null;
        foreach (double size in new[] { 12, 11, 10.5 })
        {
            if (band.Height >= size * 1.3 && TextFit.Width(group.Name, Bold, size, dpi) <= nameRoom)
            {
                headerSize = size;
                name = group.Name;
                break;
            }
        }

        name ??= TextFit.Fit(group.Name, Bold, headerSize, dpi, nameRoom, retention: 0);
        if (name is null)
        {
            return;
        }

        double used = Write(dc, name, band, ink, headerSize, Bold, dpi);
        if (withDetail)
        {
            // El detalle va en la MISMA tinta atenuada, no en el gris del tema: sobre la banda
            // clara de un módulo del paso 5, un gris fijo se vuelve ilegible justo en el módulo
            // que más importa leer.
            var rest = new TreemapRect(
                band.X + used + gap, band.Y, band.Width - used - gap, band.Height);
            DrawLabel(dc, group.Detail, rest, Faded(ink), 11, bold: false, retention: 0);
        }
    }

    /// <summary>
    /// Escribe la etiqueta de una celda si cabe entera; si no, acortada por el medio mientras siga
    /// identificando algo; y si tampoco, <b>nada</b>. Media palabra no informa: ensucia la celda de
    /// al lado y el tooltip ya dice el nombre entero.
    /// </summary>
    /// <returns>true si llegó a escribir algo.</returns>
    private bool DrawLabel(
        DrawingContext dc,
        string text,
        TreemapRect rect,
        Brush ink,
        double size,
        bool bold,
        double retention = TextFit.DefaultRetention)
    {
        double room = rect.Width - 2 * LabelPadX;
        if (string.IsNullOrEmpty(text) || room <= 0 || rect.Height < size * 1.3 + 2 * LabelPadY)
        {
            return false;
        }

        Typeface face = bold ? Bold : Regular;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (TextFit.Fit(text, face, size, dpi, room, retention) is not { } fitted)
        {
            return false;
        }

        Write(dc, fitted, rect, ink, size, face, dpi);
        return true;
    }

    /// <summary>Pinta un texto YA encajado y devuelve lo que ocupó.</summary>
    private static double Write(
        DrawingContext dc, string text, TreemapRect rect, Brush ink, double size, Typeface face, double dpi)
    {
        FormattedText formatted = TextFit.Format(text, face, size, dpi, ink);
        dc.DrawText(
            formatted,
            new Point(rect.X + LabelPadX, rect.Y + (rect.Height - formatted.Height) / 2));
        return formatted.Width + LabelPadX;
    }

    /// <summary>
    /// Las celdas que se van a dibujar: las del módulo, con la cola de las que no llegarían a
    /// verse fundida en una sola (F10.1 §2). El umbral se calcula sobre la escala REAL del hueco,
    /// así que la misma unidad se agrupa en la vista completa y se dibuja al ampliar el módulo,
    /// que es justo la razón de que el agregado sea clicable.
    /// </summary>
    private IReadOnlyList<HeatCell> Cells(HeatGroup group, TreemapRect inner)
    {
        var cells = group.Cells;
        if (ClusterFactory is not { } factory || cells.Count < MinClusterSize)
        {
            return cells;
        }

        double total = cells.Sum(c => c.Weight);
        if (total <= 0 || inner.Area <= 0)
        {
            return cells;
        }

        double scale = inner.Area / total;
        double cut = Math.Max(MinCellArea, MinCellSide * MinCellSide);
        var tiny = cells.Where(c => c.Weight * scale < cut).ToList();
        if (tiny.Count < MinClusterSize)
        {
            return cells;
        }

        var kept = cells.Where(c => c.Weight * scale >= cut).ToList();
        kept.Add(factory(tiny));
        return kept;
    }

    /// <summary>La tinta de una celda, o la del tema cuando la celda no la trae (celda gris).</summary>
    private Brush Ink(Brush? ink) => ink ?? LabelBrush;

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
