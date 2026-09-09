using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// UI-AUDIT-1 raíz 1 — <b>el recurso que no está</b> (P-12, UI-0007).
/// <para>
/// <b>De dónde viene.</b> Las paletas declaraban <c>Color.Ink.OnVivid</c> —la tinta que se escribe
/// encima de un color vivo, con su comentario explicando por qué se invierte con el tema— y era la
/// única clave de color a la que <b>no</b> le acompañaba su <c>SolidColorBrush</c>. Las seis
/// referencias <c>{DynamicResource Brush.Ink.OnVivid}</c> de <c>SessionView</c> y de la ficha no
/// resolvían y WPF caía al negro por defecto: cuatro pastillas por debajo de AA en el tema claro,
/// medidas en el píxel, y el arreglo escrito en el XAML sin llegar a pintarse nunca.
/// </para>
/// <para>
/// <b>Por qué se rompe en silencio, y por qué esto es una regla y no una forma.</b> Un
/// <c>DynamicResource</c> que no resuelve <b>no falla</b>: no revienta la vista como un
/// <c>StaticResource</c> ausente, no escribe en ningún log y no cambia nada que se pueda ver en
/// verde. Simplemente la propiedad se queda con su valor por defecto —negro, en un
/// <c>Foreground</c>— y solo se nota mirando una captura con la pregunta ya hecha. Las dos reglas
/// de aquí cubren la familia entera de ese fallo: <b>(a)</b> por cada color hay un pincel en las
/// DOS paletas, y <b>(b)</b> todo pincel que un XAML pide existe de verdad en los diccionarios
/// fusionados.
/// </para>
/// </summary>
public sealed class PaletteResourceTests
{
    /// <summary>
    /// <b>(a)</b> Por cada <c>Color.X</c> de una paleta hay un <c>Brush.X</c>, y en las dos.
    /// <para>
    /// Un color sin pincel es un color que nadie puede pintar: los recursos de una vista se piden
    /// por <c>Brush.*</c>, nunca por <c>Color.*</c>. Y tiene que estar en las dos porque la paleta
    /// se sustituye entera al cambiar de tema — una que declare un pincel de más deja la
    /// aplicación con una referencia colgando en cuanto el usuario cambia.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Cada_color_de_la_paleta_tiene_su_pincel(string theme)
    {
        string xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Atalaya.App", "Themes", $"Palette.{theme}.xaml"));

        var colores = Regex.Matches(xaml, @"<Color x:Key=""Color\.([A-Za-z0-9.]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        var pinceles = new HashSet<string>(
            Regex.Matches(xaml, @"<SolidColorBrush x:Key=""Brush\.([A-Za-z0-9.]+)""")
                .Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);

        colores.Should().NotBeEmpty("si no se encuentra ningún color, este test no está mirando nada");

        var huerfanos = colores.Where(c => !pinceles.Contains(c)).ToList();

        huerfanos.Should().BeEmpty(
            "la paleta {0} declara estos colores sin pincel, así que nadie puede pintarlos y "
            + "quien lo intente se queda con el valor por defecto SIN QUE NADA FALLE: {1}",
            theme, string.Join(", ", huerfanos));
    }

