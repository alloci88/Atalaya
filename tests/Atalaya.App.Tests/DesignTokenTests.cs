using System.Text.RegularExpressions;
using System.Windows;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F26 Parte A — <b>ningún XAML escribe un tamaño ni un margen a mano</b> (principio 3, D-946).
/// <para>
/// <b>La regla y por qué existe.</b> Antes de F26 había 285 <c>FontSize</c> y unos 700
/// <c>Margin</c>/<c>Padding</c> literales repartidos por 25 ficheros. Con eso no hay «texto
/// pequeño»: hay veintitantos tamaños pequeños distintos, y el más pequeño de todos acaba en la
/// pantalla que más texto tiene. Un tamaño que se escribe en un sitio se corrige en un sitio; uno
/// que se escribe en cuarenta no se corrige nunca.
/// </para>
/// <para>
/// <b>Por qué es un test de regla y no de forma.</b> No mira que un control exista ni cómo se
/// pinta: mira que la escala siga siendo LA escala. Lo que se rompe en silencio es que alguien
/// añada un <c>FontSize="11"</c> a una vista nueva porque «ahí no cabía», y la aplicación vuelva
/// poco a poco a donde estaba — que es exactamente cómo llegó.
/// </para>
/// <para>
/// <b>La lista de pendientes se puede acortar, nunca alargar.</b> F26 pasa las vistas por el
/// sistema en tres partes; mientras tanto, las que no han llegado a su turno viven en
/// <see cref="Pendientes"/>. Este test comprueba dos cosas a la vez: que las ya convertidas no se
/// vuelvan atrás, y que la lista no crezca — un fichero nuevo nace convertido o no nace.
/// </para>
/// </summary>
public sealed class DesignTokenTests
{
    /// <summary>
    /// Las vistas que todavía no han pasado por el sistema. Cada parte de F26 tacha las suyas:
    /// la B se lleva Portafolio, Inventario, Hallazgos, la ficha, la sesión y el arreglo; la C,
    /// Ajustes, Cuenta, Métricas, Informes y el alta.
    /// <para>
    /// <b>Al cerrar la C quedan los DIÁLOGOS, y solo ellos.</b> Van con la vista que los abre y esa
    /// vista ya está hecha, así que lo que les queda es mecánico —tipografía, espaciado y color por
    /// tokens— y sin decisiones de diseño nuevas. Está apuntado en BACKLOG. La regla de esta lista
    /// no cambia: solo puede encoger.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Pendientes = new(StringComparer.OrdinalIgnoreCase);

    // VACÍA desde UI-AUDIT-1 (raíz 2, UI-0013 y UI-0014). Los nueve diálogos eran lo único que
    // quedaba, y la auditoría midió lo que costaba dejarlos fuera: el modo claro que D-948 decidió
    // no existía dentro de ellos —ventana `#F2EBDD`, diálogo `#FAFAFA`, y las cajas de texto en
    // blanco puro—, su primario era el acento de Windows, y en «Patrones silenciados» convivían
    // las dos paletas: la página de fábrica con tarjetas crema, así que la tarjeta salía MÁS
    // OSCURA que la página que la sostiene, al revés que en toda la aplicación.
    //
    // P-24 como criterio de orden: se convierten con la vista que los abre y no en una tanda
    // aparte, porque un diálogo blanco encima de una pantalla crema no se lee como «esto todavía
    // no está hecho» sino como «esto es de otro programa» — y los dos más blancos eran justo los
    // dos que piden confirmar un borrado.

    /// <summary>
    /// Los ficheros del sistema visual: son los que DECLARAN los números, así que son los únicos
    /// que pueden escribirlos.
    /// </summary>
    private static readonly HashSet<string> Sistema = new(StringComparer.OrdinalIgnoreCase)
    {
        "Themes/Tokens.xaml",
        "Themes/Palette.Dark.xaml",
        "Themes/Palette.Light.xaml",
        "Themes/Styles.xaml",
        "Themes/Converters.xaml",
    };

