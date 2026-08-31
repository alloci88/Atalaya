using Atalaya.Updater;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F11 — el cambio de carpeta y su vuelta atrás.
/// <para>
/// Es la pieza que no puede fallar: aquí es donde una actualización o deja la aplicación entera y
/// nueva, o entera y vieja, pero <b>nunca a medio camino</b>. Todos los finales se ejercitan sobre
/// carpetas de verdad, incluida la inyección de un fallo justo a mitad — que es el caso que nadie
/// prueba y el único que de verdad importa.
/// </para>
/// </summary>
public sealed class FolderSwapTests : IDisposable
{
    private readonly string _root;
    private readonly string _appDir;
    private readonly string _staged;
    private readonly string _backup;

    public FolderSwapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-swap", Guid.NewGuid().ToString("N"));
        _appDir = Path.Combine(_root, "Atalaya");
        _staged = Path.Combine(_appDir, ".atalaya-nuevo");
        _backup = Path.Combine(_appDir, ".atalaya-anterior");
        Directory.CreateDirectory(_appDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    // ------------------------------------------------------------------ el terreno

    /// <summary>Una instalación creíble: el exe, una dll, una subcarpeta y el fichero del despliegue.</summary>
    private void InstallOld(string marker = "vieja")
    {
        File.WriteAllText(Path.Combine(_appDir, "Atalaya.exe"), marker);
        File.WriteAllText(Path.Combine(_appDir, "Atalaya.dll"), marker);
        Directory.CreateDirectory(Path.Combine(_appDir, "runtimes", "win-x64", "native"));
        File.WriteAllText(Path.Combine(_appDir, "runtimes", "win-x64", "native", "copilot.exe"), marker);
        File.WriteAllText(Path.Combine(_appDir, "appsettings.deploy.json"), """{"hubUrl":"EL-DEL-DESPLIEGUE"}""");
    }

    private void Stage(string marker = "nueva", bool withExe = true)
    {
        Directory.CreateDirectory(_staged);
        if (withExe)
        {
            File.WriteAllText(Path.Combine(_staged, "Atalaya.exe"), marker);
        }

        File.WriteAllText(Path.Combine(_staged, "Atalaya.dll"), marker);
        File.WriteAllText(Path.Combine(_staged, "NUEVO.dll"), marker);
        Directory.CreateDirectory(Path.Combine(_staged, "runtimes", "win-x64", "native"));
        File.WriteAllText(Path.Combine(_staged, "runtimes", "win-x64", "native", "copilot.exe"), marker);
        File.WriteAllText(Path.Combine(_staged, "appsettings.deploy.json"), """{"hubUrl":"EL-DE-FABRICA"}""");
    }

    private SwapPlan Plan() => new()
    {
        AppDir = _appDir,
        StagedDir = _staged,
        BackupDir = _backup,
        MainExe = "Atalaya.exe",
    };

    private string Read(string relative) => File.ReadAllText(Path.Combine(_appDir, relative));

    // ------------------------------------------------------------------ el camino bueno

    [Fact]
    public void La_carpeta_acaba_con_la_version_nueva()
    {
        InstallOld();
        Stage();

        SwapResult result = new FolderSwap().Apply(Plan());

        result.Outcome.Should().Be(SwapOutcome.Actualizada);
        Read("Atalaya.exe").Should().Be("nueva");
        Read(Path.Combine("runtimes", "win-x64", "native", "copilot.exe")).Should().Be("nueva");
        File.Exists(Path.Combine(_appDir, "NUEVO.dll")).Should().BeTrue("lo que trae la versión nueva llega");
    }

    /// <summary>
    /// Lo que un despliegue corporativo editó a mano JUNTO al ejecutable sobrevive. Sin esto, cada
    /// actualización revertiría en silencio la configuración de la instalación — que es la clase
    /// de pérdida que nadie nota hasta que la aplicación deja de encontrar el hub.
    /// </summary>
    [Fact]
    public void El_fichero_de_despliegue_editado_sobrevive_a_la_actualizacion()
    {
        InstallOld();
        Stage();

        new FolderSwap().Apply(Plan()).Ok.Should().BeTrue();

        Read("appsettings.deploy.json").Should().Contain("EL-DEL-DESPLIEGUE");
    }

    /// <summary>
    /// La copia de lo viejo NO la borra el cambio: la borra la versión nueva cuando arranca. «Se
    /// conserva hasta que la nueva arranca bien» solo significa algo si quien la borra es la nueva.
    /// </summary>
    [Fact]
    public void La_version_anterior_se_conserva_tras_el_cambio()
    {
        InstallOld();
        Stage();

        new FolderSwap().Apply(Plan()).Ok.Should().BeTrue();

        File.Exists(Path.Combine(_backup, "Atalaya.exe")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_backup, "Atalaya.exe")).Should().Be("vieja");
    }

    [Fact]
    public void La_carpeta_de_preparacion_se_retira_al_acabar()
    {
        InstallOld();
        Stage();

        new FolderSwap().Apply(Plan()).Ok.Should().BeTrue();

        Directory.Exists(_staged).Should().BeFalse();
    }

