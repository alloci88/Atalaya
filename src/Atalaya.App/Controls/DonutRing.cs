using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Atalaya.App.Controls;

/// <summary>
/// Un tramo del rosco: cuánto vale, de qué color va y cómo se llama en el tooltip.
/// </summary>
/// <param name="Tooltip">
/// El texto exacto del tooltip. Null deja el que el rosco compone solo («Auditadas: 4 de 10
/// (40 %)»). Existe porque no todos los roscos hablan de lo mismo: el de severidad dice
/// «Alta — 4 hallazgos (33 %)», y forzar una frase única para los dos habría dejado a uno de
/// los dos diciendo una rareza (F6.5).
/// </param>
/// <param name="Payload">
/// Lo que se entrega al comando cuando se pulsa este tramo. El rosco no sabe qué es —una
/// severidad, un estado, lo que sea—: solo lo devuelve. Sin esto, «clic en el tramo rojo»
/// habría que resolverlo por el nombre del tramo, que es texto de presentación.
/// </param>
public sealed record DonutSegment(
    string Name,
    double Value,
    Brush Brush,
    string? Tooltip = null,
    object? Payload = null);

/// <summary>
/// El rosco del panel de métricas. Solo dibuja el ANILLO: el número del centro y el nombre de
/// debajo son texto de la vista, que así hereda tipografía y color del tema sin que este control
/// tenga que saber nada de temas.
/// <para>
/// Lo usan DOS filas con reglas de color opuestas, y las dos tienen razón. La de <b>cobertura</b>
/// (F5.9 §3) va con el color de la app y dos neutros y NUNCA con severidades: un rosco de
/// cobertura no habla de gravedad, y pintar «pendiente» de rojo diría que lo pendiente es
/// crítico. La de <b>severidad</b> (F6.5) va justo con esos cuatro colores reservados, porque
/// ahí la paleta semántica ES el dato. Por eso el color lo pone siempre quien llama: el control
/// no elige, y así no puede equivocarse en ninguna de las dos.
/// </para>
/// </summary>
public sealed class DonutRing : Canvas
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<DonutSegment>), typeof(DonutRing),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(DonutRing),
        new PropertyMetadata(14.0, OnVisualChanged));

    public IReadOnlyList<DonutSegment>? Segments
    {
        get => (IReadOnlyList<DonutSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public static readonly DependencyProperty SegmentGapProperty = DependencyProperty.Register(
        nameof(SegmentGap), typeof(double), typeof(DonutRing),
        new PropertyMetadata(0.0, OnVisualChanged));

    public static readonly DependencyProperty EmptyBrushProperty = DependencyProperty.Register(
        nameof(EmptyBrush), typeof(Brush), typeof(DonutRing),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty SegmentCommandProperty = DependencyProperty.Register(
        nameof(SegmentCommand), typeof(ICommand), typeof(DonutRing),
        new PropertyMetadata(null, OnVisualChanged));

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    /// <summary>
    /// Grados de aire entre tramos. Cero —el valor por defecto— deja el anillo continuo, que es
    /// como nació la fila de cobertura y como se queda. El aire se pide cuando los tramos son
    /// muchos y de colores parecidos, que es el caso de las cuatro severidades.
    /// </summary>
    public double SegmentGap
    {
        get => (double)GetValue(SegmentGapProperty);
        set => SetValue(SegmentGapProperty, value);
    }

    /// <summary>
    /// Con qué se dibuja el anillo cuando no hay NADA que repartir. Null (por defecto) no dibuja
    /// nada; con pincel, se pinta el aro entero apagado — que es como se dice «esta aplicación
    /// está limpia» sin quitarla de la fila.
    /// </summary>
    public Brush? EmptyBrush
    {
        get => (Brush?)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    /// <summary>
    /// Qué se ejecuta al pulsar un tramo, con su <see cref="DonutSegment.Payload"/> de parámetro.
    /// Null deja el anillo inerte: el rosco de cobertura entero ya es un botón, y dos gestos
    /// distintos sobre la misma figura serían uno de más.
    /// </summary>
    public ICommand? SegmentCommand
    {
        get => (ICommand?)GetValue(SegmentCommandProperty);
        set => SetValue(SegmentCommandProperty, value);
    }

    public DonutRing() => SizeChanged += (_, _) => Rebuild();

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DonutRing)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();

        var segments = (Segments ?? Array.Empty<DonutSegment>()).Where(s => s.Value > 0).ToList();
        double size = Math.Min(ActualWidth, ActualHeight);
        double total = segments.Sum(s => s.Value);
        if (size <= 0)
        {
            return;
        }

        double thickness = Math.Min(RingThickness, size / 3);
        double radius = (size - thickness) / 2;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);

        if (total <= 0)
        {
            DrawEmpty(centre, radius, thickness);
            return;
        }

        // Un solo tramo al 100 %: el arco degenera (empieza y acaba en el mismo punto y WPF no
        // dibuja nada). Se pinta el circulo entero, que es lo que ese caso significa.
        if (segments.Count == 1)
        {
            DonutSegment only = segments[0];
            var ring = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = only.Brush,
                StrokeThickness = thickness,
                ToolTip = only.Tooltip ?? Tip(only, total),
            };

            if (SegmentCommand is { } command)
            {
                ring.Cursor = Cursors.Hand;
                ring.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (command.CanExecute(only.Payload))
                    {
                        command.Execute(only.Payload);
                    }
                };
            }

            SetLeft(ring, centre.X - radius);
            SetTop(ring, centre.Y - radius);
            Children.Add(ring);
            return;
        }

        // El aire se descuenta del tramo, no se añade: la vuelta tiene que seguir sumando 360°
        // o el último tramo acabaría desplazado y el anillo no cerraría.
        double gap = segments.Count > 1 ? Math.Max(0, SegmentGap) : 0;
        double angle = -90;
        foreach (DonutSegment segment in segments)
        {
            double share = 360 * segment.Value / total;
            // Un tramo minúsculo no puede quedarse en nada por culpa del aire: se le deja al
            // menos un grado, que es lo que hace que «1 crítica de 400» siga viéndose.
            double sweep = Math.Max(1, share - gap);
            Children.Add(Arc(centre, radius, thickness, angle, sweep, segment, total));
            angle += share;
        }
    }

    /// <summary>
    /// El anillo de una aplicación sin nada que repartir. Se dibuja apagado y con su tooltip: una
    /// app limpia tiene que VERSE limpia, no desaparecer de la fila (F6.5).
    /// </summary>
    private void DrawEmpty(Point centre, double radius, double thickness)
    {
        if (EmptyBrush is not { } brush)
        {
            return;
        }

        var ring = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = brush,
            StrokeThickness = thickness,
        };
        SetLeft(ring, centre.X - radius);
        SetTop(ring, centre.Y - radius);
        Children.Add(ring);
    }

    private Path Arc(
        Point centre, double radius, double thickness, double startAngle, double sweep,
        DonutSegment segment, double total)
    {
        Point from = OnCircle(centre, radius, startAngle);
        Point to = OnCircle(centre, radius, startAngle + sweep);

        var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(
            to, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, isStroked: true));

        var path = new Path
        {
            Data = new PathGeometry(new[] { figure }),
            Stroke = segment.Brush,
            StrokeThickness = thickness,
            // El trazo es una línea de 14 px: sin esto solo respondería el borde exacto del arco.
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            ToolTip = segment.Tooltip ?? Tip(segment, total),
        };
        ToolTipService.SetInitialShowDelay(path, 120);

        if (SegmentCommand is { } command)
        {
            path.Cursor = Cursors.Hand;
            path.MouseLeftButtonUp += (_, e) =>
            {
                // Marcado como atendido: si no, el clic seguiría subiendo hasta el botón que
                // envuelve el rosco entero y se dispararían los DOS gestos.
                e.Handled = true;
                if (command.CanExecute(segment.Payload))
                {
                    command.Execute(segment.Payload);
                }
            };
        }

        return path;
    }

    private static Point OnCircle(Point centre, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(centre.X + radius * Math.Cos(rad), centre.Y + radius * Math.Sin(rad));
    }

    /// <summary>El tooltip dice el tramo, cuántas unidades y qué parte del total son.</summary>
    private static string Tip(DonutSegment segment, double total)
        => $"{segment.Name}: {segment.Value:0} de {total:0} ({segment.Value / total:0%})";
}