    [Fact]
    public void Ningun_XAML_ya_convertido_escribe_un_tamano_de_letra_a_mano()
    {
        var culpables = Escanear(@"\bFontSize\s*=\s*""[0-9]");

        culpables.Should().BeEmpty(
            "los tamaños salen de la escala de `Tokens.xaml` (FontSize.Body, FontSize.Small…); "
            + "escribir uno a mano es volver a tener veintitantos tamaños en vez de siete");
    }

    [Fact]
    public void Ningun_XAML_ya_convertido_escribe_un_margen_a_mano()
    {
        var culpables = Escanear(@"\b(?:Margin|Padding)\s*=\s*""-?[0-9]");

        culpables.Should().BeEmpty(
            "los espacios salen de la escala de `Tokens.xaml` (Pad.M, Pad.Row…) o de `Stack.Gap`; "
            + "escribir uno a mano es cómo dos huecos «pequeños» acaban midiendo 2 y 6");
    }

    /// <summary>
    /// <b>Y ningún color a mano</b> (F26-B revisión, D-983). Es la misma regla que las dos de
    /// arriba, y la que más falta hacía.
    /// <para>
    /// <b>De dónde viene.</b> El aviso de fin de arreglo llevaba escrito
    /// <c>Background="#22E0A030"</c> con <c>Foreground="#F0D090"</c>: ámbar translúcido con tinta
    /// ámbar clara. Sobre el gris oscuro de siempre se leía; sobre el crema del tema claro es
    /// ámbar claro sobre ámbar claro, es decir, nada. Y no era un caso suelto: había cincuenta y
    /// tantos colores escritos a mano repartidos por las vistas, todos elegidos mirando el tema
    /// oscuro, cada uno una excepción silenciosa al principio 5.
    /// </para>
    /// <para>
    /// <b>Por qué se rompe en silencio.</b> Un color a mano no falla nunca: pinta. Solo deja de
    /// verse, y solo en el tema que nadie miró al escribirlo — que es exactamente cómo el modo
    /// claro llegó a estar «casi terminado» durante meses. Los pares de la paleta sí están
    /// medidos (D-947, <c>PaletteContrastTests</c>), pero medir la paleta no sirve de nada si las
    /// vistas no la usan.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_XAML_ya_convertido_escribe_un_color_a_mano()
    {
        // Cualquier literal hexadecimal en un atributo —#RGB, #RRGGBB o #AARRGGBB— y también los
        // colores CON NOMBRE que se usaban aquí: `Foreground="White"` sobre una pastilla de
        // gravedad es el mismo problema escrito de otra forma —blanco puro sobre el ámbar del
        // tema claro no llega a AA, y la paleta tiene `Brush.Ink.OnVivid` medido justo para eso—.
        // «Transparent» no cuenta: no es un color, es la ausencia de uno.
        var culpables = Escanear(@"=\s*""(?:#[0-9A-Fa-f]{3,8}|White|Black|Gray|Red|Green|Blue|Yellow)""");

        culpables.Should().BeEmpty(
            "los colores salen de la paleta (Brush.Warning.Ink, Brush.Surface2, Brush.Sev.Crit…), "
            + "que tiene sus pares medidos a AA en los DOS temas; un color escrito a mano se "
            + "elige mirando un solo tema y desaparece en el otro sin fallar");
    }

