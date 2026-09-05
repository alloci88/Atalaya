using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F26 Parte A — <b>los dos temas se leen, y eso se mide</b> (principio 5, D-947).
/// <para>
/// La queja que trae esta fase incluye «en modo claro, peor: el fondo es blanco nuclear y quema» y
/// «los textos de ayuda no se leen». Las dos son el mismo defecto medible: pares de color por
/// debajo del contraste que hace falta para leer. La maqueta que aprobó el usuario se dibujó a ojo
/// y siete de sus pares no llegaban a AA; los valores de la paleta son los suyos con el retoque
/// mínimo de luminosidad para pasar.
/// </para>
/// <para>
/// <b>Qué protege este test y por qué no es de forma.</b> No comprueba que exista un recurso:
/// comprueba que la COMBINACIÓN de dos recursos siga siendo legible. Es lo que se rompe en
/// silencio —alguien afina un gris «para que quede más suave» y deja las rutas de fichero en
/// 3,4:1— y lo que nadie ve en una captura hasta que lo sufre en una pantalla peor que la suya.
/// </para>
/// <para>
/// <b>Qué NO cubre, a propósito.</b> Los bordes y separadores decorativos. WCAG les pediría 3:1 si
/// identificaran un control, pero la línea de una tarjeta no lo hace; exigírselo obligaría a
/// bordes duros que no se parecerían a nada de lo aprobado. Lo que se mide aquí es TEXTO, que es
/// de lo que iba la queja.
/// </para>
/// </summary>
public sealed class PaletteContrastTests
{
    /// <summary>El mínimo de la AA para texto normal. El grande no se usa: nuestros textos de color son pequeños.</summary>
    private const double AA = 4.5;

    /// <summary>
    /// EL CONTRATO: qué se escribe sobre qué. Cada par es una combinación que la aplicación pinta
    /// de verdad —una tinta sobre una superficie, una etiqueta sobre un relleno, una pastilla sobre
    /// su fondo teñido—. Añadir un uso nuevo de color a una vista es añadir aquí su par.
    /// </summary>
    public static TheoryData<string, string, string> Pairs()
    {
        var data = new TheoryData<string, string, string>();
        string[] surfaces = { "Bg", "Surface", "Surface2" };
        string[] inks =
        {
            "Text", "TextMuted", "TextFaint",
            "Primary.Ink", "Success.Ink", "Warning.Ink", "Danger.Ink",
            "Sev.Crit", "Sev.High", "Sev.Med", "Sev.Low",
        };

        foreach (string theme in new[] { "Dark", "Light" })
        {
            foreach (string ink in inks)
            {
                foreach (string surface in surfaces)
                {
                    data.Add(theme, ink, surface);
                }
            }

            // La tinta que va ENCIMA de cada relleno sólido: la etiqueta de un botón primario, la
            // de uno de éxito. Es el par que más se rompe al elegir colores, porque el relleno se
            // elige mirando el fondo y no lo que lleva escrito.
            data.Add(theme, "Primary.OnFill", "Primary.Fill");
            data.Add(theme, "Success.OnFill", "Success.Fill");
            data.Add(theme, "Warning.OnFill", "Warning.Fill");
            data.Add(theme, "Danger.OnFill", "Danger.Fill");

            // Y sobre los fondos teñidos: pastillas de gravedad, avisos en línea, la razón de un
            // botón deshabilitado, la fila seleccionada.
            data.Add(theme, "Primary.Ink", "Primary.Soft");
            data.Add(theme, "Success.Ink", "Success.Soft");
            data.Add(theme, "Warning.Ink", "Warning.Soft");
            data.Add(theme, "Danger.Ink", "Danger.Soft");
            data.Add(theme, "Text", "Primary.Soft");
            data.Add(theme, "Text", "Success.Soft");
            data.Add(theme, "Text", "Warning.Soft");
            data.Add(theme, "Text", "Danger.Soft");

            // LA GRAVEDAD, SOBRE SU PROPIO RELLENO (P-05). Antes estos cuatro pares se medían
            // contra los fondos de OTRAS familias —`Danger.Soft` dos veces, `Warning.Soft`,
            // `Primary.Soft`—, que es exactamente lo que UI-0034 encontró en la pantalla: crítica
            // y alta con el mismo fondo. Cada nivel tiene ahora el suyo y se mide contra él.
            data.Add(theme, "Sev.Crit", "Sev.Crit.Soft");
            data.Add(theme, "Sev.High", "Sev.High.Soft");
            data.Add(theme, "Sev.Med", "Sev.Med.Soft");
            data.Add(theme, "Sev.Low", "Sev.Low.Soft");
            data.Add(theme, "Text", "Sev.Crit.Soft");
            data.Add(theme, "Text", "Sev.High.Soft");
            data.Add(theme, "Text", "Sev.Med.Soft");
            data.Add(theme, "Text", "Sev.Low.Soft");

            // Los cuatro papeles del azul suave, cada uno con lo que se escribe encima (UI-0049).
            data.Add(theme, "Text", "Nav.Active");
            data.Add(theme, "TextMuted", "Nav.Active");
            data.Add(theme, "Text", "Section.Active");
            data.Add(theme, "TextMuted", "Section.Active");
            data.Add(theme, "Text", "Info.Soft");
            data.Add(theme, "Primary.Ink", "Info.Soft");
        }

        return data;
    }