    /// <summary>
    /// Las dos carpetas reservadas viven DENTRO de la instalación, así que el barrido tiene que
    /// saltárselas: moverlas a la copia de seguridad metería la instalación dentro de sí misma.
    /// </summary>
    [Fact]
    public void Las_carpetas_de_trabajo_no_se_mueven_a_si_mismas()
    {
        InstallOld();
        Stage();

        new FolderSwap().Apply(Plan()).Ok.Should().BeTrue();

        Directory.Exists(Path.Combine(_backup, ".atalaya-nuevo")).Should().BeFalse();
        Directory.Exists(Path.Combine(_backup, ".atalaya-anterior")).Should().BeFalse();
    }

    // ------------------------------------------------------------------ abortar sin tocar nada

    [Fact]
    public void Un_paquete_sin_el_ejecutable_no_toca_la_instalacion()
    {
        InstallOld();
        Stage(withExe: false);

        SwapResult result = new FolderSwap().Apply(Plan());

        result.Outcome.Should().Be(SwapOutcome.Intacta);
        result.Message.Should().Contain("Atalaya.exe");
        Read("Atalaya.exe").Should().Be("vieja");
        Read("appsettings.deploy.json").Should().Contain("EL-DEL-DESPLIEGUE");
    }

    [Fact]
    public void Sin_paquete_preparado_no_toca_la_instalacion()
    {
        InstallOld();

        SwapResult result = new FolderSwap().Apply(Plan());

        result.Outcome.Should().Be(SwapOutcome.Intacta);
        Read("Atalaya.exe").Should().Be("vieja");
        Directory.Exists(_backup).Should().BeFalse("ni siquiera se llegó a preparar la copia");
    }

    // ------------------------------------------------------------------ fallar a mitad

    /// <summary>
    /// El caso que justifica todo lo demás: revienta con la instalación ya desmontada y la nueva a
    /// medio poner. Tiene que quedar la de antes, ENTERA, y decirlo.
    /// </summary>
    [Fact]
    public void Un_fallo_al_instalar_restaura_la_version_anterior_entera()
    {
        InstallOld();
        Stage();

        var swap = new FolderSwap { Fault = step => { if (step == "instalar") { throw new IOException("disco lleno"); } } };
        SwapResult result = swap.Apply(Plan());

        result.Outcome.Should().Be(SwapOutcome.Restaurada);
        result.Detail.Should().Contain("disco lleno");

        Read("Atalaya.exe").Should().Be("vieja");
        Read("Atalaya.dll").Should().Be("vieja");
        Read(Path.Combine("runtimes", "win-x64", "native", "copilot.exe")).Should().Be("vieja");
        Read("appsettings.deploy.json").Should().Contain("EL-DEL-DESPLIEGUE");
        File.Exists(Path.Combine(_appDir, "NUEVO.dll")).Should().BeFalse("nada de la nueva se queda");
    }

    /// <summary>
    /// Y el fallo MÁS tardío: con lo nuevo ya movido a su sitio. La vuelta atrás tiene que retirar
    /// lo nuevo antes de devolver lo viejo, o chocarían por el nombre.
    /// </summary>
    [Fact]
    public void Un_fallo_al_final_tambien_restaura_la_version_anterior()
    {
        InstallOld();
        Stage();

        var swap = new FolderSwap { Fault = step => { if (step == "conservar") { throw new IOException("cortado"); } } };
        SwapResult result = swap.Apply(Plan());

        result.Outcome.Should().Be(SwapOutcome.Restaurada);
        Read("Atalaya.exe").Should().Be("vieja");
        Read("Atalaya.dll").Should().Be("vieja");
        File.Exists(Path.Combine(_appDir, "NUEVO.dll")).Should().BeFalse();
        Read("appsettings.deploy.json").Should().Contain("EL-DEL-DESPLIEGUE");
    }

    [Fact]
    public void Una_instalacion_restaurada_vuelve_a_poder_actualizarse()
    {
        InstallOld();
        Stage();
        new FolderSwap { Fault = step => { if (step == "instalar") { throw new IOException("x"); } } }
            .Apply(Plan()).Outcome.Should().Be(SwapOutcome.Restaurada);

        // Segundo intento, esta vez sin fallo: la carpeta preparada sigue completa.
        SwapResult second = new FolderSwap().Apply(Plan());

        second.Outcome.Should().Be(SwapOutcome.Actualizada);
        Read("Atalaya.exe").Should().Be("nueva");
        Read("appsettings.deploy.json").Should().Contain("EL-DEL-DESPLIEGUE");
    }

    /// <summary>Una copia de un intento anterior no puede confundirse con la buena.</summary>
    [Fact]
    public void Una_copia_de_seguridad_vieja_se_retira_antes_de_empezar()
    {
        InstallOld();
        Directory.CreateDirectory(_backup);
        File.WriteAllText(Path.Combine(_backup, "BASURA.txt"), "de un intento anterior");
        Stage();

        new FolderSwap().Apply(Plan()).Ok.Should().BeTrue();

        File.Exists(Path.Combine(_backup, "BASURA.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_backup, "Atalaya.exe")).Should().Be("vieja");
    }
}