    /// <summary>
    /// <b>Un enlace se sienta en el margen del bloque</b> (F26-B revisión, D-983).
    /// <para>
    /// Dentro de un bloque de texto hay UN margen izquierdo. Un <c>Button.Link</c> con relleno
    /// horizontal mete su rótulo unos píxeles a la derecha de la línea de arriba y de la de abajo,
    /// y eso es exactamente el defecto que el usuario describió como «líneas consecutivas del
    /// mismo bloque que arrancan a distintas x»: con 4 px no se lee como un error, se lee como que
    /// la pantalla está mal hecha y no se sabe por qué.
    /// </para>
    /// <para>
    /// <b>Por qué en el sistema y no en cada vista.</b> El enlace es el ÚNICO control que aparece
    /// mezclado en columnas de texto —la deriva del Portafolio, los «Gestionar» del ciclo, «Ver
    /// detalle»—, así que su relleno gobierna la alineación de media aplicación desde un solo
    /// sitio. Medirlo aquí vale por medirlo en todas.
    /// </para>
    /// </summary>
    [Fact]
    public void Un_enlace_no_tiene_relleno_horizontal()
    {
        H(PadOfStyle("Button.Link")).Should().Be(
            0,
            "el rótulo de un enlace arranca donde arranca el párrafo que lo rodea; el relleno "
            + "vertical se queda, que es el que hace cómodo pulsarlo");
    }

    /// <summary>
    /// La lista de pendientes solo puede encoger. Sin esto, un fichero nuevo escrito a la antigua
    /// se «arreglaría» añadiéndolo a la lista, y la lista dejaría de significar nada.
    /// </summary>
    [Fact]
    public void La_lista_de_pendientes_no_nombra_ficheros_que_ya_no_existen()
    {
        var faltan = Pendientes.Where(p => !File.Exists(Path.Combine(XamlRoot(), p.Replace('/', Path.DirectorySeparatorChar)))).ToList();

        faltan.Should().BeEmpty("un pendiente que ya no existe hay que tacharlo de la lista, no dejarlo");
    }

    /// <summary>
    /// <b>EL RAÍL SE MIDE POR CARRILES</b> (D-966), y aquí se comprueba que las cuentas cuadren.
    /// <para>
    /// <b>De dónde viene.</b> El raíl plegado no pintaba los iconos: el chip azul de la entrada
    /// activa medía exactamente 24 px —los 12+12 de su relleno— y no quedaba ni un píxel dentro
    /// (D-963). Aquel arreglo ajustó un relleno distinto para cada estado; funcionaba, y estaba mal
    /// planteado: dos números que hay que mantener iguales a mano acaban siendo distintos, y el
    /// icono habría vuelto a moverse al siguiente retoque. Ahora hay un CANAL fijo, el mismo en los
    /// dos estados, y estos tests son la cuenta escrita.
    /// </para>
    /// <para>
    /// <b>Por qué son de regla.</b> Lo que protegen no se cae con estruendo: un ancho que no cabe
    /// se recorta en silencio —no falla nada, el icono simplemente no está— y una columna que se
    /// desalinea unos píxeles no rompe nada en absoluto, solo se ve mal. Ninguna de las dos deja
    /// rastro en ningún log.
    /// </para>
    /// </summary>
    [Fact]
    public void El_canal_de_iconos_cabe_el_icono_y_el_avatar()
    {
        double canal = Token("Rail.IconChannel");

        canal.Should().BeGreaterThanOrEqualTo(
            Token("Icon.Size"),
            "un icono que no cabe en su canal no protesta: se recorta en silencio");

        canal.Should().BeGreaterThanOrEqualTo(
            Token("Rail.AvatarSize"),
            "y el avatar de la cuenta ocupa ese mismo canal — salía cortado por la mitad");
    }

