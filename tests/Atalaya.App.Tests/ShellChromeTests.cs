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

        // Se cuenta lo VISIBLE, que es lo que la regla vigila: los atributos que llevan texto a
        // la pantalla. Un x:Class, un xmlns o una ruta de recurso («/assets/atalaya.ico», el
        // icono de la ventana y del aviso desde F6.4) llevan la palabra y no los lee nadie.
        // Contar el fichero entero hacía que esta regla fallara cada vez que alguien nombraba un
        // recurso, sin que la marca se hubiera escrito una segunda vez.
        var visible = Regex.Matches(markup, "(?:Text|Content|Title)=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .Where(v => v.Contains("Atalaya", StringComparison.OrdinalIgnoreCase))
            .ToList();

        visible.Should().OnlyContain(v => v == "Atalaya", "la marca se escribe entera o no se escribe");
        visible.Should().HaveCount(
            2,
            "el chrome de la ventana y la etiqueta de la barra de título, que son la MISMA marca");
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

    /// <summary>
    /// R1 §1 — <b>un indicador de proceso por cosa en proceso, y ninguno anónimo.</b>
    /// <para>
    /// El defecto: la barra llevaba un <c>ProgressRing</c> genérico colgado de <c>IsBusy</c> justo
    /// delante del de la sesión, así que con el hub sincronizando y una auditoría en marcha se
    /// veían <b>dos círculos girando pegados</b> delante de «Auditando … · unidad 2/2 · pasada 5»
    /// (captura del usuario del 2026-09-03). El segundo no decía qué estaba en proceso; el de al
    /// lado sí, porque lleva su propia línea.
    /// </para>
    /// <para>
    /// La regla se fija por CONSTRUCCIÓN y no por número: cada giro de la barra tiene que estar
    /// dentro de un control que además diga de qué es. Un indicador nuevo sin texto vuelve a
    /// romper esto, que es justo lo que se quiere que salte.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_spinner_in_the_status_bar_says_what_it_is_spinning_for()
    {
        string statusBar = StatusBar();

        // Cada giro cuelga de un Button que enseña su propia línea: el de la sesión y el del
        // arreglo asistido. Nada más gira en la barra.
        var spinners = Regex.Matches(statusBar, "<ui:ProgressRing").Count;
        spinners.Should().Be(2, "la sesión y el arreglo asistido; el genérico de IsBusy se fue");

        Regex.Matches(statusBar, @"<ui:ProgressRing[^>]*?/>\s*<TextBlock Text=""\{Binding (\w+)\}")
            .Select(m => m.Groups[1].Value)
            .Should().BeEquivalentTo(new[] { "SessionProgress", "FixProgress" },
                "un giro sin texto al lado es un giro que no dice de qué es");

        statusBar.Should().NotContain("{Binding IsBusy",
            "el estado de ocupado de la carcasa ya lo cuenta el piloto de sync, y con más detalle");
    }

    /// <summary>El trozo de XAML de la barra de estado, sin comentarios.</summary>
    private static string StatusBar()
    {
        string xaml = MainWindowXaml();
        int start = xaml.IndexOf("<!-- Status bar -->", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "la barra de estado sigue estando en la carcasa");
        return Markup(xaml[start..]);
    }
}
