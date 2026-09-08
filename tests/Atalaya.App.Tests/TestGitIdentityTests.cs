using System.Diagnostics;
using Atalaya.Tests;
using FluentAssertions;
using LibGit2Sharp;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>OMPT-BUGFIX-CI — el arnés no depende de la máquina.</b>
/// <para>
/// La regla que protege, en una frase: <b>un repositorio creado por la fábrica de los tests lleva
/// su identidad de git en la config LOCAL</b>, así que lo que commitea un test sale con el mismo
/// autor en el puesto de quien desarrolla y en un runner limpio.
/// </para>
/// <para>
/// <b>Qué se rompería en silencio sin este test</b> (N-5): volver a crear un repositorio temporal
/// con <c>Repository.Init</c> a pelo. No falla en el puesto de nadie —la identidad global lo
/// tapa—, y reaparece semanas después como diez tests rojos en una publicación, que es exactamente
/// como se descubrió. Aquí se cae en el sitio y con el nombre.
/// </para>
/// <para>
/// <b>Por qué se comprueba con el CLI y con la config local, y no con una variable de entorno.</b>
/// Está medido en esta misma fase: libgit2 <b>no</b> mira <c>GIT_CONFIG_GLOBAL</c> ni
/// <c>GIT_CONFIG_SYSTEM</c> —solo el CLI de git los respeta—, así que ninguna variable puede cegar
/// a los dos motores a la vez y un test que se apoyara en ella probaría media casa. Se comprueban
/// las dos mitades que sí valen en cualquier máquina: la config LOCAL, que es de donde salen ambos
/// motores, y un <c>git commit</c> de verdad con la global anulada, que es el runner.
/// </para>
/// </summary>
public sealed class TestGitIdentityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "atalaya-arnes-git", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Limpieza best-effort: un handle de git retenido no puede tumbar un test.
        }
    }

    /// <summary>
    /// Las DOS puertas por las que un test crea un repositorio —<see cref="TestGit.Init"/> y
    /// <see cref="TestFactory.MakeClone"/>— dejan la identidad puesta en local, y con ella se
    /// commitea aunque la global de la máquina no exista.
    /// </summary>
    [Theory]
    [InlineData("init")]
    [InlineData("clone")]
    public void Un_repo_de_la_fabrica_trae_su_identidad_y_commitea_sin_global(string puerta)
    {
        string folder = Path.Combine(_root, puerta);
        if (puerta == "init")
        {
            Directory.CreateDirectory(folder);
            TestGit.Init(folder);
        }
        else
        {
            TestFactory.MakeClone(folder, "https://example.invalid/org/app.git");
        }

        // 1) Está en la config LOCAL del repositorio, que es la que ganan los dos motores.
        using (var repo = new Repository(folder))
        {
            repo.Config.Get<string>("user.name", ConfigurationLevel.Local)!.Value
                .Should().Be(TestGit.Name);
            repo.Config.Get<string>("user.email", ConfigurationLevel.Local)!.Value
                .Should().Be(TestGit.Email);
        }

        // 2) Y con la global anulada —el runner— el CLI commitea igual, con ESE autor. Es el
        //    desenlace exacto que se caía: sin esto, git pide «tell me who you are» y aborta.
        File.WriteAllText(Path.Combine(folder, "A.cs"), "class A { }\r\n");
        Git(folder, "add -A").ExitCode.Should().Be(0);

        (int code, string output) = Git(folder, "commit -m alta");
        code.Should().Be(0, output);

        // Sin espacios en el formato: los argumentos van en una sola cadena y git recibiría
        // «<%ae>» como si fuera una revisión.
        Git(folder, "log -1 --format=%an|%ae").Output.Trim()
            .Should().Be($"{TestGit.Name}|{TestGit.Email}",
                "el autor sale del repositorio, no de quien esté ejecutando los tests");
    }

    /// <summary>
    /// git con la identidad global y la del sistema APAGADAS, que es lo que hay en un runner
    /// recién creado. <c>NUL</c> es el dispositivo nulo de Windows: un fichero de config vacío.
    /// </summary>
    private static (int ExitCode, string Output) Git(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = "NUL";

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return (process.ExitCode, output);
    }
}
