using System.Text.RegularExpressions;
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

    // ================================================================ el andamiaje

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