    /// <summary>
    /// <b>Y la fila cabe su icono a lo ALTO</b> (F26 §C, segunda revisión).
    /// <para>
    /// La fila del raíl pasó de tener alto MÍNIMO a tenerlo fijo —con un mínimo, la fila de usuario
    /// anclada al pie crecía con lo que le sobrara al raíl y el avatar se despegaba de su insignia—.
    /// Un alto fijo obliga a hacer la cuenta: 36 de fila, 24 de icono, quedan 6 arriba y 6 abajo.
    /// Con los 8 que había, la fila pedía 40 y <b>WPF recortaba el icono sin decir nada</b>: en la
    /// captura, la hoja de «Informes» era un palo y el triángulo de «Hallazgos» salía sin punta.
    /// </para>
    /// <para>
    /// Es el mismo defecto que D-963 por el otro eje —allí el icono no cabía a lo ancho—, y la
    /// misma razón para probarlo: un ancho o un alto que no caben se recortan en silencio, y un
    /// icono a medias es indistinguible de un icono mal dibujado.
    /// </para>
    /// </summary>
    [Fact]
    public void Una_fila_del_rail_cabe_su_icono_a_lo_alto()
    {
        double fila = Token("Rail.RowHeight");
        double aire = Pad("Pad.RailItem").Top + Pad("Pad.RailItem").Bottom;

        (fila - aire).Should().BeGreaterThanOrEqualTo(
            Token("Icon.Size"),
            "la fila mide {0}, su relleno vertical se come {1} y el icono pide {2}: lo que no cabe "
            + "se recorta sin protestar",
            fila, aire, Token("Icon.Size"));

        (fila - aire).Should().BeGreaterThanOrEqualTo(
            Token("Rail.AvatarSize"), "y el avatar de la cuenta ocupa esa misma fila");
    }

    /// <summary>
    /// <b>UNA REGLA DE RECORTE PARA TODA LA APLICACIÓN</b> (P-01, UI-0009, UI-0011, UI-0033).
    /// <para>
    /// <b>De dónde viene.</b> Cada vista recortaba a su manera, o no recortaba, y el resultado era
    /// que lo mismo mentía en un sitio y no en otro: a 1280 —que es también cualquier pantalla al
    /// 150 %— las rutas de unidad se cortaban por el final <b>sin puntos suspensivos</b> contra el
    /// galón de desplegar (<c>…/Servicios/FormateadorInfc⌄</c> parece un nombre de fichero); la
    /// pastilla de proveedor y modelo del arreglo quedaba en <c>Age</c>; y la etiqueta
    /// «Descubrimiento» de Métricas se cortaba a mitad de la «o» final. D-983 §5 arregló <b>uno</b>
    /// de los tres, con un presupuesto de caracteres fijo, y no se generalizó: un presupuesto en
    /// caracteres no sabe a qué ancho está la ventana.
    /// </para>
    /// <para>
    /// <b>La regla.</b> Un texto que <b>no envuelve</b> y o recorta o tiene tope de ancho usa una
    /// de las tres primitivas —<c>Text.Name</c> (elipsis al final), <c>Text.Path</c> (acorta por el
    /// medio, sobre <c>c:PathText</c>) o <c>Text.Chip</c> (no se recorta: si no cabe, no se
    /// pinta)— y no escribe su propio <c>TextTrimming</c>.
    /// </para>
    /// <para>
    /// <b>Por qué es de regla.</b> Un texto recortado no falla: <b>miente</b>. No hay excepción, no
    /// hay log, y lo que queda en pantalla es una palabra perfectamente legible que dice otra cosa.
    /// Es el fallo más caro de esta interfaz precisamente porque no se nota, y el único que se
    /// puede vigilar sin mirar una captura es éste: que el recorte no lo decida cada vista.
    /// </para>
    /// </summary>
    [Fact]
    public void Un_texto_que_no_envuelve_recorta_con_una_de_las_tres_primitivas()
    {
        string[] primitivas = { "Text.Name", "Text.Path", "Text.Chip" };
        var culpables = new List<string>();

        foreach (string file in Directory.EnumerateFiles(XamlRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(XamlRoot(), file).Replace('\\', '/');
            if (Sistema.Contains(relative))
            {
                continue;
            }

            string body = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
            var envuelven = EstilosQueEnvuelven(body);

            foreach (Match m in Regex.Matches(body, @"<(?:TextBlock|c:PathText)\b(?:[^>""]|""[^""]*"")*?/?>"))
            {
                string el = m.Value;

                // Un texto que ENVUELVE con tope de ancho es una medida de lectura, no un recorte:
                // el ancho dice hasta dónde llega una línea, y lo que sobra baja. La elipsis que
                // algunos llevan es el «hay más» de un bloque plegado, con su «Más» al lado. El
                // envolver puede venir del atributo o de su estilo, así que se miran los dos.
                bool envuelve = el.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal)
                    || envuelven.Any(k => el.Contains($"{{StaticResource {k}}}", StringComparison.Ordinal));
                bool recorta = el.Contains("TextTrimming=", StringComparison.Ordinal);
                bool tope = el.Contains("MaxWidth=", StringComparison.Ordinal);

                if (envuelve || !(recorta || tope))
                {
                    continue;
                }

                bool usa = primitivas.Any(p => el.Contains($"{{StaticResource {p}}}", StringComparison.Ordinal));
                if (!usa || recorta)
                {
                    int line = body.Take(m.Index).Count(c => c == '\n') + 1;
                    culpables.Add($"{relative}:{line}");
                }
            }
        }

        culpables.Should().BeEmpty(
            "el recorte lo deciden `Text.Name`, `Text.Path` y `Text.Chip`, no cada vista; un texto "
            + "recortado a su manera no falla, MIENTE:"
            + Environment.NewLine + string.Join(Environment.NewLine, culpables));
    }

