using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F11 — que el ritual de publicación siga produciendo lo que la actualización necesita.
/// <para>
/// La app se niega a instalar un paquete sin checksum, y no aparece el botón en una instalación
/// sin relevo. Las dos cosas las produce el workflow, que <b>no se puede ejecutar desde aquí</b>
/// (Actions solo corre en GitHub): lo que sí se puede es afirmar que sus pasos siguen ahí, para
/// que quitarlos rompa un test en vez de romper la actualización de todo el equipo tres semanas
/// después.
/// </para>
/// </summary>
public sealed class ReleasePipelineTests
{
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

    private static string Workflow()
        => File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));

    [Fact]
    public void El_workflow_publica_el_checksum_junto_al_zip()
    {
        string yaml = Workflow();

        yaml.Should().Contain("Get-FileHash", "el checksum se calcula en el propio workflow");
        yaml.Should().Contain("SHA256");
        yaml.Should().Contain("$zip.sha256", "el nombre que la app busca es el del zip más .sha256");
        yaml.Should().Contain("$env:SUM", "y se adjunta a la Release, no solo se calcula");
    }

    /// <summary>
    /// El sufijo que la app busca y el que el workflow escribe tienen que ser el mismo. Es la
    /// clase de acuerdo que se rompe en silencio: la Release sale bien y el botón deja de
    /// funcionar sin que nada falle.
    /// </summary>
    [Fact]
    public void El_sufijo_del_checksum_es_el_mismo_en_los_dos_lados()
    {
        SelfUpdateService.ChecksumSuffix.Should().Be(".sha256");
        Workflow().Should().Contain($"$zip{SelfUpdateService.ChecksumSuffix}");
    }

    [Fact]
    public void El_workflow_empaqueta_el_relevo_de_actualizacion()
    {
        string yaml = Workflow();

        yaml.Should().Contain("Atalaya.Updater/Atalaya.Updater.csproj");
        yaml.Should().Contain("PublishSingleFile=true", "el relevo se copia SOLO y tiene que bastarse");
        yaml.Should().Contain("--self-contained true");
        yaml.Should().Contain($"dist/{SelfUpdateService.RunnerExe}", "y se comprueba que de verdad viaja");
    }

    /// <summary>
    /// Un publish local tiene que producir la MISMA forma de carpeta que una Release. Si no, la
    /// única manera de probar la actualización sería publicando de verdad.
    /// </summary>
    [Fact]
    public void El_publish_local_tambien_deja_el_relevo()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "publish.ps1"));

        script.Should().Contain("Atalaya.Updater/Atalaya.Updater.csproj");
        script.Should().Contain("PublishSingleFile=true");
    }

    /// <summary>
    /// BUGFIX-RELEASE §3 — el log de tests sube <b>pase o falle</b>.
    /// <para>
    /// R1 vio caer un test intermitente en el runner y no pudo ponerle nombre: nadie guardaba el
    /// log del run. Se quedó en el backlog como «un test intermitente bajo carga» y volvió a costar
    /// cinco publicaciones. Es la clase de cosa que se rompe en silencio —quitar el paso no pone
    /// rojo nada, y el precio se paga meses después, el día que hace falta el log y no está—, así
    /// que se afirma aquí. Lo que importa es el `if: always()`: el run que hay que poder leer es
    /// justo el que ha fallado.
    /// </para>
    /// </summary>
    [Fact]
    public void El_log_de_tests_se_guarda_aunque_los_tests_fallen()
    {
        string yaml = Workflow();

        yaml.Should().Contain("--logger", "sin logger no hay .trx que subir");
        yaml.Should().Contain("trx", "el formato que trae el nombre del test y su pila");
        yaml.Should().Contain("artifacts/tests", "y un sitio conocido del que recogerlo");
        yaml.Should().Contain("if: always()",
            "el run que hay que poder leer es el que ha fallado, no el que ha ido bien");
    }

    /// <summary>El botón vive en el aviso de versión, con su progreso y su explicación.</summary>
    [Fact]
    public void El_aviso_de_version_ofrece_el_boton_de_actualizar()
    {
        string xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Atalaya.App", "MainWindow.xaml"));

        xaml.Should().Contain("InstallUpdateCommand");
        xaml.Should().Contain("InstallUpdateLabel");
        xaml.Should().Contain("CanInstallUpdate", "el botón se esconde cuando no se puede");
        xaml.Should().Contain("UpdateNotice", "y se dice por qué");
        xaml.Should().Contain("UpdateProgressText", "quien pulsó ve lo que pasa");
    }
}
