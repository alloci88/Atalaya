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
            data.Add(theme, "Sev.Crit", "Danger.Soft");
            data.Add(theme, "Sev.High", "Danger.Soft");
            data.Add(theme, "Sev.Med", "Warning.Soft");
            data.Add(theme, "Sev.Low", "Primary.Soft");
        }

        return data;
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
