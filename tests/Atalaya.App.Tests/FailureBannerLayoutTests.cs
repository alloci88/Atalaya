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
/// <para>
/// El medidor —cargar el XAML, medir a un tamaño y mirar los rectángulos— vive en
/// <see cref="ViewLayout"/> desde F16-RETOQUE, porque la cabecera hace las mismas preguntas. Dos
/// copias de un medidor acaban midiendo distinto.
/// </para>
/// </summary>
public sealed class FailureBannerLayoutTests
{
    private static string Xaml(string view) => ViewLayout.Xaml(view);

    private static Grid LoadRoot(string view) => ViewLayout.LoadRoot(view);

    private static void Layout(FrameworkElement root, double width, double height)
        => ViewLayout.Layout(root, width, height);

    private static Rect BoxOf(FrameworkElement child, Visual root) => ViewLayout.BoxOf(child, root);

    private static List<FrameworkElement> InRow(Grid root, int row) => ViewLayout.InRow(root, row);

    private static IEnumerable<T> Descendants<T>(DependencyObject node)
        where T : DependencyObject => ViewLayout.Descendants<T>(node);

    private static void OnUiThread(Action action) => ViewLayout.OnUiThread(action);

    /// <summary>Las dos vistas que enseñan un fallo del proveedor.</summary>
    public static TheoryData<string> Views => new() { "SessionView.xaml", "AssistedFixView.xaml" };

    // ================================================================ la estructura declarada

    /// <summary>
    /// La regla que se rompió, dicha en una línea: nadie comparte fila con nadie. Dos hijos en la
    /// misma fila de un <c>Grid</c> no reparten espacio — se superponen.
    /// </summary>
    /// <remarks>
    /// Se pregunta a la rejilla RAÍZ cargada, no al texto del fichero: desde R4 §5 el pie del
    /// arreglo asistido vive dentro de la rejilla del cuerpo —para compartir sus carriles con los
    /// dos paneles— y tiene allí su propia fila 1, que no es ésta. Contar `Grid.Row="1"` en el
    /// XAML entero contaba filas de otras rejillas y decía que había una superposición que no
    /// existe.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Views))]
    public void Ningun_elemento_comparte_fila_con_el_banner_de_fallo(string view)
        => OnUiThread(() =>
        {
            List<FrameworkElement> inRow = InRow(LoadRoot(view), 1);

            inRow.Should().ContainSingle(
                "dos elementos en la misma fila se pintan uno encima del otro, y gana el que se "
                + "declara después: un aviso que hay que leer no puede depender de eso");
        });

    [Theory]
    [InlineData("SessionView.xaml", 4)]
    [InlineData("AssistedFixView.xaml", 3)]
    public void El_grid_declara_una_fila_por_cada_bloque(string view, int rows)
        => OnUiThread(() => LoadRoot(view).RowDefinitions.Count.Should().Be(rows,
            "cabecera, banner y cuerpo son bloques de la raíz en las dos; el pie lo es solo en "
            + "Sesión en vivo, porque en Arreglo asistido se ha mudado dentro del cuerpo para "
            + "medir exactamente lo que miden los dos paneles juntos (R4 §5)"));

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

        // Desde D-983 la tarjeta es la del SISTEMA y no la de WPF-UI: `Card` sale de nuestra
        // paleta —la que tiene sus pares medidos a AA en los dos temas— mientras que los pinceles
        // de la librería son los suyos. La regla es la misma y ahora se cumple mejor: el fondo lo
        // pone el tema, no un color escrito en la vista.
        xaml.Should().Contain("Style=\"{StaticResource Card}\"",
            "la pantalla de cierre usa la tarjeta del sistema, que cambia con el tema");
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

}
