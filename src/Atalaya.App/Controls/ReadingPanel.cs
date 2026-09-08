using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// <b>La lectura y su carril</b> (F36 §1.3): dos hijos, al lado cuando hay ancho y uno debajo del
/// otro cuando no.
/// <para>
/// <b>Por qué un panel y no dos columnas de <see cref="Grid"/>.</b> Por lo mismo que
/// <see cref="ColumnsPanel"/> (D-970): el número de columnas depende del ancho DISPONIBLE, que solo
/// se conoce en el <c>Measure</c>. Un <c>Grid</c> de <c>* / Auto</c> no se pliega — a 1000 px
/// seguiría poniendo el carril al lado y le comería 300 px a la única columna que hay que poder
/// leer, y la medida de lectura de F27 es justamente lo que no se toca.
/// </para>
/// <para>
/// <b>La lectura no crece con la ventana.</b> Se para en <see cref="ReadWidth"/> aunque sobre
/// sitio: una línea de 1.600 px no se puede seguir con la vista, y este panel existe para
/// respetar eso, no para repartir. El sobrante se queda a la derecha del carril — la página
/// arranca en el margen, como todo lo demás (UI-0035).
/// </para>
/// </summary>
public sealed class ReadingPanel : Panel
{
    /// <summary>
    /// El ancho MÍNIMO que la columna de lectura tiene que poder tener para que el carril quepa a
    /// su lado. Ya no recorta la columna: la recortaba, y ése era el defecto.
    /// </summary>
    public static readonly DependencyProperty ReadWidthProperty =
        DependencyProperty.Register(
            nameof(ReadWidth), typeof(double), typeof(ReadingPanel),
            new FrameworkPropertyMetadata(
                ReportLayout.ReadWidth, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// Lo que mide el carril cuando va al lado. Fijo: es un índice, no contenido.
    /// </summary>
    public static readonly DependencyProperty RailWidthProperty =
        DependencyProperty.Register(
            nameof(RailWidth), typeof(double), typeof(ReadingPanel),
            new FrameworkPropertyMetadata(
                ReportLayout.RailWidth, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(ReadingPanel),
            new FrameworkPropertyMetadata(
                ReportLayout.Gap, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ReadWidth
    {
        get => (double)GetValue(ReadWidthProperty);
        set => SetValue(ReadWidthProperty, value);
    }

    public double RailWidth
    {
        get => (double)GetValue(RailWidthProperty);
        set => SetValue(RailWidthProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>
    /// ¿Cabe el carril al lado? Solo si queda sitio para la lectura ENTERA y el carril entero: un
    /// carril que solo cabe encogiendo la columna de texto no cabe.
    /// </summary>
    public bool SideBySide(double available)
        => !double.IsInfinity(available) && available >= ReadWidth + Gap + RailWidth;

    /// <summary>
    /// El carril, <b>si lo hay</b>. Un carril COLAPSADO no es un carril: el cuerpo se queda con el
    /// ancho entero en vez de dejarle 380 px a algo que no se pinta. Un informe con menos de cuatro
    /// tarjetas no tiene índice que aportar (F36-2b §1.3) y ésa es la forma de decirlo desde el
    /// marcado, con la visibilidad que ya estaba enlazada.
    /// </summary>
    private UIElement? Rail()
        => InternalChildren.Count > 1 && InternalChildren[1].Visibility != Visibility.Collapsed
            ? InternalChildren[1]
            : null;

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count == 0)
        {
            return new Size(0, 0);
        }

        UIElement body = InternalChildren[0];
        UIElement? rail = Rail();

        // SIN CARRIL NO HAY DOS COLUMNAS: el cuerpo se queda con el ancho entero. Sin esto, un
        // informe sin índice seguía cediendo 380 px + hueco a algo que no se pinta.
        bool side = rail is not null && SideBySide(available.Width);
        double width = double.IsInfinity(available.Width) ? ReadWidth : available.Width;
        double bodyWidth = side ? width - Gap - RailWidth : width;
        double railWidth = side ? RailWidth : width;

        body.Measure(new Size(bodyWidth, double.PositiveInfinity));
        rail?.Measure(new Size(railWidth, double.PositiveInfinity));

        double height = side
            ? Math.Max(body.DesiredSize.Height, rail?.DesiredSize.Height ?? 0)
            : body.DesiredSize.Height + (rail is null ? 0 : Gap + rail.DesiredSize.Height);

        double used = side ? bodyWidth + Gap + railWidth : Math.Max(bodyWidth, railWidth);
        return new Size(Math.Min(used, width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count == 0)
        {
            return final;
        }

        UIElement body = InternalChildren[0];
        UIElement? rail = Rail();

        bool side = rail is not null && SideBySide(final.Width);
        double bodyWidth = side ? final.Width - Gap - RailWidth : final.Width;

        if (side)
        {
            body.Arrange(new Rect(0, 0, bodyWidth, final.Height));
            rail?.Arrange(new Rect(bodyWidth + Gap, 0, RailWidth, final.Height));
            return final;
        }

        double top = body.DesiredSize.Height;
        body.Arrange(new Rect(0, 0, bodyWidth, top));
        rail?.Arrange(new Rect(0, top + Gap, bodyWidth, rail.DesiredSize.Height));
        return final;
    }
}
