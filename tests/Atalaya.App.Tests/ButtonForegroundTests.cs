using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// Un <c>Style TargetType="Button"</c> SIN <c>BasedOn</c> no hereda del estilo implícito de
/// WPF-UI: su <c>Foreground</c> cae al de serie de WPF, que es <b>negro</b>. Sobre el fondo oscuro
/// del tema eso es texto invisible — medido en el render de V2: 0,0,0 sobre 32,32,32.
/// <para>
/// Pasó en la cabecera de módulo de F5.6 §1 y estaba latente en <c>RowLink</c> de V3, donde no se
/// veía solo porque cada <c>TextBlock</c> de dentro se pone su propio color. La regla es del
/// estilo, no de sus hijos: quien redefine la plantilla de un botón se queda también con la
/// responsabilidad de su color.
/// </para>
/// </summary>
public sealed class ButtonForegroundTests
{
    public static TheoryData<string> Views => new()
    {
        "InventoryView.xaml",
        "FindingsView.xaml",
        "FindingDetailView.xaml",
        "SessionView.xaml",
        "PortfolioView.xaml",
        "MetricsView.xaml",
        "SettingsView.xaml",
        "AccountView.xaml",
        "OnboardingView.xaml",
    };

    [Theory]
    [MemberData(nameof(Views))]
    public void Un_estilo_de_Button_sin_BasedOn_declara_su_Foreground(string view)
    {
        string markup = Markup(ViewXaml(view));

        foreach (Match style in Regex.Matches(markup, @"<Style\b[^>]*TargetType=""(?:\{x:Type )?Button\}?""[^>]*>"))
        {
            if (style.Value.Contains("BasedOn", StringComparison.Ordinal))
            {
                continue;   // hereda: el color viene del estilo base
            }

            Body(markup, style).Should().Contain(
                "Property=\"Foreground\"",
                $"un Style de Button sin BasedOn en {view} arranca con el Foreground NEGRO de WPF, "
                + "invisible sobre el tema oscuro");
        }
    }

    /// <summary>El cuerpo del <c>Style</c>, desde su etiqueta de apertura hasta su cierre.</summary>
    private static string Body(string markup, Match open)
    {
        int start = open.Index;
        int end = markup.IndexOf("</Style>", start, StringComparison.Ordinal);
        return end < 0 ? markup[start..] : markup[start..end];
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
