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
/// El banner vivía en <c>Grid.Row="1"</c>, la misma fila que el cuerpo. En un <c>Grid</c> de WPF
/// eso no reparte espacio: superpone, y gana el Z-order —o sea, el que se declara después—, que era
/// el cuerpo. El mensaje de error quedaba pintado sobre la cola de unidades y la actividad.
/// </para>
/// <para>
/// <b>Y estaba en las DOS vistas.</b> Se arregló primero en la sesión de auditoría, y el usuario
/// siguió viéndolo — porque el que tenía delante era el del arreglo asistido, idéntico. Por eso
/// cada comprobación estructural recorre <see cref="Views"/>: probar solo la que se arregló primero
/// es como no probar ninguna.
/// </para>
/// </summary>
public sealed class FailureBannerLayoutTests
{
    /// <summary>Las dos vistas que enseñan un fallo del proveedor.</summary>
    public static TheoryData<string> Views => new() { "SessionView.xaml", "AssistedFixView.xaml" };

    private static string Xaml(string view)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Atalaya.App", "Views", view));
    }

    // ================================================================ la estructura declarada

    /// <summary>
    /// La regla que se rompió, dicha en una línea: nadie comparte fila con nadie. Dos hijos en la
    /// misma fila de un <c>Grid</c> no reparten espacio — se superponen.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void Ningun_elemento_comparte_fila_con_el_banner_de_fallo(string view)
    {
        var rows = Regex.Matches(Xaml(view), @"Grid\.Row=""(\d)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        rows.Should().Contain("1", "el banner ocupa la fila 1");
        rows.Count(r => r == "1").Should().Be(1,
            "dos elementos en la misma fila se pintan uno encima del otro, y gana el que se "
            + "declara después: un aviso que hay que leer no puede depender de eso");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void El_grid_declara_una_fila_por_cada_bloque(string view)
        => Regex.Matches(Xaml(view), @"<RowDefinition\b").Count.Should().Be(4,
            "cabecera, banner, cuerpo y pie: cuatro bloques, cuatro filas");

    /// <summary>
    /// Sin <c>VerticalAlignment="Top"</c>: era el parche con el que la superposición «casi»
    /// funcionaba. En su fila propia el banner mide lo que ocupa y empuja al cuerpo hacia abajo.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void El_banner_ya_no_se_ancla_arriba_para_disimular_la_superposicion(string view)
        => Xaml(view).Should().NotContain("Grid.Row=\"1\" VerticalAlignment=\"Top\"");

    [Theory]
    [MemberData(nameof(Views))]
    public void El_texto_del_error_se_puede_leer_seleccionar_y_copiar(string view)
    {
        string xaml = Xaml(view);

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
    /// La segunda superposición, la que no se veía midiendo una copia desnuda del XAML: la pantalla
    /// de cierre era un panel casi negro al 95 % de opacidad puesto ENCIMA de las columnas, en sus
    /// mismas celdas. Ese 5 % traslucía la cola, la actividad y los hallazgos, y el texto del
    /// resumen chocaba con rutas y chips fantasma. Ahora las columnas se RETIRAN.
    /// </summary>
    [Theory]
    [InlineData("SessionView.xaml", "ShowSummary")]
    [InlineData("AssistedFixView.xaml", "ShowClosing")]
    public void Las_columnas_se_retiran_cuando_esta_la_pantalla_de_cierre(string view, string flag)
    {
        string hide =
            $"Visibility=\"{{Binding {flag}, Converter={{StaticResource InverseBoolToVisibility}}}}\"";

        Regex.Matches(Xaml(view), Regex.Escape(hide)).Count.Should().Be(3,
            "las tres piezas del cuerpo, o la que quede sigue transluciéndose bajo el resumen");
    }

    /// <summary>
    /// Y deja de ser negra a secas: con un fondo fijo casi negro, la pantalla de cierre era además
    /// un agujero oscuro en el tema claro. Sigue el tema, como todo lo demás.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void La_pantalla_de_cierre_sigue_el_tema_y_no_es_una_capa_translucida(string view)
    {
        string xaml = Xaml(view);

        xaml.Should().NotContain("#F2101010", "un fondo casi negro fijo no vale para el tema claro");
        xaml.Should().Contain("Background=\"{DynamicResource CardBackgroundFillColorDefaultBrush}\"");
    }

    // ================================================================ y medido de verdad

    /// <summary>
    /// La prueba que no se discute: se carga la plantilla, se mide a los tamaños reales y se
    /// comprueba que los rectángulos no se cruzan. Se prueban TODOS los elementos de la fila del
    /// cuerpo, no solo el primero: en el arreglo asistido ahí viven el estado vacío y la sesión.
    /// </summary>
    [Theory]
    [InlineData("SessionView.xaml", 1366, 768)]
    [InlineData("SessionView.xaml", 900, 700)]
    [InlineData("SessionView.xaml", 700, 520)]
    [InlineData("AssistedFixView.xaml", 1366, 768)]
    [InlineData("AssistedFixView.xaml", 900, 700)]
    [InlineData("AssistedFixView.xaml", 700, 520)]
    public void Medido_a_tamano_real_el_banner_no_se_solapa_con_nada(string view, double width, double height)
        => OnUiThread(() =>
        {
            Grid root = LoadRoot(view);
            FrameworkElement banner = BannerOf(root);
            var footer = (FrameworkElement)root.Children[^1];

            banner.Visibility = Visibility.Visible;
            foreach (FrameworkElement inBody in InRow(root, 2))
            {
                inBody.Visibility = Visibility.Visible;
            }

            Layout(root, width, height);

            BoxOf(banner, root).Height.Should().BeGreaterThan(0, "el banner ocupa sitio de verdad");
            BoxOf(banner, root).IntersectsWith(BoxOf(footer, root)).Should().BeFalse(
                $"{view} a {width}x{height}: el aviso no puede pisar el pie");

            foreach (FrameworkElement inBody in InRow(root, 2))
            {
                Rect box = BoxOf(inBody, root);
                BoxOf(banner, root).IntersectsWith(box).Should().BeFalse(
                    $"{view} a {width}x{height}: el aviso no puede pisar el cuerpo");
                box.Top.Should().BeGreaterThanOrEqualTo(BoxOf(banner, root).Bottom - 0.5,
                    "el cuerpo EMPIEZA donde acaba el banner: eso es estar en el flujo");
            }
        });

    /// <summary>Y sin fallo el banner no reserva ni un píxel: su fila es <c>Auto</c>.</summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void Sin_fallo_el_banner_no_ocupa_espacio(string view)
        => OnUiThread(() =>
        {
            Grid root = LoadRoot(view);
            BannerOf(root).Visibility = Visibility.Collapsed;
            Layout(root, 1366, 768);

            root.RowDefinitions[1].ActualHeight.Should().Be(0);
        });

    /// <summary>
    /// Y con un error CRUDO largo desplegado tampoco: el detalle está acotado y con su propio
    /// scroll, que es lo que impide que la longitud del error del proveedor decida el layout.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void Un_error_largo_desplegado_no_empuja_el_cuerpo_fuera_de_la_ventana(string view)
        => OnUiThread(() =>
        {
            Grid root = LoadRoot(view);
            FrameworkElement banner = BannerOf(root);
            banner.Visibility = Visibility.Visible;
            Layout(root, 900, 700);

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

            BoxOf(banner, root).Height.Should().BeLessThan(340,
                "el crudo vive en un ScrollViewer acotado: su longitud no puede decidir el layout");

            foreach (FrameworkElement inBody in InRow(root, 2))
            {
                inBody.Visibility = Visibility.Visible;
                Layout(root, 900, 700);
                BoxOf(banner, root).IntersectsWith(BoxOf(inBody, root)).Should().BeFalse(
                    "ni desplegado se cruza con nada");
            }
        });

    // ================================================================ andamiaje

    /// <summary>El aviso de fallo: el único hijo directo que vive en la fila 1.</summary>
    private static FrameworkElement BannerOf(Grid root) => InRow(root, 1).Single();

    private static List<FrameworkElement> InRow(Grid root, int row)
        => root.Children.OfType<FrameworkElement>().Where(c => Grid.GetRow(c) == row).ToList();

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
    /// y brochas del tema—.
    /// <para>
    /// <b>Lo que esto prueba y lo que NO.</b> Prueba la geometría de las filas, que es donde vivía
    /// el defecto. No prueba lo que se pinta ENCIMA por diseño —la pantalla de cierre lo hacía— ni
    /// los colores del tema: eso se ve montando la vista real, y de ahí salen las capturas.
    /// </para>
    /// </summary>
    private static Grid LoadRoot(string view)
    {
        // XamlReader solo resuelve un xmlns si el ensamblado que lo declara YA está cargado, y en un
        // proceso de tests nadie lo ha tocado todavía. Basta con nombrar un tipo suyo.
        _ = typeof(Wpf.Ui.Controls.Button);

        string xaml = Xaml(view);

        // El Grid RAÍZ, no el primero que aparezca: el arreglo asistido declara plantillas con
        // Grids dentro de <UserControl.Resources>, y quedarse con uno de ésos da markup a medias.
        int after = xaml.IndexOf("</UserControl.Resources>", StringComparison.Ordinal);
        int start = xaml.IndexOf("<Grid>", after < 0 ? 0 : after, StringComparison.Ordinal);
        int end = xaml.LastIndexOf("</Grid>", StringComparison.Ordinal);
        string body = xaml[start..(end + "</Grid>".Length)];

        body = Regex.Replace(body, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        // Los bloques de propiedad en sintaxis de elemento que llevan un MultiBinding dentro: sin
        // su converter no se pueden construir, y el converter vive en los recursos de la app.
        body = Regex.Replace(
            body,
            @"<([\w:.]+)>\s*<MultiBinding.*?</MultiBinding>\s*</\1>",
            string.Empty,
            RegexOptions.Singleline);

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
