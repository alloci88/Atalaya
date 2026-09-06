using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>F30 §3 — un componente de conversación, y uno solo.</b>
/// <para>
/// La regla nueva de esta fase, y la única que puede romperse <b>en silencio</b>: cada clase de
/// evento tiene su plantilla. Una clase sin plantilla no falla al compilar, no avisa y no revienta:
/// el hilo pinta el <c>ToString</c> del objeto en medio de la conversación, que es un texto que
/// nadie va a leer como un error. Y una vista que vuelva a declararse la suya reabre exactamente lo
/// que esta fase vino a cerrar — dos implementaciones de la misma burbuja, que acaban divergiendo
/// como divergen dos cálculos de la misma verdad (F5.14).
/// </para>
/// </summary>
public sealed class ConversationSurfaceTests
{
    /// <summary>
    /// Primera mitad: <b>ninguna clase de evento se queda sin plantilla</b>. Se monta el
    /// diccionario entero —tokens, paleta, estilos y la conversación, en el orden de
    /// <c>App.xaml</c>— y se pide la plantilla de cada clase: así salta también la que se renombre
    /// o se apoye en una pieza que alguien borre de <c>Styles.xaml</c> sin mirar quién la usaba.
    /// </summary>
    [Fact]
    public void Cada_clase_de_evento_tiene_exactamente_una_plantilla()
    {
        ViewLayout.OnUiThread(() =>
        {
            ResourceDictionary system = AppResources();

            foreach (ConversationKind kind in Enum.GetValues<ConversationKind>())
            {
                string key = ConversationTemplates.KeyFor(kind);

                system[key].Should().BeOfType<DataTemplate>(
                    $"«{kind}» se pintaría como el ToString del objeto sin su plantilla");
            }
        });
    }

    /// <summary>
    /// Y lo que el diccionario no puede comprobar solo: <b>toda clave que el componente pide
    /// existe</b>. Las plantillas resuelven sus <c>StaticResource</c> cuando se <i>pintan</i>, no
    /// cuando se cargan, así que una clave que no está no revienta al montar el diccionario: espera
    /// a la primera burbuja. Es la regla de <c>Ninguna_vista_pide_una_clave_que_no_existe</c>,
    /// extendida al único fichero de <c>Themes/</c> que declara plantillas.
    /// </summary>
    [Fact]
    public void El_componente_no_pide_ninguna_clave_que_no_exista()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(ThemesRoot(), "*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)"""))
            {
                declared.Add(m.Groups[1].Value);
            }
        }

        string body = File.ReadAllText(Path.Combine(ThemesRoot(), "Conversation.xaml"));
        var orphans = new List<string>();

        foreach (Match m in Regex.Matches(body, @"\{StaticResource ([A-Za-z0-9._]+)\}"))
        {
            if (!declared.Contains(m.Groups[1].Value))
            {
                orphans.Add(m.Groups[1].Value);
            }
        }

        orphans.Should().BeEmpty(
            "una clave que no existe revienta la burbuja al pintarla, no al cargar el diccionario: "
            + string.Join(", ", orphans));
    }

    /// <summary>
    /// Segunda mitad: <b>la burbuja se declara en UN sitio</b>. Ninguna vista puede volver a
    /// declararse una plantilla para una entrada de la conversación; si lo hace, la misma cosa se
    /// pinta de dos maneras según la pantalla en la que caiga, que es el estado del que sale esta
    /// fase.
    /// </summary>
    [Theory]
    [InlineData("SessionView.xaml")]
    [InlineData("AssistedFixView.xaml")]
    public void Ninguna_vista_se_declara_su_propia_burbuja(string view)
    {
        string xaml = Regex.Replace(ViewLayout.Xaml(view), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        foreach (string type in new[] { "ConversationEntry", "FixMessage", "FixQuestion", "ActivityEntry" })
        {
            xaml.Should().NotContain($"x:Type services:{type}",
                $"la plantilla de {type} vive en Themes/Conversation.xaml y en ningún otro sitio");
        }

        // Y tampoco a mano: una burbuja se reconoce porque nombra quién habla.
        xaml.Should().NotContain("{Binding Speaker}",
            "quien pinte una burbuja fuera del componente la está duplicando");
    }

    /// <summary>
    /// El diccionario de la aplicación, montado en el mismo orden que <c>App.xaml</c>: la
    /// conversación se apoya en las piezas de <c>Styles.xaml</c>, así que va detrás.
    /// <para>
    /// Se monta con <c>XamlReader</c> y no con <c>new ResourceDictionary { Source = … }</c> porque
    /// el esquema <c>pack://</c> lo resuelve el andamiaje de WPF, y en un proceso de tests ese
    /// andamiaje no existe hasta que alguien parsea markup. Levantar una <c>Application</c> lo
    /// arreglaría y no se hace: <c>Application.Current</c> es del proceso entero, y dos clases que
    /// levanten la suya cuelgan el conjunto (D-1017).
    /// </para>
    /// </summary>
    /// <summary>La carpeta del sistema visual, desde el repositorio.</summary>
    private static string ThemesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "Atalaya.App", "Themes");
    }

    private static ResourceDictionary AppResources()
    {
        // XamlReader solo resuelve un xmlns si el ensamblado que lo declara YA está cargado.
        _ = typeof(Wpf.Ui.Controls.Button);
        _ = typeof(Atalaya.App.Controls.Icons);
        _ = typeof(ConversationTemplates);

        string sources = string.Concat(
            new[] { "Converters", "Tokens", "Palette.Dark", "Styles", "Conversation" }
                .Select(n => $"<ResourceDictionary Source=\"pack://application:,,,/Atalaya;component/Themes/{n}.xaml\" />"));

        return (ResourceDictionary)XamlReader.Parse(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + ">"
            + "<ResourceDictionary.MergedDictionaries>"
            + sources
            + "</ResourceDictionary.MergedDictionaries></ResourceDictionary>");
    }
}
