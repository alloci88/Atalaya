using System.Text.RegularExpressions;
using System.Windows.Media;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F26 Parte B — <b>el código se lee en los dos temas</b> (principio 5, D-980).
/// <para>
/// El panel de código llevaba una superficie oscura fija, la misma en claro y en oscuro, y su
/// coloreado se ajustaba con una luminosidad HSL mínima. Dos aproximaciones: la primera dejaba un
/// ladrillo negro en una pantalla cálida, y la segunda no mide lo que dice medir — dos colores con
/// la misma luminosidad HSL contrastan distinto contra el mismo fondo, porque el ojo no pesa igual
/// el rojo, el verde y el azul.
/// </para>
/// <para>
/// <b>Por qué es un test de regla.</b> Un coloreado ilegible no falla: se pinta, y lo que no se
/// lee simplemente no se lee. Es el mismo defecto que <c>PaletteContrastTests</c> vigila para la
/// interfaz, aplicado al único sitio que se le escapaba — porque el color del código no sale de la
/// paleta, sale de una definición de AvalonEdit que hay que ajustar a mano.
/// </para>
/// </summary>
public sealed class CodePaletteTests
{
    private const double AA = 4.5;

    /// <summary>
    /// Los colores de fábrica de la definición de C# de AvalonEdit: los que hay que ajustar. Están
    /// escritos aquí y no leídos de la librería a propósito — lo que se prueba es el AJUSTE, y un
    /// test que tomara la entrada de la misma librería que la salida dejaría de tener un caso
    /// difícil el día que la librería cambie de colores.
    /// </summary>
    public static TheoryData<string, string> FactoryColours() => new()
    {
        { "Comment", "#008000" },       // verde oscuro: invisible sobre negro
        { "String", "#A31515" },        // rojo ladrillo: invisible sobre negro
        { "Keyword", "#0000FF" },       // azul puro: el peor caso de los dos lados
        { "Type", "#2B91AF" },
        { "Number", "#000000" },        // negro: invisible sobre negro, perfecto sobre claro
        { "Preprocessor", "#804000" },
        { "Char", "#A31515" },
        { "Punctuation", "#000000" },
    };

    [Theory]
    [MemberData(nameof(FactoryColours))]
    public void Cada_color_de_sintaxis_llega_a_AA_sobre_la_superficie_oscura(string name, string hex)
        => Ajustado(hex, Surface("Dark")).Should().BeGreaterThanOrEqualTo(
            AA, "«{0}» tiene que leerse sobre el panel oscuro", name);

    [Theory]
    [MemberData(nameof(FactoryColours))]
    public void Cada_color_de_sintaxis_llega_a_AA_sobre_la_superficie_clara(string name, string hex)
        => Ajustado(hex, Surface("Light")).Should().BeGreaterThanOrEqualTo(
            AA, "«{0}» tiene que leerse sobre el panel claro", name);

    /// <summary>
    /// El ajuste conserva el TONO. Es lo que hace reconocible un resaltado: si el verde de los
    /// comentarios saliera azul, el color dejaría de significar «comentario» y sería decoración.
    /// </summary>
    [Theory]
    [MemberData(nameof(FactoryColours))]
    public void El_ajuste_conserva_el_tono(string name, string hex)
    {
        Color original = Parse(hex);
        if (Saturation(original) < 0.1)
        {
            return; // un gris no tiene tono que conservar
        }

        foreach (string theme in new[] { "Dark", "Light" })
        {
            Color fitted = CodePalette.Fit(original, Surface(theme));
            Math.Abs(Hue(fitted) - Hue(original)).Should().BeLessThan(
                8, "«{0}» sigue siendo del mismo color en tema {1}", name, theme.ToLowerInvariant());
        }
    }

    /// <summary>
    /// La tinta del texto sin token también, que es la mayoría de lo que se ve en un fragmento.
    /// </summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void La_tinta_del_panel_llega_a_AA_sobre_su_superficie(string theme)
        => CodePalette.Contrast(Ink(theme), Surface(theme)).Should().BeGreaterThanOrEqualTo(AA);

