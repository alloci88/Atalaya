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
    /// Ajustes, Cuenta, Métricas, Informes y el alta. Al cerrar la C, esta lista queda vacía.
    /// </summary>
    private static readonly HashSet<string> Pendientes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Parte B — las seis vistas de trabajo ya han pasado por el sistema.

        // Parte C
        "Views/SettingsView.xaml",
        "Views/AccountView.xaml",
        "Views/MetricsView.xaml",
        "Views/ReportsView.xaml",
        "Views/OnboardingView.xaml",

        // Los diálogos van con la vista que los abre; se tachan con ella.
        "Views/AboutDialog.xaml",
        "Views/AuditLaunchDialog.xaml",
        "Views/CycleConfigDialog.xaml",
        "Views/DeleteAppDialog.xaml",
        "Views/DeletedUnitsDialog.xaml",
        "Views/DirectivesDialog.xaml",
        "Views/FactoryResetDialog.xaml",
        "Views/LinkCloneDialog.xaml",
        "Views/ModelRatesDialog.xaml",
        "Views/PatternSilencesDialog.xaml",
        "Views/ThresholdsDialog.xaml",
    };

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
    /// La x del texto es la suma de los carriles, y es la MISMA para las entradas y para los
    /// rótulos de grupo. Un rótulo que no cae en la columna de lo que rotula se lee como si fuera
    /// de otra cosa.
    /// </summary>
    [Fact]
    public void El_texto_del_rail_empieza_donde_acaban_los_carriles()
    {
        double sangria = Token("Rail.MarkerLane") + Token("Rail.IconChannel") + Pad("Pad.RailLabel").Left;

        Token("Rail.TextIndent").Should().Be(
            sangria,
            "el número que sangra los rótulos de grupo tiene que ser la suma de los carriles: "
            + "{0} + {1} + {2}",
            Token("Rail.MarkerLane"), Token("Rail.IconChannel"), Pad("Pad.RailLabel").Left);

        Pad("Pad.RailGroup").Left.Should().Be(
            Token("Rail.TextIndent"),
            "y los rótulos de grupo se sangran con él");
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
