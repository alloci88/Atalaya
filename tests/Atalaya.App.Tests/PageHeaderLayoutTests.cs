using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F16-RETOQUE §2 — LA CABECERA NO SE PISA A NINGÚN ANCHO.
/// <para>
/// <b>El defecto.</b> Las cabeceras eran un <c>Grid</c> sin columnas con dos <c>StackPanel</c>
/// dentro, el segundo con <c>HorizontalAlignment="Right"</c>. Eso no reparte espacio: <b>superpone</b>
/// — la misma lección de D-710b, en el otro eje. Mientras la izquierda fue corta no se notó; con el
/// distintivo de proveedor y modelo de F16, «Pausar» se pintó encima de «Volver al hallazgo
/// (MEJ-0011)».
/// </para>
/// <para>
/// Se comprueba en las DOS vistas que usan el patrón, por lo mismo que el banner de fallo: arreglar
/// solo la que se reportó es como no arreglar ninguna.
/// </para>
/// </summary>
public sealed class PageHeaderLayoutTests
{
    /// <summary>Las vistas con cabecera de identidad + acciones.</summary>
    public static TheoryData<string> Views => new() { "SessionView.xaml", "AssistedFixView.xaml" };

    /// <summary>
    /// Los anchos <b>del área de página</b>, que no son los de la ventana: el menú lateral se lleva
    /// 210 px y los márgenes otros 32. Así que 1366 —el portátil de la casa— deja 1124, y 900 —el
    /// mínimo que la ventana admite, <c>MinWidth</c>— deja 658. Ésos son los dos extremos que un
    /// usuario puede alcanzar de verdad; medir a anchos imposibles sería medir una fantasía.
    /// </summary>
    public static TheoryData<string, double> Widths => new()
    {
        { "SessionView.xaml", 1124 },
        { "SessionView.xaml", 658 },
        { "AssistedFixView.xaml", 1124 },
        { "AssistedFixView.xaml", 658 },
    };

    /// <summary>
    /// Y además un ancho por debajo de lo posible, donde el reparto de columnas tiene que aguantar
    /// igual aunque no quepa nada: la garantía de que no se superpone no depende de que haya sitio.
    /// </summary>
    public static TheoryData<string, double> StressWidths => new()
    {
        { "SessionView.xaml", 400 },
        { "AssistedFixView.xaml", 400 },
    };

    // ================================================================ la estructura declarada

    /// <summary>
    /// La regla, dicha en una línea: la cabecera es un <see cref="PageHeader"/> y no un
    /// <c>Grid</c> con dos paneles superpuestos. Y el reparto no se re-declara en la vista: si cada
    /// una pusiera sus columnas, volveríamos a tener el layout escrito en dos sitios, que es de
    /// donde vino esto.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void La_cabecera_usa_el_reparto_compartido(string view)
    {
        string xaml = ViewLayout.Xaml(view);

        xaml.Should().Contain("<c:PageHeader Grid.Row=\"0\"",
            "la cabecera reparte por columnas, y ese reparto vive en una sola clase");
        xaml.Should().NotContain("HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\">",
            "alinear a la derecha DENTRO de la misma celda es exactamente lo que superponía");
    }

    // ================================================================ y medido de verdad

