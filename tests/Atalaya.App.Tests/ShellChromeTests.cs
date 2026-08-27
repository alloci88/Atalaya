using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.3 §2 y §3 — la carcasa de la ventana, leída del propio XAML.
/// <para>
/// <b>Por qué se prueba sobre el fichero.</b> Las dos son propiedades de la PLANTILLA, no del
/// view-model: «cuántas veces se lee Atalaya en la ventana» y «qué cuelga de la barra de estado»
/// no existen como estado observable que un test pueda interrogar. Instanciar la ventana exigiría
/// un hilo STA y un <c>Application</c> vivo para resolver los recursos de WPF-UI, que es mucho
/// aparato para fijar dos invariantes de maquetado. Leer el XAML las fija donde viven.
/// </para>
/// </summary>
public sealed class ShellChromeTests
{
    private static string MainWindowXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        string path = Path.Combine(dir!.FullName, "src", "Atalaya.App", "MainWindow.xaml");
        File.Exists(path).Should().BeTrue($"se esperaba la carcasa en {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Sin comentarios: lo que documenta la decisión no cuenta como interfaz.</summary>
    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    /// <summary>
    /// La marca sale UNA vez. Estaba dos: en la barra de título y como cabecera grande del rail
    /// de navegación, una encima de la otra en la misma esquina de la misma ventana.
    /// </summary>
    [Fact]
    public void The_brand_is_written_once_and_it_is_the_title_bar()
    {
        string markup = Markup(MainWindowXaml());

        markup.Should().NotContain("Text=\"ATALAYA\"",
            "la cabecera del rail era la segunda marca; el rail empieza por los items");

        // Las rutas de recurso no son marca ESCRITA: «/assets/atalaya.ico» es el icono de la
        // ventana y del aviso (F6.4), y nadie lo lee en pantalla. Se descuentan antes de contar,
        // porque lo que esta regla vigila es cuántas veces se ve la palabra, no cuántas veces
        // aparece en el fichero.
        string visible = Regex.Replace(markup, "pack://[^\"]+", string.Empty);

        // Las apariciones que quedan son atributos Title: el chrome de la ventana y la etiqueta
        // pequeña de la barra de título, que son la MISMA marca visible.
        MatchCollection brand = Regex.Matches(visible, "Atalaya", RegexOptions.IgnoreCase);
        MatchCollection titles = Regex.Matches(visible, "Title=\"Atalaya\"", RegexOptions.IgnoreCase);
        brand.Count.Should().Be(
            titles.Count + 1,
            "solo debería quedar la marca en los Title, más el x:Class del propio control");
    }

    /// <summary>
    /// La barra de estado se queda con lo ESTABLE: sync, cuenta y «Auditando…». Los avisos son
    /// efímeros y flotan sobre la página; colgarlos de la barra fue lo que hizo que dos «Sesión
    /// completada» se quedaran ahí para siempre.
    /// </summary>
    [Fact]
    public void The_status_bar_hosts_no_notifications()
    {
        string xaml = MainWindowXaml();
        int start = xaml.IndexOf("<!-- Status bar -->", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "la barra de estado sigue estando en la carcasa");

        string statusBar = xaml[start..];
        statusBar.Should().NotContain("{Binding Toasts}",
            "los avisos ya no son elementos de la barra de estado");

        // Y lo estable sí sigue ahí, que es la otra mitad de la regla.
        statusBar.Should().Contain("{Binding SyncHealth}");
        statusBar.Should().Contain("{Binding SessionProgress}");
        statusBar.Should().Contain("{Binding AccountLabel}");
    }
}
