using System.Windows;
using System.Windows.Media;

namespace Atalaya.App.Controls;

/// <summary>
/// Una entrada de la leyenda del mapa de calor. Siempre <b>color + texto</b>, nunca un color solo
/// (misma regla que la leyenda de las gráficas): quien no distinga dos tonos tiene que poder leer
/// cuál es cuál.
/// </summary>
/// <param name="Fill">
/// El relleno de la muestra. <c>null</c> es una entrada de <b>solo texto</b> —la nota de los pesos,
/// que no es un color— y no dibuja una muestra vacía, que se leería como un sexto paso.
/// </param>
/// <param name="Dotted">
/// Dibuja el contorno punteado en vez de un borde liso. Es la entrada que explica la marca de «el
/// relleno no lo cuenta todo».
/// </param>
public sealed record HeatLegendItem(string Label, Brush? Fill, bool Dotted = false, string? Tooltip = null)
{
    /// <summary>Hay muestra que dibujar. Falso en la nota de los pesos.</summary>
    public bool HasSwatch => Fill is not null;
}

/// <summary>
/// Los pinceles del mapa que no son un color plano.
/// </summary>
public static class HeatBrushes
{
    /// <summary>
    /// El gris <b>tramado</b> de «no auditada» (F10 §1). La trama no es decoración: es un canal
    /// distinto del color, así que sobrevive a una impresión en blanco y negro, a un proyector
    /// malo y a cualquier daltonismo. Un gris liso se podría confundir con el primer paso de la
    /// escala —que significa «limpio»— y eso es justo la confusión que esta vista no puede tener.
    /// </summary>
    public static Brush Hatch(Color background, Color stroke)
    {
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(new Point(0, 6), new Point(6, 0)));
        geometry.Children.Add(new LineGeometry(new Point(-1, 1), new Point(1, -1)));
        geometry.Children.Add(new LineGeometry(new Point(5, 7), new Point(7, 5)));

        var pen = new Pen(new SolidColorBrush(stroke), 1.1);
        pen.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(background), null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
        drawing.Children.Add(new GeometryDrawing(null, pen, geometry));

        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Un pincel sólido a partir de un hexadecimal, ya congelado.</summary>
    public static SolidColorBrush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