    /// <summary>
    /// La prueba que no se discute: se carga la cabecera real, se mide, y se comprueba que la zona
    /// de identidad y la de acciones <b>no se cruzan</b>. Con el contenido en el peor caso — todos
    /// los botones visibles (los bindings de visibilidad no se cargan aquí, así que sale todo) y
    /// textos largos de verdad en cada hueco.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    [MemberData(nameof(StressWidths))]
    public void Identidad_y_acciones_nunca_se_cruzan(string view, double width)
        => ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot(view);
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);

            ViewLayout.Layout(root, width, 768);

            var identity = (FrameworkElement)header.Children[0];
            var actions = (FrameworkElement)header.Children[1];

            Grid.GetColumn(identity).Should().Be(0);
            Grid.GetColumn(actions).Should().Be(1);

            // El HUECO que el reparto le da a cada zona, no lo que cada una dice que mide. WPF
            // deja que un hijo se arregle MÁS ancho que su celda cuando no cabe —y luego lo
            // recorta—, así que `ActualWidth` mide una intención y no lo que se ve. La celda sí.
            Rect left = SlotOf(identity);
            Rect right = SlotOf(actions);

            right.Width.Should().BeGreaterThan(0, "las acciones no se pueden perder: son lo que se pulsa");
            Overlaps(left, right).Should().BeFalse(
                $"{view} a {width}px: la identidad no puede pisar a las acciones");
            left.Right.Should().BeLessThanOrEqualTo(right.Left + 0.5,
                "la identidad TERMINA donde empiezan las acciones: eso es repartir, no superponer");
        });

    /// <summary>
    /// Y ni un control suelto de una zona se cruza con uno de la otra. Es la comprobación fina: dos
    /// rectángulos padre que no se tocan pueden tener hijos que sí, si alguien anida un panel que
    /// no encoge.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void Ningun_control_de_una_zona_pisa_a_uno_de_la_otra(string view, double width)
        => ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot(view);
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);
            ViewLayout.Layout(root, width, 768);

            List<Rect> identity = VisibleBoxes(header, (FrameworkElement)header.Children[0]);
            List<Rect> actions = VisibleBoxes(header, (FrameworkElement)header.Children[1]);

            foreach (Rect a in actions)
            {
                foreach (Rect b in identity)
                {
                    Overlaps(a, b).Should().BeFalse(
                        $"{view} a {width}px: un botón encima de un texto se pulsa igual, y el "
                        + "usuario no sabe qué hay debajo");
                }
            }
        });

    /// <summary>
    /// El texto largo se RECORTA, no se sale. Un <c>StackPanel</c> da a sus hijos ancho infinito y
    /// nunca llega a recortar nada — por eso la zona de identidad es un <c>Grid</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void Un_titulo_largo_se_recorta_y_se_lee_en_el_tooltip(string view)
        => ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot(view);
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);
            ViewLayout.Layout(root, 1366, 768);

            var trimmed = ViewLayout.Descendants<TextBlock>(header)
                .Where(t => t.TextTrimming == TextTrimming.CharacterEllipsis)
                .ToList();

            trimmed.Should().NotBeEmpty("el subtítulo se recorta con puntos suspensivos");
            trimmed.Should().OnlyContain(t => t.MaxWidth < double.PositiveInfinity,
                "sin un tope, el recorte no llega a activarse hasta que ya ha empujado a los demás");

            SlotOf((FrameworkElement)header.Children[0]).Width
                .Should().BeLessThanOrEqualTo(header.ActualWidth,
                    "la identidad cabe en su columna: para eso está el recorte");
        });

    /// <summary>
    /// Y la cabecera recorta lo que se salga. Es el cinturón además de los tirantes: un panel
    /// anidado que no quepa dibujaría fuera de su columna sin pedirle permiso a nadie.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void La_cabecera_recorta_lo_que_se_salga(string view)
        => ViewLayout.OnUiThread(() =>
            HeaderOf(ViewLayout.LoadRoot(view)).ClipToBounds.Should().BeTrue());

    // ================================================================ andamiaje

    private static PageHeader HeaderOf(Grid root)
        => ViewLayout.InRow(root, 0).OfType<PageHeader>().Single();

    /// <summary>
    /// El peor caso realista: los bindings no se cargan en esta plantilla, así que cada hueco de
    /// texto llega vacío. Se rellenan con lo más largo que la pantalla enseña de verdad — el alias
    /// y el nombre de la aplicación, y el distintivo con proveedor y modelo.
    /// </summary>
    private static void FillWithLongText(PageHeader header)
    {
        foreach (TextBlock block in ViewLayout.Descendants<TextBlock>(header))
        {
            if (block.Text.Length == 0)
            {
                block.Text = "MEJ-0011 · XBLAST-INDUSTRIAL · Claude Code · modelo claude-opus-4-6";
            }
        }

        foreach (Button link in ViewLayout.Descendants<Button>(header))
        {
            link.Content ??= "Volver al hallazgo (MEJ-0011)";
        }
    }

    /// <summary>
    /// Los rectángulos de lo que de verdad SE PINTA dentro de una zona: cada hijo recortado por su
    /// zona. Es la medida honrada — la zona lleva <c>ClipToBounds</c>, así que lo que asome por su
    /// borde no llega al cristal. Comparar el rectángulo sin recortar mediría una intención, no lo
    /// que ve el usuario.
    /// </summary>
    private static List<Rect> VisibleBoxes(PageHeader header, FrameworkElement zone)
    {
        Rect bounds = SlotOf(zone);
        return ViewLayout.Descendants<FrameworkElement>(zone)
            .Where(e => e.Visibility == Visibility.Visible && e.ActualWidth > 0 && e.ActualHeight > 0)
            .Select(e => Rect.Intersect(ViewLayout.BoxOf(e, header), bounds))
            .Where(r => !r.IsEmpty)
            .ToList();
    }

    /// <summary>
    /// El hueco que el reparto le dio a una zona, en coordenadas de la cabecera. Es lo que de
    /// verdad se ve: la zona lleva <c>ClipToBounds</c>, así que su celda ES su ventana al mundo.
    /// </summary>
    private static Rect SlotOf(FrameworkElement zone) => LayoutInformation.GetLayoutSlot(zone);

    /// <summary>
    /// Se solapan de verdad: comparten superficie. <c>Rect.IntersectsWith</c> de WPF dice que sí
    /// cuando solo se TOCAN por el borde —usa <c>&gt;=</c>—, y dos columnas contiguas siempre se
    /// tocan: con ese criterio, un reparto correcto daría siempre positivo.
    /// </summary>
    private static bool Overlaps(Rect a, Rect b)
    {
        Rect shared = Rect.Intersect(a, b);
        return !shared.IsEmpty && shared.Width > 0.5 && shared.Height > 0.5;
    }
}
