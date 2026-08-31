using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-CUOTA §2 — el aviso de error es un elemento DEL flujo, no una capa encima.
/// <para>
/// El banner vivía en <c>Grid.Row="1"</c>, la misma fila que el cuerpo de tres columnas. En un
/// <c>Grid</c> de WPF eso no reparte espacio: superpone, y gana el Z-order —o sea, el que se
/// declara después—, que era el cuerpo. El mensaje de error quedaba tapado por la cola de unidades
/// y la columna de actividad, y con la ventana pequeña era ilegible.
/// </para>
/// <para>
/// Aquí se mira el XAML de verdad: primero su estructura, y luego se CARGA y se MIDE a los tamaños
/// reales para comprobar que los rectángulos no se cruzan. Es la mitad que el compilador no vigila,
/// y es exactamente por donde se coló el fallo.
/// </para>
/// </summary>
public sealed class FailureBannerLayoutTests
{
    private static string Xaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Atalaya.App", "Views", "SessionView.xaml"));
    }

    // ================================================================ la estructura declarada

    /// <summary>
    /// La regla que se rompió, dicha en una línea: nadie comparte fila con nadie. Dos hijos en la
    /// misma fila de un <c>Grid</c> no reparten espacio — se superponen.
    /// </summary>
    [Fact]
    public void Ningun_elemento_comparte_fila_con_el_banner_de_fallo()
    {
        var rows = Regex.Matches(Xaml(), @"Grid\.Row=""(\d)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        rows.Should().OnlyHaveUniqueItems(
            "dos elementos en la misma fila se pintan uno encima del otro, y gana el que se "
            + "declara después: un aviso que hay que leer no puede depender de eso");
        rows.Should().Contain("1", "y el banner ocupa la suya");
    }

    [Fact]
    public void El_grid_declara_una_fila_por_cada_elemento()
        => Regex.Matches(Xaml(), @"<RowDefinition\b").Count.Should().Be(4,
            "cabecera, banner, cuerpo y pie: cuatro elementos, cuatro filas");

    /// <summary>
    /// Sin <c>VerticalAlignment="Top"</c>: era el parche con el que la superposición «casi»
    /// funcionaba. En su fila propia el banner mide lo que ocupa y empuja al cuerpo hacia abajo.
    /// </summary>
    [Fact]
    public void El_banner_ya_no_se_ancla_arriba_para_disimular_la_superposicion()
        => Xaml().Should().NotContain("Grid.Row=\"1\" VerticalAlignment=\"Top\"");

    [Fact]
    public void El_texto_del_error_se_puede_leer_seleccionar_y_copiar()
    {
        string xaml = Xaml();

        xaml.Should().Contain("IsReadOnly=\"True\"",
            "un TextBox de solo lectura se selecciona con el ratón; un TextBlock no");
        xaml.Should().Contain("CopyFailureCommand", "y hay botón, porque nadie selecciona a mano");
        xaml.Should().Contain("ToggleFailureDetailCommand", "el crudo se despliega, no desborda");
        xaml.Should().Contain("MaxHeight=\"120\"", "y acotado: un error largo no se come la vista");
        Regex.Matches(xaml, @"TextWrapping=""Wrap""").Count.Should().BeGreaterThanOrEqualTo(2,
            "envuelven el mensaje y el crudo, los dos");
    }

    // ================================================================ y la pantalla de cierre

    /// <summary>
    /// La segunda superposición, y la que se veía de verdad: la pantalla de cierre era un panel
    /// <c>#F2101010</c> puesto ENCIMA de las tres columnas, en sus mismas celdas. Al 95 % de
    /// opacidad traslucía la cola de unidades, la actividad y los hallazgos, y el texto del resumen
    /// —incluida la línea que explica por qué se cortó la sesión— chocaba con rutas y chips
    /// fantasma. Ahora las columnas se RETIRAN y la pantalla ocupa el hueco.
    /// </summary>
    [Fact]
    public void Las_tres_columnas_se_retiran_cuando_esta_la_pantalla_de_cierre()
    {
        string xaml = Xaml();
        const string hide =
            "Visibility=\"{Binding ShowSummary, Converter={StaticResource InverseBoolToVisibility}}\"";

        Regex.Matches(xaml, Regex.Escape(hide)).Count.Should().Be(3,
            "cola, actividad y hallazgos: las tres, o la que quede sigue traslucieńdose");
    }

    /// <summary>
    /// Y deja de ser negra a secas: con un fondo fijo casi negro, la pantalla de cierre era un
    /// agujero oscuro en el tema claro. Sigue el tema, como todo lo demás.
    /// </summary>
    [Fact]
    public void La_pantalla_de_cierre_sigue_el_tema_y_no_es_una_capa_translucida()
    {
        string xaml = Xaml();

        xaml.Should().NotContain("#F2101010", "un fondo casi negro fijo no vale para el tema claro");
        xaml.Should().Contain("Background=\"{DynamicResource CardBackgroundFillColorDefaultBrush}\"");
    }

    // ================================================================ y medido de verdad

    /// <summary>
    /// La prueba que no se discute: se carga la plantilla, se mide a los tamaños reales y se
    /// comprueba que los rectángulos no se cruzan. 1366×768 es el portátil de la casa; los otros
    /// dos son la ventana pequeña, que es donde el solape se veía peor.
    /// </summary>
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(900, 700)]
    [InlineData(700, 520)]
    public void Medido_a_tamano_real_el_banner_no_se_solapa_con_nada(double width, double height)
        => OnUiThread(() =>
        {
            Grid root = LoadRoot();
            var banner = (FrameworkElement)root.Children[1];
            var body = (FrameworkElement)root.Children[2];
            var footer = (FrameworkElement)root.Children[3];

            banner.Visibility = Visibility.Visible;
            Layout(root, width, height);

            Rect bannerBox = BoxOf(banner, root);
            Rect bodyBox = BoxOf(body, root);
            Rect footerBox = BoxOf(footer, root);

            bannerBox.Height.Should().BeGreaterThan(0, "el banner ocupa sitio de verdad");
            bannerBox.IntersectsWith(bodyBox).Should().BeFalse(
                $"a {width}×{height} el aviso no puede pisar el cuerpo de la sesión");
            bannerBox.IntersectsWith(footerBox).Should().BeFalse(
                $"a {width}×{height} el aviso no puede pisar el pie de coste");
            bodyBox.Top.Should().BeGreaterThanOrEqualTo(bannerBox.Bottom - 0.5,
                "el cuerpo EMPIEZA donde acaba el banner: eso es estar en el flujo");
        });

    /// <summary>Y sin fallo el banner no reserva ni un píxel: su fila es <c>Auto</c>.</summary>
    [Fact]
    public void Sin_fallo_el_banner_no_ocupa_espacio()
        => OnUiThread(() =>
        {
            Grid root = LoadRoot();
            ((FrameworkElement)root.Children[1]).Visibility = Visibility.Collapsed;
            Layout(root, 1366, 768);

            root.RowDefinitions[1].ActualHeight.Should().Be(0);
        });

    /// <summary>
    /// Y con un error CRUDO largo desplegado tampoco: el detalle está acotado y con su propio
    /// scroll, que es lo que impide que la longitud del error del proveedor decida el layout.
    /// </summary>
    [Fact]
    public void Un_error_largo_desplegado_no_empuja_el_cuerpo_fuera_de_la_ventana()
        => OnUiThread(() =>
        {
            Grid root = LoadRoot();
            var banner = (FrameworkElement)root.Children[1];
            var body = (FrameworkElement)root.Children[2];

            // El crudo desplegado y desmedido: cuarenta veces el error real del proveedor.
            foreach (ScrollViewer scroller in Descendants<ScrollViewer>(banner))
            {
                foreach (TextBox raw in Descendants<TextBox>(scroller))
                {
                    raw.Text = string.Join(' ', Enumerable.Repeat(
                        "Session error: You have exceeded your monthly quota (Request ID: FA81:2498A4)", 40));
                }
            }

            foreach (Border detail in Descendants<Border>(banner))
            {
                detail.Visibility = Visibility.Visible;
            }

            Layout(root, 900, 700);

            BoxOf(banner, root).Height.Should().BeLessThan(300,
                "el crudo vive en un ScrollViewer acotado: su longitud no puede decidir el layout");
            BoxOf(body, root).Height.Should().BeGreaterThan(100, "y al cuerpo le queda sitio de sobra");
            BoxOf(banner, root).IntersectsWith(BoxOf(body, root)).Should().BeFalse(
                "ni desplegado se cruza con nada");
        });

    // ================================================================ andamiaje

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static Rect BoxOf(FrameworkElement child, Visual root)
    {
        Point origin = child.TransformToAncestor(root).Transform(new Point(0, 0));
        return new Rect(origin, new Size(child.ActualWidth, child.ActualHeight));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is T hit)
            {
                yield return hit;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// WPF exige STA. Se levanta un hilo propio por test en vez de traerse un paquete nuevo: es
    /// andamiaje de seis líneas y no añade dependencias al proyecto de tests.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>
    /// Carga el <c>Grid</c> raíz sin arrastrar la aplicación entera: se quitan el envoltorio de
    /// <c>UserControl</c> y todo lo que solo existe con la app viva —bindings, comandos, converters
    /// y brochas del tema—. Ninguno de ellos cambia dónde cae un rectángulo, que es lo único que
    /// se mide aquí.
    /// </summary>
    private static Grid LoadRoot()
    {
        // XamlReader solo resuelve un xmlns si el ensamblado que lo declara YA está cargado, y en un
        // proceso de tests nadie lo ha tocado todavía. Basta con nombrar un tipo suyo.
        _ = typeof(Wpf.Ui.Controls.Button);

        string xaml = Xaml();
        int start = xaml.IndexOf("<Grid>", StringComparison.Ordinal);
        int end = xaml.LastIndexOf("</Grid>", StringComparison.Ordinal);
        string body = xaml[start..(end + "</Grid>".Length)];

        body = Regex.Replace(body, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        body = Regex.Replace(
            body,
            @"\s[\w:.]+=""\{(?:Binding|DynamicResource|StaticResource)[^""]*""",
            string.Empty);

        // Y los manejadores del code-behind, que aquí no existe.
        body = Regex.Replace(body, @"\s[\w:.]+=""On[A-Za-z0-9_]*""", string.Empty);
        // Los controles de WPF-UI se cargan de VERDAD —el proyecto de tests ya los tiene por la
        // referencia a la app—: sustituirlos por botones planos mediría un layout que no existe.
        const string open = "<Grid>";
        body = string.Concat(
            "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:ui=\"clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui\">",
            body.AsSpan(open.Length));

        return (Grid)XamlReader.Parse(body);
    }
}