    /// <summary>
    /// <b>Cuatro rellenos y cuatro tintas, y los ocho distintos</b> (P-05, UI-0034).
    /// <para>
    /// <b>De dónde viene.</b> La escala de gravedad tenía cuatro tintas y ningún relleno propio:
    /// la pastilla tomaba prestados <c>Danger.Soft</c> (para crítica <b>y</b> para alta),
    /// <c>Warning.Soft</c> y <c>Primary.Soft</c>. Resultado medido en la captura: «Crít 0» y
    /// «Alta 27» con el mismo fondo, y la baja pintada del azul de «estás aquí». A la distancia a
    /// la que se recorre una lista —que es para lo que D-973 puso el color— había tres manchas
    /// donde tiene que haber cuatro.
    /// </para>
    /// <para>
    /// <b>Por qué es de regla.</b> «La gravedad se ve sin leer» es una decisión (D-973) y hasta
    /// ahora la única forma de comprobarla era mirar una captura y contar manchas. Dos colores que
    /// se acercan no rompen nada: siguen pasando el contraste, siguen pintando, y la escala deja
    /// de ser una escala sin que falle nada.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Los_cuatro_rellenos_y_las_cuatro_tintas_de_gravedad_son_distintos(string theme)
    {
        var palette = Palette(theme);
        string[] niveles = { "Crit", "High", "Med", "Low" };

        foreach (string papel in new[] { string.Empty, ".Soft" })
        {
            var valores = niveles.ToDictionary(n => n, n => palette[$"Color.Sev.{n}{papel}"], StringComparer.Ordinal);

            var repetidos = valores
                .GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => $"{string.Join(" = ", g.Select(p => p.Key))} → {g.Key}")
                .ToList();

            repetidos.Should().BeEmpty(
                "en el tema {0}, dos niveles de gravedad comparten {1} y la escala de cuatro se lee "
                + "como tres: {2}",
                theme.ToLowerInvariant(),
                papel == ".Soft" ? "relleno" : "tinta",
                string.Join("; ", repetidos));
        }
    }

    /// <summary>
    /// <b>Un diálogo pinta su fondo con una superficie que este test mide</b> (raíz 2, UI-0013).
    /// <para>
    /// <b>De dónde viene.</b> Ninguno de los nueve <c>*Dialog.xaml</c> pintaba su rejilla raíz.
    /// <c>MainWindow.xaml</c> sí lo hace, y su comentario explica por qué: <c>FluentWindow</c>
    /// aplica su propio telón por debajo. Medido en el píxel, la ventana de la aplicación era
    /// <c>#F2EBDD</c> y el diálogo <c>#FAFAFA</c> — los colores de fábrica de WPF-UI—, así que el
    /// modo claro que D-948 decidió no existía en los diálogos, y dos de ellos son los que
    /// confirman un borrado.
    /// </para>
    /// <para>
    /// <b>Por qué aquí.</b> Éste era el punto ciego exacto: los pares de arriba pasaban en verde
    /// sobre <c>Bg</c>, <c>Surface</c> y <c>Surface2</c> mientras la mitad de los diálogos se
    /// pintaba sobre una superficie que la aplicación no declara en ninguna parte. Medir bien
    /// unas superficies no dice nada de las que nadie mide; lo que cierra el hueco es que el
    /// diálogo esté OBLIGADO a usar una de las medidas.
    /// </para>
    /// </summary>
    [Fact]
    public void Cada_dialogo_pinta_su_fondo_con_una_superficie_medida()
    {
        string[] medidas = { "Brush.Bg", "Brush.Surface", "Brush.Surface2" };
        string views = Path.Combine(RepoRoot(), "src", "Atalaya.App", "Views");

        var dialogos = Directory.EnumerateFiles(views, "*Dialog.xaml").ToList();
        dialogos.Should().NotBeEmpty("si no se encuentra ningún diálogo, este test no está mirando nada");

        var descalzos = new List<string>();

        foreach (string file in dialogos)
        {
            string body = Regex.Replace(
                File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            // La rejilla raíz: la primera que aparece tras el `<ui:FluentWindow …>` de apertura.
            var m = Regex.Match(body, @"<Grid\b[^>]*>");

            string fondo = m.Success
                ? Regex.Match(m.Value, @"Background=""\{DynamicResource ([A-Za-z0-9.]+)\}""").Groups[1].Value
                : string.Empty;

            if (!medidas.Contains(fondo, StringComparer.Ordinal))
            {
                descalzos.Add($"{Path.GetFileName(file)} → {(fondo.Length == 0 ? "sin fondo" : fondo)}");
            }
        }

        descalzos.Should().BeEmpty(
            "un diálogo que no pinta su rejilla raíz se queda con el telón de fábrica de WPF-UI "
            + "—#FAFAFA en claro, #202020 en oscuro— y sale de otro programa:"
            + Environment.NewLine + string.Join(Environment.NewLine, descalzos));
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Cada_par_de_color_de_los_dos_temas_llega_a_AA(string theme, string ink, string surface)
    {
        var palette = Palette(theme);

        palette.Should().ContainKey(Key(ink), "la paleta {0} declara la tinta {1}", theme, ink);
        palette.Should().ContainKey(Key(surface), "la paleta {0} declara la superficie {1}", theme, surface);

        double ratio = Contrast(palette[Key(ink)], palette[Key(surface)]);

        ratio.Should().BeGreaterThanOrEqualTo(
            AA,
            "en el tema {0}, «{1}» escrito sobre «{2}» da {3:0.00}:1 y hace falta {4}:1 para poder leerlo",
            theme.ToLowerInvariant(), ink, surface, ratio, AA);
    }

    /// <summary>
    /// Las dos paletas declaran EXACTAMENTE las mismas claves. Es la regla que hace que cambiar de
    /// tema no pueda dejar un hueco: quien pinta pide `Brush.Danger.Ink` sin saber en qué tema
    /// está, y una clave que solo existe en uno de los dos revienta —o peor, se queda con el color
    /// del otro tema— justo al cambiar.
    /// </summary>
    [Fact]
    public void Las_dos_paletas_declaran_las_mismas_claves()
    {
        var dark = Palette("Dark").Keys.OrderBy(k => k, StringComparer.Ordinal);
        var light = Palette("Light").Keys.OrderBy(k => k, StringComparer.Ordinal);

        light.Should().Equal(dark);
    }

    // ================================================================ el andamiaje

    private static string Key(string role) => "Color." + role;

    /// <summary>Los `Color` declarados en una paleta, leídos del XAML del repositorio.</summary>
    private static Dictionary<string, string> Palette(string theme)
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atalaya.App", "Themes", $"Palette.{theme}.xaml"));

        return Regex.Matches(xaml, @"<Color x:Key=""([^""]+)"">\s*(#[0-9A-Fa-f]{6})\s*</Color>")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    /// <summary>La razón de contraste de WCAG 2.1, tal cual la define la norma.</summary>
    private static double Contrast(string a, string b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double C(int v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        int Byte(int start) => int.Parse(hex.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return (0.2126 * C(Byte(1))) + (0.7152 * C(Byte(3))) + (0.0722 * C(Byte(5)));
    }
}