    /// <summary>
    /// <b>EL MARCADOR DE «ESTÁS AQUÍ» CABE EN SU CARRIL</b> (UI-0046).
    /// <para>
    /// <b>De dónde viene.</b> El <c>DataTrigger</c> de <c>IsActive</c> pinta el marcador de
    /// <c>Brush.Primary.Fill</c>, y el barrido de píxel de la auditoría no encontró <b>ni un solo
    /// píxel del primario</b> en el raíl de ninguna de las cuatro combinaciones, ni desplegado ni
    /// plegado. La causa: el carril es <c>Rail.MarkerLane</c> = 8 y el <c>Border</c> pedía
    /// <c>Rail.MarkerWidth</c> 3 más <c>Pad.XXS</c> 4 <b>a cada lado</b> = 11. Toda la señal de
    /// «estás aquí» recaía entonces en el relleno de la pastilla, que mide 1,27:1 en oscuro y
    /// 1,21:1 en claro contra el fondo del raíl; desplegado lo salvaba el texto en seminegrita, y
    /// plegado no hay texto.
    /// </para>
    /// <para>
    /// <b>Por qué es de regla, y por tercera vez.</b> Es la aritmética de carriles de D-966
    /// fallando otra vez —D-963 y D-999 §3 fueron las dos primeras—, y falla siempre igual: lo que
    /// no cabe <b>se recorta en silencio</b>. No hay excepción, ni log, ni test rojo; el elemento
    /// simplemente no está. Los dos raíles de la casa —el de primer nivel y la lista de secciones
    /// de Ajustes— comparten plantilla y compartían el defecto, así que se comprueban los dos.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("RailItem")]
    [InlineData("Setting.Section")]
    public void El_marcador_de_estas_aqui_cabe_en_su_carril(string estilo)
    {
        Thickness relleno = MarkerPadOfStyle(estilo);
        double pide = Token("Rail.MarkerWidth") + H(relleno);

        pide.Should().BeLessThanOrEqualTo(
            Token("Rail.MarkerLane"),
            "en «{0}» el marcador pide {1} —{2} de barra más {3} de relleno— y su carril mide {4}: "
            + "lo que no cabe se recorta SIN PROTESTAR, y el «estás aquí» deja de pintarse",
            estilo, pide, Token("Rail.MarkerWidth"), H(relleno), Token("Rail.MarkerLane"));
    }

