using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.7 §4 — la regla transversal: <b>el feedback de una acción se da con un toast global, nunca
/// con un texto incrustado al fondo de un panel</b>.
/// <para>
/// El patrón ha aparecido cuatro veces y siempre con el mismo defecto: V4 («Abriendo en el
/// editor…», que además se quedaba pegado), V5 (residuos de sesión), el panel del ciclo de V2 y
/// el «Ajustes guardados» del pie de Ajustes — este último invisible sin bajar la página, justo
/// debajo del botón que lo provocaba. Un mensaje al fondo de un panel tiene dos problemas que no
/// se arreglan moviéndolo: no caduca, y está donde el usuario no mira.
/// </para>
/// <para>
/// <b>Las dos excepciones son deliberadas</b> y están comentadas en su XAML. No son feedback de
/// una acción: <c>AccountView</c> narra el flujo de dispositivo, que el usuario tiene que poder
/// leer mientras se va al navegador y vuelve (un toast de 8 segundos se lo llevaría); y
/// <c>SessionView</c> enseña el estado VIVO de la sesión que está corriendo, que debe permanecer
/// mientras dure. Este test las enumera para que sean una decisión, no un olvido.
/// </para>
/// </summary>
public sealed class EmbeddedStatusSweepTests
{
    /// <summary>Las diez vistas de la aplicación.</summary>
    private static readonly string[] AllViews =
    {
        "PortfolioView.xaml",
        "InventoryView.xaml",
        "FindingsView.xaml",
        "FindingDetailView.xaml",
        "SessionView.xaml",
        "MetricsView.xaml",
        "SettingsView.xaml",
        "AccountView.xaml",
        "ImportView.xaml",
        "OnboardingView.xaml",
    };

    /// <summary>
    /// Las excepciones razonadas, con su motivo. Añadir una entrada aquí obliga a escribir por qué
    /// ese texto NO es feedback de una acción.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["AccountView.xaml"] = "el hilo narrado del flujo de dispositivo, que sobrevive a un viaje al navegador",
        ["SessionView.xaml"] = "el estado vivo de la sesión en curso, que debe permanecer mientras dure",
    };

    public static TheoryData<string> Views
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string view in AllViews)
            {
                data.Add(view);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Ninguna_vista_incrusta_un_texto_de_estado_salvo_las_excepciones_razonadas(string view)
    {
        string markup = Markup(ViewXaml(view));
        bool binds = markup.Contains("StatusMessage", StringComparison.Ordinal);

        if (Allowed.TryGetValue(view, out string? why))
        {
            binds.Should().BeTrue(
                $"{view} está en la lista de excepciones por {why}; si ya no lo hace, sácala de la lista");
            return;
        }

        binds.Should().BeFalse(
            $"{view} incrusta un texto de estado. El feedback de una acción va por el toast global "
            + "(ToastCenter): caduca solo y se ve sin hacer scroll.");
    }

    /// <summary>
    /// Y el otro lado de la regla: los view-models que la aplicaron no vuelven a tener un
    /// <c>StatusMessage</c> que enlazar. Sin esto, el texto podría volver al XAML sin más.
    /// </summary>
    [Theory]
    [InlineData("Atalaya.App.ViewModels.SettingsViewModel")]
    [InlineData("Atalaya.App.ViewModels.InventoryViewModel")]
    [InlineData("Atalaya.App.ViewModels.ImportViewModel")]
    [InlineData("Atalaya.App.ViewModels.OnboardingViewModel")]
    [InlineData("Atalaya.App.ViewModels.FindingDetailViewModel")]
    public void Los_view_models_barridos_ya_no_exponen_StatusMessage(string typeName)
    {
        Type type = typeof(Atalaya.App.ViewModels.SettingsViewModel).Assembly.GetType(typeName)!;

        type.Should().NotBeNull();
        type.GetProperty("StatusMessage")
            .Should().BeNull($"{typeName} avisa por toast: no le queda dónde dejar un mensaje colgado");
    }

    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string ViewXaml(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        string path = Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", fileName);
        File.Exists(path).Should().BeTrue($"la vista {fileName} debería existir");
        return File.ReadAllText(path);
    }
}