    /// <summary>
    /// Y la superficie del panel se DISTINGUE de la tarjeta que lo contiene sin ser una mancha.
    /// La referencia es <c>Color.Surface</c> y no <c>Color.Bg</c> porque el panel de código
    /// siempre va dentro de una tarjeta —en la ficha y en la vista rápida—, nunca sobre el fondo
    /// de la página. Por debajo de 1,04 no se ve que haya un panel; por encima de 1,4 el panel
    /// pesa más que el código que contiene, que es en lo que se convirtió el ladrillo oscuro
    /// sobre el crema (D-980).
    /// </summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void La_superficie_del_panel_se_distingue_de_la_tarjeta_sin_ser_una_mancha(string theme)
    {
        double ratio = CodePalette.Contrast(Surface(theme), Colour(theme, "Color.Surface"));

        ratio.Should().BeInRange(1.04, 1.4);
    }

    /// <summary>
    /// Y las tres tintas del diff, que es donde esto se rompio de verdad. La fila de un diff lleva
    /// ENCIMA un tinte de color con alfa, asi que el fondo contra el que se lee no es la superficie
    /// del panel sino la superficie compuesta con ese tinte, un punto mas oscura. Medir contra la
    /// superficie pelada da un numero optimista y deja pasar justo el caso que fallaba: en tema
    /// claro las tintas heredadas de la interfaz se quedaban en 3,8.
    /// </summary>
    [Theory]
    [InlineData("Dark", "Color.Code.Added", "#3FB950", 0x22)]
    [InlineData("Dark", "Color.Code.Removed", "#D13A3A", 0x22)]
    [InlineData("Dark", "Color.Code.Faint", "#808080", 0x10)]
    [InlineData("Light", "Color.Code.Added", "#3FB950", 0x22)]
    [InlineData("Light", "Color.Code.Removed", "#D13A3A", 0x22)]
    [InlineData("Light", "Color.Code.Faint", "#808080", 0x10)]
    public void Las_tintas_del_diff_llegan_a_AA_sobre_su_fila_tenida(
        string theme, string key, string tintHex, int alpha)
    {
        Color row = Over(Surface(theme), Parse(tintHex), alpha);

        CodePalette.Contrast(Colour(theme, key), row).Should().BeGreaterThanOrEqualTo(
            AA, "«{0}» se lee sobre su fila en tema {1}", key, theme.ToLowerInvariant());
    }

    /// <summary>Compone un tinte con alfa sobre una superficie opaca, como hace WPF al pintar.</summary>
    private static Color Over(Color surface, Color tint, int alpha)
    {
        double a = alpha / 255.0;
        static byte Mix(byte under, byte over, double a) => (byte)Math.Round((under * (1 - a)) + (over * a));
        return Color.FromRgb(
            Mix(surface.R, tint.R, a), Mix(surface.G, tint.G, a), Mix(surface.B, tint.B, a));
    }

    // ================================================================ el andamiaje

    private static double Ajustado(string hex, Color surface)
        => CodePalette.Contrast(CodePalette.Fit(Parse(hex), surface), surface);

    private static Color Surface(string theme) => Colour(theme, "Color.Code.Surface");

    private static Color Ink(string theme) => Colour(theme, "Color.Code.Ink");

    /// <summary>Un color de la paleta, leído del XAML del repositorio.</summary>
    private static Color Colour(string theme, string key)
    {
        string xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Atalaya.App", "Themes", $"Palette.{theme}.xaml"));

        Match m = Regex.Match(xaml, $"<Color x:Key=\"{Regex.Escape(key)}\">\\s*(#[0-9A-Fa-f]{{6}})\\s*</Color>");
        m.Success.Should().BeTrue($"la paleta {theme} declara {key}");
        return Parse(m.Groups[1].Value);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        if (d == 0)
        {
            return 0;
        }

        return max == r ? 60 * ((((g - b) / d) + 6) % 6)
             : max == g ? 60 * (((b - r) / d) + 2)
             : 60 * (((r - g) / d) + 4);
    }

    private static double Saturation(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double d = max - min;
        return d == 0 ? 0 : d / (1 - Math.Abs((2 * l) - 1));
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
}