    /// <summary>
    /// <b>(b)</b> Todo <c>{DynamicResource Brush.*}</c> que aparece en un XAML de <c>Views/</c>,
    /// <c>Controls/</c> o <c>Themes/</c> resuelve contra los diccionarios fusionados de verdad.
    /// <para>
    /// No es un barrido de texto: se montan los diccionarios en el mismo orden que
    /// <c>App.xaml</c> y se le pregunta al resultado. Es lo único que distingue «la clave está
    /// escrita en algún fichero» de «la clave está donde la vista va a buscarla».
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Ningun_XAML_pide_un_pincel_que_no_existe(string theme)
    {
        var pedidos = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string dir in new[] { "Views", "Controls", "Themes" })
        {
            string root = Path.Combine(RepoRoot(), "src", "Atalaya.App", dir);
            foreach (string file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            {
                string body = Regex.Replace(
                    File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

                foreach (Match m in Regex.Matches(body, @"\{DynamicResource (Brush\.[A-Za-z0-9.]+)\}"))
                {
                    pedidos.Add(m.Groups[1].Value);
                }
            }
        }

        pedidos.Should().NotBeEmpty("si no se encuentra ninguna referencia, este test no está mirando nada");

        var faltan = new List<string>();

        ViewLayout.OnUiThread(() =>
        {
            var merged = Merged(theme);
            faltan.AddRange(pedidos.Where(k => !merged.Contains(k)));
        });

        faltan.Should().BeEmpty(
            "con la paleta {0} puesta, estos pinceles no resuelven; un DynamicResource que no "
            + "resuelve NO falla —la propiedad se queda con su valor por defecto— así que el "
            + "único aviso posible es éste: {1}",
            theme, string.Join(", ", faltan));
    }

    // ================================================ F37 §1.3 — la fila de carpeta

    /// <summary>
    /// <b>El nombre de una carpeta se puede LEER, en los dos temas.</b>
    /// <para>
    /// La fila de carpeta va del color de identidad de su aplicación (D-314) — no del ámbar, que
    /// es aviso (D-316), ni de una severidad, que es gravedad. Ese color existía solo como color
    /// de GRÁFICA: una línea de 1,6 px o un tramo de rosco, a los que WCAG pide 3:1. Escrito es
    /// texto pequeño y pide 4,5:1, y <b>dos de los seis no llegaban</b> sobre el fondo claro
    /// —turquesa a 3,89:1 y oliva a 3,79:1 sobre <c>Color.Surface</c>—: por eso
    /// <see cref="SeriesColor.Ink"/> tiene un paso propio para texto.
    /// </para>
    /// <para>
    /// <b>Por qué se rompe en silencio.</b> Un color ilegible no falla: pinta. Y aquí no lo
    /// sufriría quien elige el color —que mira el tema oscuro, donde los seis van sobrados— sino
    /// quien abre el inventario de la aplicación número dos o número cinco en tema claro. Sin esta
    /// medida, el reparto por hash de D-314 decide a quién le toca no poder leer sus carpetas.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_color_de_una_carpeta_se_lee_sobre_su_fondo(bool dark)
    {
        var palette = Palette(dark ? "Dark" : "Light");
        string[] surfaces = { "Color.Bg", "Color.Surface", "Color.Surface2" };
        var flojos = new List<string>();

        foreach (SeriesColor color in SeriesPalette.Steps)
        {
            foreach (string surface in surfaces)
            {
                double ratio = Contrast(color.Ink(dark), palette[surface]);
                if (ratio < 4.5)
                {
                    flojos.Add($"{color.Name} ({color.Ink(dark)}) sobre {surface} = {ratio:0.00}:1");
                }
            }
        }

        flojos.Should().BeEmpty(
            "en tema {0} estas carpetas no se leerían, y a quién le toca lo decide un hash: {1}",
            dark ? "oscuro" : "claro", string.Join(" · ", flojos));
    }

    /// <summary>
    /// <b>Y el paso de texto sigue siendo el MISMO color, no otro</b>: el de gráfica se queda
    /// intacto —lo pintan roscos, líneas y puntos desde F5.9— y el de texto es un escalón de la
    /// misma familia. Se le pide estar a menos de 15 de su color de gráfica (más lejos ya sería
    /// otra identidad) y, con la vara de F35-2, a <b>ΔE ≥ 20,1</b> de todo lo reservado.
    /// <para>
    /// <b>La vara se aplica a los pasos que F37 elige, no a los seis colores de aplicación.</b> No
    /// es una rebaja: es que esos seis son de D-314/D-315 y son ANTERIORES a la vara de F35-2, que
    /// nació para una familia nueva. Medido de paso, y se dice porque nadie lo había dicho:
    /// <b>«violeta» (<c>#6E56CF</c>) ya está a ΔE 18,6 de «nuevos» del flujo de hallazgos
    /// (<c>#8E44AD</c>)</b>, por debajo de esa vara y desde mucho antes de esta fase. Moverlo
    /// repintaría la identidad de una aplicación y las gráficas donde sale, que es exactamente lo
    /// que N-6 no autoriza; queda anotado en el BACKLOG. Lo que sí puede exigirse aquí es que los
    /// pasos que esta fase estrena no empeoren nada.
    /// </para>
    /// </summary>
    [Fact]
    public void El_paso_de_texto_de_una_app_es_su_color_y_no_pisa_nada_reservado()
    {
        var reservados = new List<(string Name, string Hex)>
        {
            ("crítica", SeverityPalette.Critica), ("alta", SeverityPalette.Alta),
            ("media", SeverityPalette.Media), ("baja", SeverityPalette.Baja),
            ("nuevos", FlowPalette.New.Light), ("resueltos", FlowPalette.Resolved.Light),
            ("activos", FlowPalette.Alive.Light),
            ("auditoría", ActionPalette.Auditoria.Light), ("verificación", ActionPalette.Verificacion.Light),
            ("arreglo", ActionPalette.Arreglo.Light), ("gestión", ActionPalette.Gestion.Light),
        };

        foreach (Match m in Regex.Matches(
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atalaya.App", "Themes", "Palette.Light.xaml")),
            @"<Color x:Key=""Color\.(?:Success|Warning|Danger)\.(?:Fill|Ink)"">\s*(#[0-9A-Fa-f]{6})\s*</Color>"))
        {
            reservados.Add(("estado", m.Groups[1].Value));
        }

        var elegidos = SeriesPalette.Steps.Where(c => c.LightInk is not null).ToList();

        elegidos.Should().HaveCount(
            2, "si un día ninguno tiene paso propio, este test no está mirando nada");

        foreach (SeriesColor color in elegidos)
        {
            string ink = color.Ink(dark: false);

            Distance(ink, color.Light).Should().BeLessThan(
                15, "el paso de texto de «{0}» tiene que seguir siendo su color, no otro", color.Name);

            foreach ((string name, string hex) in reservados)
            {
                Distance(ink, hex).Should().BeGreaterThanOrEqualTo(
                    20.1,
                    "«{0}» escrito ({1}) queda a {2:0.0} de «{3}» ({4}), y dos colores tan cerca se "
                    + "confunden: una carpeta no puede leerse como una gravedad ni como un estado",
                    color.Name, ink, Distance(ink, hex), name, hex);
            }

            foreach (SeriesColor otra in SeriesPalette.Steps.Where(c => c.Name != color.Name))
            {
                Distance(ink, otra.Light).Should().BeGreaterThanOrEqualTo(
                    20.1, "«{0}» y «{1}» tienen que seguir distinguiéndose", color.Name, otra.Name);
            }
        }
    }

    /// <summary>La razón de contraste de WCAG 2.1, igual que en <c>PaletteContrastTests</c>.</summary>
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

        int Byte(int start) => int.Parse(
            hex.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return (0.2126 * C(Byte(1))) + (0.7152 * C(Byte(3))) + (0.0722 * C(Byte(5)));
    }

    /// <summary>Distancia CIEDE76, la misma vara que F35-2.</summary>
    private static double Distance(string a, string b)
    {
        (double L1, double A1, double B1) = Lab(a);
        (double L2, double A2, double B2) = Lab(b);
        return Math.Sqrt(((L1 - L2) * (L1 - L2)) + ((A1 - A2) * (A1 - A2)) + ((B1 - B2) * (B1 - B2)));
    }

    private static (double L, double A, double B) Lab(string hex)
    {
        double Lin(int start)
        {
            double c = int.Parse(hex.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        double r = Lin(1), g = Lin(3), b = Lin(5);
        double x = ((r * 0.4124) + (g * 0.3576) + (b * 0.1805)) / 0.95047;
        double y = (r * 0.2126) + (g * 0.7152) + (b * 0.0722);
        double z = ((r * 0.0193) + (g * 0.1192) + (b * 0.9505)) / 1.08883;
        return ((116 * F(y)) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116);
    }

    /// <summary>Los `Color` de una paleta, leídos del XAML del repositorio.</summary>
    private static Dictionary<string, string> Palette(string theme)
        => Regex.Matches(
                File.ReadAllText(Path.Combine(
                    RepoRoot(), "src", "Atalaya.App", "Themes", $"Palette.{theme}.xaml")),
                @"<Color x:Key=""([^""]+)"">\s*(#[0-9A-Fa-f]{6})\s*</Color>")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    /// <summary>
    /// Los diccionarios de la aplicación, fusionados EN EL ORDEN DE <c>App.xaml</c> — WPF-UI
    /// incluido, porque `Styles.xaml` extiende sus estilos implícitos y sin ellos ni siquiera
    /// carga. Montarlos aquí tiene un segundo efecto que vale por sí solo: si alguien rompe
    /// `Styles.xaml`, esto se entera sin abrir la aplicación.
    /// <para>
    /// La fusión se declara EN XAML y no se construye a mano: sin una <c>Application</c> viva, el
    /// esquema <c>pack:</c> no tiene quién lo sirva y asignar <c>Source</c> desde código revienta.
    /// Es el mismo truco que ya usa <see cref="ViewLayout"/>.
    /// </para>
    /// </summary>
    private static ResourceDictionary Merged(string theme)
    {
        _ = typeof(Atalaya.App.Controls.Icons);
        _ = typeof(Wpf.Ui.Controls.Button);

        string fuentes = string.Concat(
            new[] { "Converters", "Tokens", $"Palette.{theme}", "Styles" }.Select(
                n => $"<ResourceDictionary Source=\"pack://application:,,,/Atalaya;component/Themes/{n}.xaml\" />"));

        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:ui=\"clr-namespace:Wpf.Ui.Markup;assembly=Wpf.Ui\">"
            + "<ResourceDictionary.MergedDictionaries>"
            + "<ui:ThemesDictionary Theme=\"Dark\" /><ui:ControlsDictionary />"
            + fuentes + "</ResourceDictionary.MergedDictionaries>"
            + "</ResourceDictionary>");
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
