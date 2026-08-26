using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Atalaya.App.Controls;

/// <summary>Un tramo del rosco: cuánto vale, de qué color va y cómo se llama en el tooltip.</summary>
public sealed record DonutSegment(string Name, double Value, Brush Brush);

/// <summary>
/// El rosco de cobertura (F5.9 §3, gráfica 2). Solo dibuja el ANILLO: el porcentaje del centro y
/// el nombre de debajo son texto de la vista, que así hereda tipografía y color del tema sin que
/// este control tenga que saber nada de temas.
/// <para>
/// Los tramos van con un color neutro y el de la app (nunca severidades): un rosco de cobertura
/// no habla de gravedad, y pintar «pendiente» de rojo diría que lo pendiente es critico.
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

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
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
        if (size <= 0 || total <= 0)
        {
            return;
        }

        double thickness = Math.Min(RingThickness, size / 3);
        double radius = (size - thickness) / 2;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);

        // Un solo tramo al 100 %: el arco degenera (empieza y acaba en el mismo punto y WPF no
        // dibuja nada). Se pinta el circulo entero, que es lo que ese caso significa.
        if (segments.Count == 1)
        {
            var ring = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = segments[0].Brush,
                StrokeThickness = thickness,
                ToolTip = Tip(segments[0], total),
            };
            SetLeft(ring, centre.X - radius);
            SetTop(ring, centre.Y - radius);
            Children.Add(ring);
            return;
        }

        double angle = -90;
        foreach (DonutSegment segment in segments)
        {
            double sweep = 360 * segment.Value / total;
            Children.Add(Arc(centre, radius, thickness, angle, sweep, segment, total));
            angle += sweep;
        }
    }

    private static Path Arc(
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
            ToolTip = Tip(segment, total),
        };
        ToolTipService.SetInitialShowDelay(path, 120);
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
