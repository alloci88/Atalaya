using System.Text.RegularExpressions;
using System.Windows;
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