    /// <summary>
    /// El raíl plegado mide EXACTAMENTE sus carriles: el del marcador, el del icono y el aire de la
    /// derecha. Ni uno más —sobraría hueco a un lado del icono y dejaría de estar centrado con el
    /// desplegado— ni uno menos.
    /// </summary>
    [Fact]
    public void El_rail_plegado_mide_sus_carriles()
    {
        double esperado = Token("Rail.MarkerLane") + Token("Rail.IconChannel") + Pad("Pad.RailChip").Right;

        Token("Rail.CollapsedWidth").Should().Be(
            esperado,
            "plegado es el raíl SIN el texto: {0} del marcador + {1} del canal + {2} de aire",
            Token("Rail.MarkerLane"), Token("Rail.IconChannel"), Pad("Pad.RailChip").Right);
    }

    /// <summary>
    /// El raíl NO tiene relleno horizontal: el ancho lo reparten los carriles de cada fila. Es lo
    /// que garantiza que el icono caiga en la misma x plegado y desplegado — con relleno en el
    /// raíl, cambiarlo al plegar movía el icono, que es exactamente el defecto de partida.
    /// </summary>
    [Fact]
    public void El_rail_no_tiene_relleno_horizontal()
    {
        var pad = Pad("Pad.Rail");

        H(pad).Should().Be(0, "quien reparte el ancho del raíl son los carriles, no su relleno");
        H(Pad("Pad.RailItem")).Should().Be(0, "ni el de sus filas");
    }

    // ================================================================ el andamiaje

    /// <summary>Lo que un relleno se come A LO ANCHO. `Thickness` de WPF no lo trae hecho.</summary>
    private static double H(Thickness t) => t.Left + t.Right;

    /// <summary>Un `sys:Double` de `Tokens.xaml`, leído del repositorio.</summary>
    private static double Token(string key)
    {
        var m = Regex.Match(
            Tokens(),
            $"<sys:Double x:Key=\"{Regex.Escape(key)}\">\\s*([0-9.]+)\\s*</sys:Double>");

        m.Success.Should().BeTrue($"`Tokens.xaml` declara {key}");
        return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Un `Thickness` de `Tokens.xaml`, con la sintaxis abreviada de WPF (1, «h,v» o los cuatro).</summary>
    private static Thickness Pad(string key)
    {
        var m = Regex.Match(
            Tokens(),
            $"<Thickness x:Key=\"{Regex.Escape(key)}\">\\s*([-0-9.,]+)\\s*</Thickness>");

        m.Success.Should().BeTrue($"`Tokens.xaml` declara {key}");

        double[] n = m.Groups[1].Value
            .Split(',')
            .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        return n.Length switch
        {
            1 => new Thickness(n[0]),
            2 => new Thickness(n[0], n[1], n[0], n[1]),
            _ => new Thickness(n[0], n[1], n[2], n[3]),
        };
    }

    /// <summary>El <c>Padding</c> que un estilo con nombre declara, resuelto a su token.</summary>
    private static Thickness PadOfStyle(string key)
    {
        string styles = File.ReadAllText(Path.Combine(XamlRoot(), "Themes", "Styles.xaml"));

        int start = styles.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"`Styles.xaml` declara {key}");

        int end = styles.IndexOf("</Style>", start, StringComparison.Ordinal);
        var m = Regex.Match(
            styles[start..end],
            @"<Setter Property=""Padding"" Value=""\{StaticResource ([^}]+)\}"" />");

        m.Success.Should().BeTrue($"{key} declara su relleno con un token, no a mano");
        return Pad(m.Groups[1].Value);
    }

    /// <summary>
    /// El <c>Margin</c> del <c>Border</c> llamado <c>Marker</c> dentro de la plantilla de un
    /// estilo, resuelto a su token. Es lo que el marcador se come además de su ancho.
    /// </summary>
    private static Thickness MarkerPadOfStyle(string key)
    {
        string styles = File.ReadAllText(Path.Combine(XamlRoot(), "Themes", "Styles.xaml"));

        int start = styles.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"`Styles.xaml` declara {key}");

