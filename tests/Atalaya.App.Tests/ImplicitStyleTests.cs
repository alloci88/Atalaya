using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// Un <c>Style</c> con <c>TargetType</c> de un control de WPF-UI y SIN <c>BasedOn</c> no extiende
/// el estilo implícito de la librería: lo <b>sustituye</b>. El control se queda sin plantilla
/// propia — sin fondo, sin borde y sin relleno— y lo que se ve es una tarjeta que ya no parece
/// una tarjeta y un contenido pegado a los bordes de la página.
/// <para>
/// Pasó en el panel de métricas de F5.9: <c>Style x:Key="Block" TargetType="ui:Card"</c> se puso
/// solo para compartir un margen y se llevó por delante el relleno de los cuatro bloques de
/// gráficas — el interruptor «Acumulado» acabó tocando la barra de desplazamiento. Es la misma
/// trampa que <see cref="ButtonForegroundTests"/> vigila en los <c>Button</c>, y por la misma
/// razón: quien redefine un estilo hereda la responsabilidad de todo lo que ese estilo traía.
/// </para>
/// </summary>
public sealed class ImplicitStyleTests
{
    /// <summary>
    /// La deuda YA CONTRAÍDA, con su motivo. No es una dispensa: es la lista de sitios donde el
    /// mismo fallo sigue en pie, escrita para que se vea en vez de para que se olvide. Quitar una
    /// entrada de aquí es arreglar el estilo; añadir una obliga a explicar por qué se acepta.
    /// </summary>
    private static readonly Dictionary<string, string> Pending = new()
    {
        ["FindingDetailView.xaml"] =
            "«SideAction» (ui:Button) redefine solo alineación y margen y se lleva por delante la "
            + "plantilla del botón, igual que el «Block» de F5.9 §2. Es anterior a esta tanda y "
            + "F5.9 tenía prohibido tocar otras vistas: se arregla cuando se toque esa vista",
    };

    /// <summary>Todas las vistas, más la carcasa: la regla no es de una página.</summary>
    public static TheoryData<string> Markup
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string file in Directory.GetFiles(ViewsDir(), "*.xaml"))
            {
                data.Add(Path.GetFileName(file));
            }

            data.Add(Path.Combine("..", "MainWindow.xaml"));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Markup))]
    public void Un_estilo_de_un_control_de_WPF_UI_extiende_el_implicito_en_vez_de_sustituirlo(string file)
    {
        string markup = Strip(File.ReadAllText(Path.Combine(ViewsDir(), file)));

        var offenders = Regex
            .Matches(markup, @"<Style\b[^>]*TargetType=""(?:\{x:Type )?ui:(?<control>\w+)\}?""[^>]*>")
            .Where(m => !m.Value.Contains("BasedOn", StringComparison.Ordinal))
            .Select(m => m.Groups["control"].Value)
            .ToList();

        if (Pending.TryGetValue(file, out string? why))
        {
            offenders.Should().NotBeEmpty(
                $"{file} está en la lista de deuda por {why}; si ya está arreglado, sácala de la lista");
            return;
        }

        offenders.Should().BeEmpty(
            $"un Style de ui:{string.Join("/", offenders)} sin BasedOn en {file} SUSTITUYE al estilo "
            + "implícito de WPF-UI: el control pierde plantilla, fondo, borde y relleno");
    }

    private static string Strip(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string ViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        return Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views");
    }
}
