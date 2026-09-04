using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-RELEASE §2 — Un tag mal escrito no puede contaminar el estampado de un build.
/// <para>
/// <b>El parte.</b> Alguien creó <c>V1.4.0</c>, con mayúscula. No disparó el workflow —que escucha
/// <c>v*</c>— pero sí llegó a GitHub, y para <c>git describe</c> era el tag más cercano. Los builds
/// de test salieron estampados <c>V1.4.0-dev+…</c> y el test de identidad, que exige un número,
/// tumbó el run de <c>v1.4.1</c>: <b>un tag mal escrito rompió la publicación del tag bien
/// escrito</b>. Cinco relanzamientos a mano.
/// </para>
/// <para>
/// <b>Por qué hay test y no basta con el de identidad.</b> El de identidad mira el binario que se
/// está ejecutando: solo se pone rojo cuando ya hay un tag malo cerca de <c>HEAD</c>, es decir, el
/// día de la publicación y en el runner. Si alguien quitase la normalización, todo seguiría verde
/// hasta el siguiente error de dedo — y volvería a costar un release averiguarlo. Éste corre la
/// lógica de verdad, contra tags de verdad, hoy.
/// </para>
/// </summary>
public sealed class VersionStampTests : IDisposable
{
    private readonly string _repo;

    public VersionStampTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "atalaya-stamp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);

        // El estampado se lee de `git describe` sobre la carpeta del propio targets, así que el
        // banco de pruebas es una copia de los ficheros REALES en un repositorio de usar y tirar.
        // Copiarlos, y no reimplementarlos, es lo que hace que este test valga para algo.
        string root = RepoRoot();
        File.Copy(Path.Combine(root, "Directory.Build.props"), Path.Combine(_repo, "Directory.Build.props"));
        File.Copy(Path.Combine(root, "Directory.Build.targets"), Path.Combine(_repo, "Directory.Build.targets"));
        File.WriteAllText(
            Path.Combine(_repo, "t.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
            </Project>
            """);

        Git("init -q .");
        Git("config user.email banco@atalaya.test");
        Git("config user.name banco");
        Git("add -A");
        Git("commit -qm base");
    }

    /// <summary>
    /// La regla: <b>el tag se normaliza, y si lo que queda no es un número de versión el tag se
    /// ignora</b>. Pase lo que pase, lo estampado empieza por un número — que es justo lo que el
    /// test de identidad exige y lo que el tag con mayúscula rompió.
    /// </summary>
    [Fact]
    public void Un_tag_mal_escrito_no_puede_contaminar_la_version_estampada()
    {
        // El caso que pasó: la mayúscula se quita, y el número sale intacto.
        Stamp("V1.4.0").Should().StartWith("1.4.0-dev+", "la «v» no es parte de la versión, se escriba como se escriba");

        // Y el caso general: lo que no es una versión no estampa nada — se cae al suelo del props.
        Stamp("v.1.0.1").Should().StartWith("1.0.0-dev+",
            "un tag que no es un número se ignora y manda el AtalayaFallbackVersion");

        // La regla entera, dicha como la exige IdentityTests: siempre un número delante.
        foreach (string tag in new[] { "V1.4.0", "v.1.0.1" })
        {
            Stamp(tag).Should().MatchRegex(@"^\d+\.\d+", $"el tag «{tag}» no puede tumbar una publicación");
        }
    }

    /// <summary>Estampa el proyecto de prueba con <paramref name="tag"/> como único tag del repo.</summary>
    private string Stamp(string tag)
    {
        foreach (string existing in Git("tag --list").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Git($"tag -d {existing.Trim()}");
        }

        Git($"tag {tag}");

        string log = Run("dotnet", $"msbuild \"{Path.Combine(_repo, "t.csproj")}\" -t:AtalayaStampDevelopmentVersion -nologo -v:n");
        Match stamped = Regex.Match(log, @"estampado como (?<v>\S+)");
        stamped.Success.Should().BeTrue($"el target tiene que decir qué estampó. Salida:\n{log}");
        return stamped.Groups["v"].Value;
    }

    private string Git(string arguments) => Run("git", arguments);

    private string Run(string exe, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return output;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        return dir!.FullName;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repo))
            {
                // El `.git` viene con ficheros de solo lectura; sin esto, el borrado falla.
                foreach (string file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_repo, recursive: true);
            }
        }
        catch (IOException)
        {
            // Un temporal que Windows todavía tiene abierto no es un fallo del test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