        int end = styles.IndexOf("</Style>", start, StringComparison.Ordinal);
        var m = Regex.Match(
            styles[start..end],
            @"<Border x:Name=""Marker""[^>]*?Margin=""\{StaticResource ([^}]+)\}""",
            RegexOptions.Singleline);

        m.Success.Should().BeTrue($"{key} declara el margen de su marcador con un token, no a mano");
        return Pad(m.Groups[1].Value);
    }

    /// <summary>
    /// Las claves de estilo que ponen <c>TextWrapping="Wrap"</c>, ellas o alguno de sus
    /// antecesores. Se miran el sistema y el propio fichero, que es donde una vista declara los
    /// suyos. Sin esto, un texto que envuelve porque su estilo lo dice se contaría como recorte.
    /// </summary>
    private static HashSet<string> EstilosQueEnvuelven(string ownMarkup)
    {
        string styles = File.ReadAllText(Path.Combine(XamlRoot(), "Themes", "Styles.xaml"));

        var basedOn = new Dictionary<string, string>(StringComparer.Ordinal);
        var wraps = new HashSet<string>(StringComparer.Ordinal);

        foreach (string markup in new[] { styles, ownMarkup })
        {
            foreach (Match m in Regex.Matches(
                markup,
                @"<Style\b(?<head>[^>]*)>(?<body>.*?)</Style>",
                RegexOptions.Singleline))
            {
                var key = Regex.Match(m.Groups["head"].Value, @"x:Key=""([^""]+)""");
                if (!key.Success)
                {
                    continue;
                }

                var parent = Regex.Match(m.Groups["head"].Value, @"BasedOn=""\{StaticResource ([^}]+)\}""");
                if (parent.Success)
                {
                    basedOn[key.Groups[1].Value] = parent.Groups[1].Value;
                }

                if (m.Groups["body"].Value.Contains(
                    @"<Setter Property=""TextWrapping"" Value=""Wrap"" />", StringComparison.Ordinal))
                {
                    wraps.Add(key.Groups[1].Value);
                }
            }
        }

        // Y las que heredan de una que envuelve. Dos vueltas bastan para las cadenas que hay; se
        // repite hasta que no crezca, que es más barato que razonar sobre la profundidad.
        bool crecio = true;
        while (crecio)
        {
            crecio = false;
            foreach ((string key, string parent) in basedOn)
            {
                if (wraps.Contains(parent) && wraps.Add(key))
                {
                    crecio = true;
                }
            }
        }

        return wraps;
    }

    private static string Tokens()
        => File.ReadAllText(Path.Combine(XamlRoot(), "Themes", "Tokens.xaml"));

    private static List<string> Escanear(string patron)
    {
        var culpables = new List<string>();

        foreach (string file in Directory.EnumerateFiles(XamlRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(XamlRoot(), file).Replace('\\', '/');
            if (Pendientes.Contains(relative) || Sistema.Contains(relative))
            {
                continue;
            }

            string body = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            foreach (Match m in Regex.Matches(body, patron))
            {
                int line = body.Take(m.Index).Count(c => c == '\n') + 1;
                culpables.Add($"{relative}:{line} → {m.Value}");
            }
        }

        return culpables;
    }

    /// <summary>
    /// TODA clave que un XAML pide con <c>StaticResource</c> existe: o la declara el sistema visual
    /// o la declara el propio fichero.
    /// <para>
    /// <b>Se rompe en el peor sitio posible.</b> Una clave que no existe no es un error de
    /// compilación —el XAML se compila igual— ni lo ve ningún test que no monte la vista: es una
    /// <c>XamlParseException</c> el día que alguien navega a esa pantalla, y la aplicación se cierra
    /// entera. Pasó al mover la barra de guardar de Ajustes: dos tokens nuevos se escribieron en la
    /// vista y el parche que los añadía a <c>Tokens.xaml</c> no llegó a aplicarse. Compilación
    /// verde, 2.397 tests verdes, y el dist se moría al pulsar «Ajustes».
    /// </para>
    /// <para>
    /// Mira los DOS sitios donde una clave puede vivir: los diccionarios de <c>Themes/</c>, que
    /// están fusionados en la aplicación, y el propio fichero —muchas vistas declaran sus plantillas
    /// y estilos locales—. Con eso basta: lo que no está en ninguno de los dos no está.
    /// </para>
    /// </summary>
    [Fact]
    public void Ninguna_vista_pide_una_clave_que_no_existe()
    {
        var declaradas = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(XamlRoot(), "Themes"), "*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)"""))
            {
                declaradas.Add(m.Groups[1].Value);
            }
        }

        var huerfanas = new List<string>();

        foreach (string file in Directory.EnumerateFiles(XamlRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(XamlRoot(), file).Replace('\\', '/');
            if (relative.StartsWith("Themes/", StringComparison.Ordinal))
            {
                continue;
            }

            string body = File.ReadAllText(file);
            var locales = new HashSet<string>(
                Regex.Matches(body, @"x:Key=""([^""]+)""").Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);

            foreach (Match m in Regex.Matches(body, @"\{StaticResource ([A-Za-z0-9._]+)\}"))
            {
                string key = m.Groups[1].Value;
                if (!declaradas.Contains(key) && !locales.Contains(key))
                {
                    int line = body.Take(m.Index).Count(c => c == '\n') + 1;
                    huerfanas.Add($"{relative}:{line} → {key}");
                }
            }
        }

        huerfanas.Should().BeEmpty(
            "una clave que no existe revienta la vista al abrirla, no al compilarla:"
            + Environment.NewLine + string.Join(Environment.NewLine, huerfanas));
    }

    /// <summary>
    /// Un token de TAMAÑO no puede ir en el ancho de una columna ni en el alto de una fila.
    /// <para>
    /// Los tokens de medida se declaran <c>sys:Double</c>, y <c>ColumnDefinition.Width</c> y
    /// <c>RowDefinition.Height</c> quieren un <c>GridLength</c>. Un <c>StaticResource</c> no pasa
    /// por el conversor de tipos —eso solo lo hace un literal en el atributo—, así que la
    /// asignación falla; y falla <b>al montar la vista</b>, no al compilarla. Pasó al fijar la
    /// columna de claves de la ficha del hallazgo: compilación verde, tests verdes, y el dist se
    /// cerraba al abrir un hallazgo.
    /// </para>
    /// <para>
    /// La forma correcta es la que usan Ajustes y «Acerca de»: la columna en <c>Auto</c> y el ancho
    /// puesto en el hijo, donde <c>Width</c> sí es un <c>Double</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_token_de_tamano_se_usa_como_medida_de_una_rejilla()
    {
        var dobles = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(XamlRoot(), "Themes"), "*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"<sys:Double x:Key=""([^""]+)"""))
            {
                dobles.Add(m.Groups[1].Value);
            }
        }

        dobles.Should().NotBeEmpty("si no se encuentra ningún token, este test no está mirando nada");

        var culpables = new List<string>();

        foreach (string file in Directory.EnumerateFiles(XamlRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(XamlRoot(), file).Replace('\\', '/');
            string body = File.ReadAllText(file);

            foreach (Match m in Regex.Matches(
                body, @"<(ColumnDefinition|RowDefinition)[^>]*?\s(Width|Height)=""\{StaticResource ([A-Za-z0-9._]+)\}"""))
            {
                if (dobles.Contains(m.Groups[3].Value))
                {
                    int line = body.Take(m.Index).Count(c => c == '\n') + 1;
                    culpables.Add($"{relative}:{line} → {m.Groups[1].Value}.{m.Groups[2].Value} = {m.Groups[3].Value}");
                }
            }
        }

        culpables.Should().BeEmpty(
            "una rejilla quiere GridLength y el token es Double: la vista revienta al abrirse."
            + Environment.NewLine + string.Join(Environment.NewLine, culpables));
    }

    private static string XamlRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "Atalaya.App");
    }
}
