using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-AVISO — una sola fuente para la versión, y una sola forma de escribirla.
/// <para>
/// El barrido de D-728 hizo esto con las URLs del repositorio: un test que recorre <c>src/</c> y
/// falla si vuelve a aparecer un literal. Las versiones necesitaban el mismo guardián y por la
/// misma razón — dos caminos que calculan «la versión» acaban discrepando, y el que discrepa es
/// siempre el que nadie mira.
/// </para>
/// </summary>
public sealed class VersionSourceTests
{
    private static DirectoryInfo Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        return dir!;
    }

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(Path.Combine(Root().FullName, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".cs" or ".xaml")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// La versión en ejecución se lee del ensamblado en <b>un solo fichero</b>: `AboutInfo`. Es
    /// donde BUGFIX-VERSION dejó la regla de qué atributo mirar y por qué, y un segundo sitio que
    /// lo hiciera «parecido» es exactamente cómo «Acerca de» y el aviso podrían volver a decir
    /// cosas distintas sobre el mismo binario.
    /// </summary>
    [Fact]
    public void Solo_AboutInfo_lee_la_version_del_ensamblado()
    {
        string[] señales =
        {
            "GetName().Version",
            "AssemblyInformationalVersionAttribute",
            "FileVersionInfo.GetVersionInfo",
        };

        var offenders = new List<string>();
        foreach (string file in SourceFiles())
        {
            if (Path.GetFileName(file) == "AboutInfo.cs")
            {
                continue;
            }

            foreach (string line in File.ReadLines(file))
            {
                // Los comentarios explican por qué NO se usa cada una; prohibirlos sería prohibir
                // dejar dicho el motivo. Lo que se persigue es la lectura de verdad.
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string señal in señales)
                {
                    if (line.Contains(señal, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "la versión en ejecución sale de AboutInfo y de ningún otro sitio: un segundo camino "
            + "es como el banner acabó anunciando un número que la decisión nunca usó");
    }

    /// <summary>
    /// Y una sola forma de ESCRIBIRLA. Hubo un segundo formateador (`SemanticVersion.Short`) que
    /// dejaba la 1.0.4 en «1.0»; el aviso lo usaba y anunciaba una versión inexistente. Si vuelve
    /// a nacer un formateador de versiones, que falle aquí.
    /// </summary>
    [Fact]
    public void No_hay_un_segundo_formateador_de_versiones()
    {
        typeof(SemanticVersion).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain("Short",
                "una versión se escribe entera; abreviarla ocultaba justo el dígito que cambiaba");

        var offenders = new List<string>();
        foreach (string file in SourceFiles())
        {
            // El único sitio donde una versión se compone a partir de sus piezas es su propio
            // ToString(); ahí es la definición, no una copia.
            if (Path.GetFileName(file) == "SemanticVersion.cs")
            {
                continue;
            }

            foreach (string line in File.ReadLines(file))
            {
                // Componer una versión a mano a partir de sus piezas es reinventar ToString().
                if (line.Contains("{Major}", StringComparison.Ordinal)
                    || line.Contains("{Minor}", StringComparison.Ordinal)
                    || line.Contains("{Patch}", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty("la única forma de escribir una versión es SemanticVersion.ToString()");
    }

    /// <summary>
    /// El banner no redacta su propio texto: lo toma del resultado del chequeo. Que quien decide
    /// sea quien redacta es lo que impide que el texto y la decisión discrepen.
    /// </summary>
    [Fact]
    public void El_banner_no_construye_su_propio_texto_de_version()
    {
        string source = File.ReadAllText(Path.Combine(
            Root().FullName, "src", "Atalaya.App", "ViewModels", "MainViewModel.cs"));

        source.Should().Contain("UpdateLabel = result.Headline");
        source.Should().NotContain("$\"Atalaya {", "el número no se vuelve a formatear en la interfaz");
    }

    /// <summary>
    /// Los dos sitios que enseñan la versión propia coinciden: «Acerca de» y lo que el chequeo usa
    /// para comparar salen del MISMO valor del ensamblado.
    /// </summary>
    [Fact]
    public void Acerca_de_y_el_chequeo_hablan_del_mismo_binario()
    {
        string raw = AboutInfo.CurrentVersion();

        AboutInfo.BaseVersion(raw).Should().NotBeNullOrEmpty();
        SemanticVersion.TryParse(AboutInfo.BaseVersion(raw))
            .Should().NotBeNull("lo que el chequeo compara sale de la misma lectura que «Acerca de»");
    }
}
