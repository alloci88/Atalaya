using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// V4 — la ficha de hallazgo (F5.5).
/// <para>
/// Lo único que hace este código detrás es el <b>colapso a una columna</b>. La ficha se dibuja en
/// dos columnas —principal al 60 %, lateral al 40 % con su propio scroll— y por debajo de
/// <see cref="TwoColumnBreakpoint"/> píxeles la lateral no cabe sin estrangular a la principal, así
/// que baja al final de la primera <b>conservando el orden</b>: cabecera, texto, código, historial,
/// comentarios y luego metadatos, acciones y gobernanza.
/// </para>
/// <para>
/// Se mueve el panel de sitio en vez de duplicarlo en el XAML con dos visibilidades: dos árboles
/// para el mismo contenido es la manera segura de que uno de los dos se quede sin arreglar la
/// próxima vez que alguien toque la gobernanza.
/// </para>
/// </summary>
public partial class FindingDetailView : UserControl
{
    /// <summary>
    /// El ancho de página por debajo del cual se pasa a una columna. Con el rail de navegación
    /// (210 px) y el relleno de la página (40), son ventanas de ~1110 px o menos: la lateral
    /// necesita 320 px para que la gobernanza no se recorte, y por debajo de este ancho lo que
    /// quedaría para la columna principal ya no da para leer código.
    /// </summary>
    public const double TwoColumnBreakpoint = 860;

    /// <summary>
    /// Lo que el rail de navegación y el relleno de la página se llevan del ancho de la ventana
    /// (210 + 20 + 20), para poder razonar sobre anchos de VENTANA y no de página.
    /// </summary>
    public const double ShellChrome = 250;

    public FindingDetailView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// ¿Caben dos columnas en esta anchura de página? Es una decisión de maquetado con un número
    /// detrás, así que vive aparte de la ventana para poder fijarla en un test.
    /// </summary>
    public static bool FitsTwoColumns(double pageWidth) => pageWidth >= TwoColumnBreakpoint;

    /// <summary>La misma pregunta, hecha sobre el ancho de la ventana entera.</summary>
    public static bool FitsTwoColumnsInWindow(double windowWidth)
        => FitsTwoColumns(windowWidth - ShellChrome);

    /// <summary>¿Está la ficha en dos columnas ahora mismo? Lo consultan los tests de layout.</summary>
    public bool IsTwoColumn => SideScroll.Visibility == Visibility.Visible;

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyLayout(e.NewSize.Width);

    /// <summary>Reparte el contenido entre una o dos columnas según el ancho disponible.</summary>
    public void ApplyLayout(double width)
    {
        bool wide = FitsTwoColumns(width);
        if (wide == IsTwoColumn && SideStack.Parent is not null)
        {
            return;
        }

        if (wide)
        {
            MainStack.Children.Remove(SideStack);
            SideScroll.Content = SideStack;
            SideScroll.Visibility = Visibility.Visible;
            GutterColumn.Width = new GridLength(18);
            SideColumn.Width = new GridLength(2, GridUnitType.Star);
            SideColumn.MinWidth = 320;
        }
        else
        {
            SideScroll.Content = null;
            SideScroll.Visibility = Visibility.Collapsed;
            if (!MainStack.Children.Contains(SideStack))
            {
                MainStack.Children.Add(SideStack);
            }

            GutterColumn.Width = new GridLength(0);
            SideColumn.MinWidth = 0;
            SideColumn.Width = new GridLength(0);
        }
    }
}
