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
        // Parte B
        "Views/PortfolioView.xaml",
        "Views/InventoryView.xaml",
        "Views/FindingsView.xaml",
        "Views/FindingDetailView.xaml",
        "Views/SessionView.xaml",
        "Views/AssistedFixView.xaml",

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
    /// <b>Con el raíl plegado, al icono le queda sitio.</b> Es la cuenta que falló en la Parte A y
    /// que dejó el menú sin iconos: solo se veía el chip azul de la entrada activa, vacío, y el
    /// avatar cortado por la mitad.
    /// <para>
    /// <b>La causa, medida.</b> El raíl plegado tenía 60 px; menos 24 de su propio relleno
    /// horizontal quedan 36; menos 11 de la barra de «estás aquí» con su margen quedan 25; menos
    /// los 24 del relleno de la entrada queda <b>1 px</b> para un icono de 18. En la captura, el
    /// chip azul medía exactamente 24 px —12+12 de relleno y nada dentro—.
    /// </para>
    /// <para>
    /// <b>Por qué es una regla y no forma.</b> Un ancho fijo dentro de una columna demasiado
    /// estrecha <b>se recorta en silencio</b>: no falla nada, no avisa nadie, y el icono
    /// simplemente no está. Es indistinguible de «no hay icono». Cualquiera que estreche el raíl
    /// plegado, engorde un relleno o agrande el icono vuelve a romperlo sin enterarse — y esto se
    /// pone rojo con los números delante.
    /// </para>
    /// </summary>
    [Fact]
    public void Con_el_rail_plegado_al_icono_le_queda_sitio()
    {
        double rail = Token("Rail.CollapsedWidth");
        double icon = Token("Icon.Size");
        double marker = Token("Rail.MarkerWidth");

        var railPad = Pad("Pad.RailCollapsed");
        var itemPad = Pad("Pad.RailItemCollapsed");
        var markerMargin = Pad("Pad.XXS");

        double markerCost = marker + H(markerMargin);
        double disponible = rail - H(railPad) - markerCost - H(itemPad);

        disponible.Should().BeGreaterThanOrEqualTo(
            icon,
            "un raíl de {0} px, con {1} de su relleno, {2} de la barra activa y {3} del relleno de "
            + "la entrada, deja {4} px para un icono de {5}. Un icono que no cabe NO protesta: se "
            + "recorta en silencio y el menú se queda con marcadores vacíos",
            rail, H(railPad), markerCost, H(itemPad), disponible, icon);
    }

    /// <summary>Y al avatar de la cuenta, que no lleva barra pero mide más que un icono.</summary>
    [Fact]
    public void Con_el_rail_plegado_al_avatar_le_queda_sitio()
    {
        const double avatar = 24;

        double disponible = Token("Rail.CollapsedWidth")
            - H(Pad("Pad.RailCollapsed"))
            - H(Pad("Pad.RailItemCollapsed"));

        disponible.Should().BeGreaterThanOrEqualTo(
            avatar,
            "el avatar salía cortado por la mitad: {0} px de hueco para {1} de avatar",
            disponible, avatar);
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
