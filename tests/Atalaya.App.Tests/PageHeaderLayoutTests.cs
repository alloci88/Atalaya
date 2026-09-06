using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F16-RETOQUE §2 — LA CABECERA NO SE PISA Y ADEMÁS RESPIRA.
/// <para>
/// <b>El defecto original.</b> Las cabeceras eran un <c>Grid</c> sin columnas con dos
/// <c>StackPanel</c> dentro, el segundo con <c>HorizontalAlignment="Right"</c>. Eso no reparte
/// espacio: <b>superpone</b> — la misma lección de D-710b, en el otro eje. Con el distintivo de
/// proveedor y modelo de F16, «Pausar» se pintó encima de «Volver al hallazgo (MEJ-0011)».
/// </para>
/// <para>
/// <b>Y lo que trajo esta tanda.</b> Arreglado el solape, la captura seguía enseñando una cabecera
/// apretada: las piezas se tocaban y el ojo leía una sola masa. «Apretado» es una apreciación
/// hasta que se mide, así que aquí se mide: <b>separaciones mínimas</b> entre piezas, un margen
/// claro entre la zona de identidad y la de acciones, y aire extra alrededor de la destructiva.
/// Un test de geometría que solo comprueba «no se solapan» da por buena la cabecera de la captura.
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
    /// 210 px y los márgenes otros 32. Así que 1366 —el portátil de la casa— deja 1124; 900 —el
    /// mínimo que la ventana admite, <c>MinWidth</c>— deja 658; y la ventana a media pantalla del
    /// portátil (683) deja 441, que es el caso que el usuario mira de verdad cuando pone el código
    /// al lado. Ésos son los anchos alcanzables; medir a anchos imposibles sería medir una fantasía.
    /// </summary>
    public static TheoryData<string, double> Widths => new()
    {
        { "SessionView.xaml", 1124 },
        { "SessionView.xaml", 658 },
        { "SessionView.xaml", 441 },
        { "AssistedFixView.xaml", 1124 },
        { "AssistedFixView.xaml", 658 },
        { "AssistedFixView.xaml", 441 },
    };

    /// <summary>
    /// Y además un ancho por debajo de lo posible, donde el reparto tiene que aguantar igual aunque
    /// no quepa nada: la garantía de que no se superpone no depende de que haya sitio.
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

    /// <summary>
    /// La cabecera se queda con <b>lo que se pulsa</b> (F16-RETOQUE §2·3). «Compilar solución
    /// completa» no es una acción: es un ajuste de la sesión, y al marcarlo no pasa nada — cambia
    /// lo que hará el siguiente build. Su sitio es junto al resultado de ese build, que es donde
    /// se ve su consecuencia, y no en la fila de los botones.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void La_cabecera_no_lleva_ajustes(string view)
        => ViewLayout.OnUiThread(() =>
        {
            PageHeader header = HeaderOf(ViewLayout.LoadRoot(view));

            ViewLayout.Descendants<CheckBox>(header).Should().BeEmpty(
                "un interruptor entre botones se lee como un botón: la cabecera es para lo que se "
                + "pulsa, y el ajuste vive donde se ve lo que hace");
        });

    /// <summary>
    /// Y el ajuste no se ha perdido por el camino: sigue en la vista, en el pie donde se enseña el
    /// resultado del build. Moverlo fuera de la cabecera no puede significar quitárselo al usuario.
    /// </summary>
    [Fact]
    public void El_ajuste_del_build_vive_junto_al_resultado_del_build()
    {
        string xaml = ViewLayout.Xaml("AssistedFixView.xaml");

        int footer = xaml.IndexOf("================================ PIE ", StringComparison.Ordinal);
        footer.Should().BeGreaterThan(0);
        xaml[footer..].Should().Contain("Compilar solución completa",
            "el ámbito del build se elige al lado del veredicto del build");
    }

    /// <summary>
    /// El identificador del hallazgo aparece <b>una sola vez</b> (F16-RETOQUE §2·2). Salía
    /// truncado junto al título («BUG-0012…») y entero dentro de «Volver al hallazgo (BUG-0012)»:
    /// dos apariciones, y la primera —la que está donde el ojo busca— a medias.
    /// </summary>
    [Fact]
    public void El_identificador_del_hallazgo_se_dice_una_vez()
    {
        string xaml = ViewLayout.Xaml("AssistedFixView.xaml");

        // Desde D-983 el alias es un `Run` dentro del TextBlock del título —dos tamaños en una
        // fila solo comparten línea base si comparten párrafo—, así que el enlace lleva `Mode`.
        // La regla no cambia: el alias aparece UNA vez y entero.
        xaml.Should().Contain("{Binding FindingAliasText, Mode=OneWay}",
            "el alias vive junto al título, en la misma línea: es corto y cabe entero");
        xaml.Should().NotContain("Volver al hallazgo (",
            "el enlace ya no repite el alias que está dos piezas más a la izquierda");
    }

    // ================================================================ y medido de verdad

    /// <summary>
    /// La prueba que no se discute: se carga la cabecera real, se mide, y se comprueba que la zona
    /// de identidad y la de acciones <b>no se cruzan</b>. Con el contenido en el peor caso — todos
    /// los botones visibles (los bindings de visibilidad no se cargan aquí, así que sale todo) y
    /// textos largos de verdad en los huecos que están declarados para absorberlos.
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
            Grid.GetRow(identity).Should().Be(0);

            if (header.Stacked)
            {
                Grid.GetRow(actions).Should().Be(1, "sin ancho, la cabecera baja a dos filas");
            }
            else
            {
                Grid.GetColumn(actions).Should().Be(1);
            }

            // El HUECO que el reparto le da a cada zona, no lo que cada una dice que mide. WPF
            // deja que un hijo se arregle MÁS ancho que su celda cuando no cabe —y luego lo
            // recorta—, así que `ActualWidth` mide una intención y no lo que se ve. La celda sí.
            Rect left = SlotOf(identity);
            Rect right = SlotOf(actions);

            right.Width.Should().BeGreaterThan(0, "las acciones no se pueden perder: son lo que se pulsa");
            Overlaps(left, right).Should().BeFalse(
                $"{view} a {width}px: la identidad no puede pisar a las acciones");

            if (!header.Stacked)
            {
                left.Right.Should().BeLessThanOrEqualTo(right.Left + 0.5,
                    "la identidad TERMINA donde empiezan las acciones: eso es repartir, no superponer");
            }
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

    // ================================================================ y respira

    /// <summary>
    /// <b>El margen entre la identidad y las acciones</b> (F16-RETOQUE §2·1). No se pisaban, pero
    /// se tocaban: el último texto de la izquierda terminaba justo donde empezaba el primer botón.
    /// Con las dos zonas pegadas, el ojo lee una sola fila de cosas y no dos bloques con
    /// significados distintos — que es lo que la captura del parte enseñaba.
    /// <para>
    /// El hueco lo pone <see cref="PageHeader"/>, así que esto vale para las dos vistas sin que
    /// ninguna tenga que acordarse.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void Entre_la_identidad_y_las_acciones_hay_aire(string view, double width)
        => ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot(view);
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);
            ViewLayout.Layout(root, width, 768);

            var identityZone = (FrameworkElement)header.Children[0];
            Rect actions = ViewLayout.BoxOf((FrameworkElement)header.Children[1], header);

            if (header.Stacked)
            {
                (actions.Top - VisibleEdge(identityZone, vertical: true)).Should().BeGreaterThanOrEqualTo(
                    MinRowGap - Tolerance,
                    $"{view} a {width}px: dos filas pegadas se leen como una sola línea rota");
                return;
            }

            (actions.Left - VisibleEdge(identityZone, vertical: false)).Should().BeGreaterThanOrEqualTo(
                MinZoneGap - Tolerance,
                $"{view} a {width}px: quién soy y qué puedo hacer son dos bloques, no una lista");
        });

    /// <summary>
    /// <b>Y las piezas de dentro de cada zona también</b>. Los botones iban a 8 px unos de otros y
    /// se leían como un bloque continuo; el mínimo de la casa son <see cref="MinPieceGap"/>.
    /// <para>
    /// Se miran los hijos DIRECTOS de cada zona —que son las piezas que el usuario distingue— y no
    /// todos los descendientes: el texto de dentro de un botón está pegado a su borde por diseño,
    /// y medir eso sería medir el relleno del control.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void Las_piezas_de_una_zona_no_se_amontonan(string view, double width)
        => ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot(view);
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);
            ViewLayout.Layout(root, width, 768);

            foreach (FrameworkElement zone in header.Children.OfType<FrameworkElement>())
            {
                List<(FrameworkElement El, Rect Box)> pieces = PiecesOf(header, zone);
                for (int i = 1; i < pieces.Count; i++)
                {
                    (pieces[i].Box.Left - pieces[i - 1].Box.Right).Should().BeGreaterThanOrEqualTo(
                        MinPieceGap - Tolerance,
                        $"{view} a {width}px: «{Describe(pieces[i - 1].El)}» y «{Describe(pieces[i].El)}» "
                        + $"a menos de {MinPieceGap} px se leen como una sola pieza");
                }
            }
        });

    /// <summary>
    /// <b>La destructiva va aparte</b> (F16-RETOQUE §2·4). «Descartar todo» viajaba entre «Pausar»
    /// y «Cerrar», que son control de sesión: mismo tamaño, misma fila, mismo aire. Ahora va la
    /// última y con más hueco del que separa a las demás, para que el gesto que deshace el trabajo
    /// no esté a un píxel de distancia del que lo pausa.
    /// </summary>
    [Fact]
    public void La_accion_destructiva_no_viaja_entre_las_de_control()
        => ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot("AssistedFixView.xaml");
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);
            ViewLayout.Layout(root, 1124, 768);

            // POR DESCENDENCIA, no por hijos directos: desde R11 §4 «Descartar todo» y la razón
            // de su apagado viajan juntos en su propio panel —la razón necesita hueco y tope
            // propios, y sueltos en la barra no tenía ninguno de los dos—. Lo que este test
            // protege es el ORDEN de las acciones dentro de la zona, y ése no depende de cuántos
            // paneles haya por medio.
            var bar = (FrameworkElement)header.Children[1];
            List<Button> buttons = ViewLayout.Descendants<Button>(bar)
                .Where(c => c.Visibility == Visibility.Visible)
                .ToList();

            // Los botones de la cabecera son los del sistema desde D-983, no los de la librería:
            // tres `Appearance` distintas traían tres alturas y tres rellenos, y el grupo no
            // parecía un grupo. Aquí el destructivo se reconoce por su ROTULO y no por su estilo
            // porque el banco de geometría quita los `Style=` a propósito —mide la vista, no el
            // tema— así que preguntar por el estilo aquí no preguntaría por nada.
            var danger = buttons.Single(b => (b.Content as string) == "Descartar todo");

            // El ÚLTIMO BOTÓN, no el último elemento: detrás va el chip que dice por qué
            // «Descartar todo» está apagado (P-27, R10 §7), y una razón no es una acción.
            buttons[^1].Should().BeSameAs(danger, "la que deshace el trabajo va al final, no en medio");

            // LO QUE YA NO SE COMPRUEBA, y por qué. Hasta aquí este test exigía además que el
            // destructivo llevara MÁS aire que el resto (F16-RETOQUE §2·4, «la separación ES la
            // jerarquía»). En la revisión de la Parte B el usuario miró la cabecera en el dist y
            // pidió lo contrario: los tres son un grupo, con la misma altura, el mismo relleno y
            // la MISMA separación entre ellos; lo que distingue al destructivo es su color, que es
            // lo que dice el principio 4. Se retira esa mitad de la regla —no se relaja: se
            // sustituye por una decisión posterior y explícita— y se conserva la otra, que es el
            // orden: la que deshace el trabajo va la última.
        });

    /// <summary>
    /// <b>A ancho estrecho, dos filas antes que comprimir</b> (F16-RETOQUE §2·5). Comprimir es la
    /// peor salida: la identidad se recorta hasta no decir nada y los botones se aprietan hasta
    /// tocarse. Partida en dos, las dos zonas caben enteras y siguen respirando.
    /// <para>
    /// El ancho al que se parte no es el mismo en las dos vistas, y tiene que serlo: depende de lo
    /// que pidan SUS acciones, que es el dato duro. La sesión en vivo tiene dos botones y aguanta
    /// más; el arreglo asistido tiene cuatro y se parte antes — de hecho ya a media pantalla del
    /// portátil (441 px), que es el caso que el usuario mira de verdad.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("SessionView.xaml", 240)]
    [InlineData("AssistedFixView.xaml", 441)]
    public void A_ancho_estrecho_la_cabecera_baja_a_dos_filas(string view, double width)
        => ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot(view);
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);
            ViewLayout.Layout(root, width, 768);

            header.Stacked.Should().BeTrue(
                $"{view} a {width}px: no cabe una fila con las dos zonas, así que se parte");

            var identity = (FrameworkElement)header.Children[0];
            var actions = (FrameworkElement)header.Children[1];
            Grid.GetRow(identity).Should().Be(0);
            Grid.GetRow(actions).Should().Be(1);
            SlotOf(actions).Width.Should().BeGreaterThan(0, "y las acciones siguen ahí, enteras");
        });

    /// <summary>Y a los anchos normales NO se parte: partir de más es tan malo como comprimir.</summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void A_ancho_de_portatil_la_cabecera_va_en_una_fila(string view)
        => ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot(view);
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);
            ViewLayout.Layout(root, 1124, 768);

            header.Stacked.Should().BeFalse("a 1366×768 sobra ancho para las dos zonas");
        });

    /// <summary>
    /// El texto largo se RECORTA, no se sale. Un <c>StackPanel</c> da a sus hijos ancho infinito y
    /// nunca llega a recortar nada — por eso la zona de identidad es un <c>Grid</c>.
    /// <para>
    /// <b>El recorte se lee del MARCADO desde UI-AUDIT-1</b> (P-01). Antes se medía sobre el árbol
    /// montado, y funcionaba porque cada vista escribía su propio <c>TextTrimming</c>. Ahora el
    /// recorte lo pone una de las tres primitivas del sistema —aquí <c>Text.Name</c>— y
    /// <see cref="ViewLayout"/> quita los estilos al cargar, porque viven en <c>Styles.xaml</c> y
    /// ése necesita la paleta montada. Lo que la vista tiene que declarar sigue siendo lo mismo y
    /// se comprueba igual: la primitiva, y un TOPE, sin el cual el recorte no llega a activarse
    /// hasta que ya ha empujado a los demás fuera de la pantalla.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Se lee de la fila de identidad</b> (R10 §6). Era un único <c>TextBlock</c> que abarcaba
    /// dos columnas; ahora las piezas son hermanas dentro de un <c>c:BaselineRow</c>, para que
    /// compartan línea base. La regla no cambia y se comprueba igual: la primitiva del sistema, un
    /// tope, y el tooltip que devuelve entero lo que se recorta.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Views))]
    public void Un_titulo_largo_se_recorta_y_se_lee_en_el_tooltip(string view)
    {
        string identidad = Regex.Match(
            ViewLayout.Xaml(view),
            @"<c:BaselineRow\b.*?</c:BaselineRow>",
            RegexOptions.Singleline).Value;

        identidad.Should().NotBeEmpty("la identidad de la cabecera es una fila de línea base");
        identidad.Should().Contain(
            "{StaticResource Text.Name}",
            "el recorte de un nombre lo pone la primitiva del sistema, no la vista (P-01)");
        // UN TOPE DE LA CABECERA, y de la escala: el que necesite cada vista. En la sesión en vivo
        // lo lleva la fila entera —lo último que hay es el subtítulo, y es lo que tiene que ceder—;
        // en el arreglo asistido lo lleva el nombre de la aplicación, que es la única pieza de las
        // cinco que puede ser larga (R11 §2). Lo que no puede faltar es el tope: sin él, el recorte
        // no llega a activarse hasta que ya ha empujado a los demás fuera de la pantalla.
        identidad.Should().MatchRegex(
            @"MaxWidth=""\{StaticResource Header\.[A-Za-z]+MaxWidth\}""",
            "sin un tope, el recorte no llega a activarse hasta que ya ha empujado a los demás");
        identidad.Should().Contain("ToolTip=", "y lo que no cabe se lee entero al pasar por encima");

        ViewLayout.OnUiThread(() =>
        {
            Grid root = ViewLayout.LoadRoot(view);
            PageHeader header = HeaderOf(root);
            FillWithLongText(header);
            ViewLayout.Layout(root, 1366, 768);

            SlotOf((FrameworkElement)header.Children[0]).Width
                .Should().BeLessThanOrEqualTo(header.ActualWidth,
                    "la identidad cabe en su columna: para eso está el recorte");
        });
    }

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

    /// <summary>El mínimo entre dos piezas contiguas de la misma zona.</summary>
    private const double MinPieceGap = 12;

    /// <summary>
    /// El mínimo entre la zona de identidad y la de acciones.
    /// <para>
    /// <b>Es un número escrito aquí y no <c>PageHeader.ZoneGap</c></b>, y la diferencia importa: un
    /// test que comparase la producción consigo misma pasaría con el hueco puesto a cero. Lo que
    /// esto fija es el criterio —16 px entre bloques—, no que el código coincida consigo mismo.
    /// </para>
    /// </summary>
    private const double MinZoneGap = 16;

    /// <inheritdoc cref="MinZoneGap"/>
    private const double MinRowGap = 10;

    /// <summary>Medio píxel de holgura: WPF redondea a la rejilla del dispositivo.</summary>
    private const double Tolerance = 0.5;

    private static PageHeader HeaderOf(Grid root)
        => ViewLayout.InRow(root, 0).OfType<PageHeader>().Single();


    /// <summary>
    /// Hasta dónde llega lo que de VERDAD se ve de una zona. No basta con su rectángulo: cuando el
    /// contenido no cabe, WPF lo arregla más ancho que su celda y luego lo recorta, así que el
    /// rectángulo mide una intención. La zona lleva <c>ClipToBounds</c>, de modo que lo visible
    /// termina, como muy tarde, en el borde interior de su celda — el que deja el margen.
    /// </summary>
    private static double VisibleEdge(FrameworkElement zone, bool vertical)
    {
        Rect box = ViewLayout.BoxOf(zone, (Visual)zone.Parent);
        Rect slot = SlotOf(zone);
        return vertical
            ? Math.Min(box.Bottom, slot.Bottom - zone.Margin.Bottom)
            : Math.Min(box.Right, slot.Right - zone.Margin.Right);
    }

    /// <summary>
    /// El peor caso realista: los bindings no se cargan en esta plantilla, así que cada hueco de
    /// texto llega vacío.
    /// <para>
    /// Se rellenan <b>según lo que cada hueco declara</b>: los que se recortan reciben lo más largo
    /// que la pantalla enseña de verdad —el nombre de una aplicación, el distintivo con proveedor y
    /// modelo—, y los que NO se recortan reciben lo que de verdad llevan, que es corto por
    /// construcción (un alias). Meterle un texto de 60 caracteres a una columna declarada
    /// <c>Auto</c> mediría una cabecera que no existe.
    /// </para>
    /// </summary>
    private static void FillWithLongText(PageHeader header)
    {
        foreach (TextBlock block in ViewLayout.Descendants<TextBlock>(header))
        {
            if (block.Text.Length > 0)
            {
                continue;
            }

            block.Text = block.TextTrimming == TextTrimming.CharacterEllipsis
                ? "XBLAST-INDUSTRIAL · Claude Code · modelo claude-opus-4-6"
                : "MEJ-0011";
        }

        foreach (Button link in ViewLayout.Descendants<Button>(header))
        {
            link.Content ??= "Volver al hallazgo";
        }
    }

    /// <summary>
    /// Las piezas que el usuario distingue dentro de una zona: sus hijos DIRECTOS visibles, de
    /// izquierda a derecha y ya recortados por la zona. Las que no llegan a verse —porque la zona
    /// las recortó entera— no cuentan: no hay separación que medir con algo que no se pinta.
    /// </summary>
    private static List<(FrameworkElement El, Rect Box)> PiecesOf(PageHeader header, FrameworkElement zone)
    {
        if (zone is not Panel panel)
        {
            return new List<(FrameworkElement, Rect)>();
        }

        Rect bounds = SlotOf(zone);
        return panel.Children.OfType<FrameworkElement>()
            .Where(c => c.Visibility == Visibility.Visible && c.ActualWidth > 0)
            .Select(c => (El: c, Box: Rect.Intersect(ViewLayout.BoxOf(c, header), bounds)))
            .Where(p => !p.Box.IsEmpty && p.Box.Width > 0.5)
            .OrderBy(p => p.Box.Left)
            .ToList();
    }

    /// <summary>Cómo se nombra una pieza en el mensaje de un fallo: su tipo y lo que dice.</summary>
    private static string Describe(FrameworkElement element) => element switch
    {
        TextBlock t => $"TextBlock: {t.Text}",
        ContentControl { Content: TextBlock inner } => $"{element.GetType().Name}: {inner.Text}",
        ContentControl c => $"{element.GetType().Name}: {c.Content}",
        _ => element.GetType().Name,
    };

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
