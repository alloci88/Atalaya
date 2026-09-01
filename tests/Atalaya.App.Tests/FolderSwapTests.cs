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

    private SwapPlan Plan(string syncedAdvice = "") => new()
    {
        AppDir = _appDir,
        StagedDir = _staged,
        BackupDir = _backup,
        MainExe = "Atalaya.exe",
        SyncedAdvice = syncedAdvice,
    };

    /// <summary>
    /// La receta tal y como la escribe la aplicación cuando detecta el cliente de sincronización.
    /// Aquí se pasa a mano: quien detecta es la App, y lo que se prueba es que el relevo la lleva
    /// hasta el mensaje de fallo.
    /// </summary>
    private const string Receta =
        "Atalaya está dentro de OneDrive: pausa la sincronización y reintenta.";

    /// <summary>
    /// La política de los tests: reintenta lo mismo que la de verdad, pero <b>sin esperar</b>. Un
    /// test que esperase seis segundos para comprobar que se rinde estaría probando Thread.Sleep.
    /// </summary>
    private static RetryPolicy Impaciente() => new(new[] { 0, 0, 0 }, _ => { });

    /// <summary>
    /// Un fichero cogido por otro proceso, que es lo que hace un cliente de sincronización
    /// mientras sube: <c>FileShare.None</c> hace de OneDrive. Ni se puede borrar ni se puede
    /// mover mientras el manejador viva.
    /// </summary>
    private static FileStream Retener(string path)
    {
        File.WriteAllText(path, "lo está subiendo OneDrive");
        return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

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

    // ------------------------------------------------------------- carpetas sincronizadas (BUGFIX-SYNC)

    /// <summary>
    /// El caso real: Atalaya bajo OneDrive, un <c>.atalaya-anterior</c> residual que el cliente de
    /// sincronización no suelta, y la actualización abortando contra él. Ahora se esquiva con un
    /// nombre libre — <b>un residuo de un intento viejo no puede impedir actualizar hoy</b>— y la
    /// huérfana queda anotada para retirarla cuando se pueda.
    /// </summary>
    [Fact]
    public void Un_respaldo_residual_bloqueado_se_esquiva_con_otro_nombre()
    {
        InstallOld();
        Stage();
        Directory.CreateDirectory(_backup);
        using FileStream held = Retener(Path.Combine(_backup, "RETENIDO.dll"));

        SwapResult result = new FolderSwap { Retries = Impaciente() }.Apply(Plan());

        result.Outcome.Should().Be(SwapOutcome.Actualizada);
        result.BackupDir.Should().Be($"{_backup}-2");
        result.Orphans.Should().ContainSingle().Which.Should().Be(_backup);

        Read("Atalaya.exe").Should().Be("nueva");
        Read("appsettings.deploy.json").Should().Contain("EL-DEL-DESPLIEGUE");
        File.ReadAllText(Path.Combine($"{_backup}-2", "Atalaya.exe")).Should().Be("vieja");
        File.Exists(Path.Combine(_backup, "RETENIDO.dll")).Should().BeTrue("la residual se deja donde está");
    }

    /// <summary>
    /// El residual bloqueado sigue siendo una carpeta dentro de la instalación: si el barrido no
    /// la saltara, intentaría meterla en la copia buena — y ahí sí que reventaría, porque está
    /// bloqueada, con la instalación ya desmontada.
    /// </summary>
    [Fact]
    public void El_respaldo_huerfano_no_se_mete_dentro_de_la_copia_nueva()
    {
        InstallOld();
        Stage();
        Directory.CreateDirectory(_backup);
        using FileStream held = Retener(Path.Combine(_backup, "RETENIDO.dll"));

        new FolderSwap { Retries = Impaciente() }.Apply(Plan()).Ok.Should().BeTrue();

        Directory.Exists(Path.Combine($"{_backup}-2", ".atalaya-anterior")).Should().BeFalse();
        Directory.Exists(_backup).Should().BeTrue("se queda fuera, en la carpeta, tal cual estaba");
    }

    /// <summary>
    /// El bloqueo TRANSITORIO, que es la forma normal del problema: el cliente de sincronización
    /// suelta el fichero a los pocos segundos. Antes eso era un aborto; ahora es una espera. La
    /// espera del reintento es, aquí, el momento exacto en que se suelta.
    /// </summary>
    [Fact]
    public void Un_bloqueo_transitorio_se_supera_reintentando()
    {
        InstallOld();
        Stage();
        Directory.CreateDirectory(_backup);
        FileStream held = Retener(Path.Combine(_backup, "RETENIDO.dll"));

        var suelta = new RetryPolicy(new[] { 0, 0, 0 }, _ => held.Dispose());
        SwapResult result = new FolderSwap { Retries = suelta }.Apply(Plan());

        result.Outcome.Should().Be(SwapOutcome.Actualizada);
        result.BackupDir.Should().Be(_backup, "el residuo se dejó borrar al reintentar");
        result.Orphans.Should().BeEmpty();
        Read("Atalaya.exe").Should().Be("nueva");
    }

    /// <summary>
    /// Y el bloqueo que NO se suelta: agotados los reintentos y los nombres, el aborto limpio de
    /// siempre — nada modificado— pero con la receta detrás. Un «acceso denegado» a secas no le
    /// dice a nadie qué hacer.
    /// </summary>
    [Fact]
    public void Un_bloqueo_persistente_aborta_limpio_y_dice_que_hacer()
    {
        InstallOld();
        Stage();

        var held = new List<FileStream>();
        try
        {
            for (int n = 1; n <= 5; n++)
            {
                string dir = n == 1 ? _backup : $"{_backup}-{n}";
                Directory.CreateDirectory(dir);
                held.Add(Retener(Path.Combine(dir, "RETENIDO.dll")));
            }

            SwapResult result = new FolderSwap { Retries = Impaciente() }.Apply(Plan(Receta));

            result.Outcome.Should().Be(SwapOutcome.Intacta);
            result.Message.Should().Contain("no se ha modificado nada");
            result.Message.Should().Contain("OneDrive").And.Contain("pausa la sincronización");
            result.Orphans.Should().HaveCount(5);

            Read("Atalaya.exe").Should().Be("vieja");
            Read("appsettings.deploy.json").Should().Contain("EL-DEL-DESPLIEGUE");
            File.Exists(Path.Combine(_appDir, "NUEVO.dll")).Should().BeFalse();
        }
        finally
        {
            foreach (FileStream stream in held)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Un bloqueo que aparece con el cambio ya empezado: se deshace, queda la de antes entera, y
    /// el mensaje receta igual. Es el mismo final de siempre — lo nuevo es que antes de rendirse
    /// se ha insistido.
    /// </summary>
    [Fact]
    public void Un_fichero_retenido_a_mitad_del_cambio_restaura_y_receta()
    {
        InstallOld();
        Stage();
        using FileStream held = File.Open(
            Path.Combine(_appDir, "Atalaya.dll"), FileMode.Open, FileAccess.Read, FileShare.None);

        SwapResult result = new FolderSwap { Retries = Impaciente() }.Apply(Plan(Receta));

        result.Outcome.Should().Be(SwapOutcome.Restaurada);
        result.Message.Should().Contain("OneDrive");
        Read("Atalaya.exe").Should().Be("vieja");
        Read("appsettings.deploy.json").Should().Contain("EL-DEL-DESPLIEGUE");
        File.Exists(Path.Combine(_appDir, "NUEVO.dll")).Should().BeFalse("nada de la nueva se queda");
    }

    /// <summary>Sin carpeta sincronizada no hay receta que dar: el mensaje va limpio.</summary>
    [Fact]
    public void Sin_carpeta_sincronizada_el_mensaje_no_receta_nada()
    {
        InstallOld();
        Stage();
        using FileStream held = File.Open(
            Path.Combine(_appDir, "Atalaya.dll"), FileMode.Open, FileAccess.Read, FileShare.None);

        SwapResult result = new FolderSwap { Retries = Impaciente() }.Apply(Plan());

        result.Outcome.Should().Be(SwapOutcome.Restaurada);
        result.Message.Should().NotContain("OneDrive").And.NotContain("sincronización");
    }

    /// <summary>
    /// Reintentar no puede convertirse en insistir para siempre: la política de producción son
    /// seis intentos en poco más de seis segundos, y ahí se acaba.
    /// </summary>
    [Fact]
    public void La_politica_de_produccion_insiste_unos_segundos_y_no_mas()
    {
        RetryPolicy.Default.Attempts.Should().Be(6);
        RetryPolicy.DefaultWaitsMs.Sum().Should().BeInRange(3_000, 10_000);
    }
}
