using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.IO.Compression;
using System.Security.Cryptography;
using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F11 — actualizar desde la propia app.
/// <para>
/// Lo que se comprueba aquí es, sobre todo, <b>cuándo NO se actualiza</b>: con una sesión en
/// curso, en un build local, sin permisos de escritura, sin checksum publicado, y con un paquete
/// que no coincide con el suyo. Un actualizador se juzga por sus negativas, no por su camino
/// bueno: el camino bueno solo tiene que funcionar, y las negativas tienen que dejar la
/// instalación exactamente como estaba.
/// </para>
/// </summary>
public sealed class SelfUpdateTests : IDisposable
{
    private const string RepoUrl = "https://github.com/Applied-Advanced-Solutions-AAS/Atalaya";

    private readonly string _root;
    private readonly string _appDir;
    private readonly AppPaths _paths;
    private readonly GitHubAccountService _account;
    private readonly AgentBusyGate _busy = new();
    private readonly FixSnapshotStore _fixes;
    private readonly UpdateJournal _journal;
    private readonly List<ProcessStartInfo> _launched = new();

    public SelfUpdateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f11", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _appDir = Path.Combine(_root, "Atalaya");
        Directory.CreateDirectory(_appDir);
        // Una instalación creíble: el ejecutable y el relevo, que es lo que el servicio mira.
        File.WriteAllText(Path.Combine(_appDir, "Atalaya.exe"), "vieja");
        File.WriteAllText(Path.Combine(_appDir, SelfUpdateService.RunnerExe), "relevo");

        _account = TestFactory.Account(_paths);
        _account.Connect("token-de-la-cuenta", new GitHubUser(1, "alguien", "Alguien", null, null));
        _fixes = new FixSnapshotStore(_paths);
        _journal = new UpdateJournal(_paths);
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

    private SelfUpdateService Service(
        HttpStub? stub = null,
        string mine = "1.0.3",
        string? repoUrl = RepoUrl,
        string? appDir = null)
        => new(
            _paths,
            new DeployConfig { AppRepoUrl = repoUrl ?? string.Empty },
            _account,
            new GitHubApiClient((stub ?? new HttpStub()).Client()),
            _busy,
            _fixes,
            _journal,
            log: null,
            currentVersion: () => mine,
            appDirectory: appDir ?? _appDir,
            launch: info => _launched.Add(info),
            mainExeName: "Atalaya.exe");

    // ------------------------------------------------------------------ cuándo se ofrece

    /// <summary>
    /// Un `dist` de desarrollo no se ofrece para actualizar. Es la misma regla que ya enseña
    /// «Acerca de» (D-727): si el binario no viene del workflow, no sabe de qué release viene.
    /// </summary>
    [Fact]
    public void Un_build_local_no_ofrece_actualizar()
    {
        UpdateReadiness readiness = Service(mine: "1.0.3-dev+abc1234").CanOffer();

        readiness.CanUpdate.Should().BeFalse();
        readiness.Reason.Should().Contain("build local");
    }

    [Fact]
    public void Una_release_de_verdad_si_ofrece_actualizar()
        => Service(mine: "1.0.3").CanOffer().CanUpdate.Should().BeTrue();

    /// <summary>
    /// Un pre-release publicado por el workflow (<c>1.1.0-rc.1</c>) NO es un build local: se mira
    /// el identificador completo, no un «contiene dev». Misma lección que en BUGFIX-VERSION.
    /// </summary>
    [Fact]
    public void Un_prerelease_publicado_no_se_confunde_con_un_build_local()
        => Service(mine: "1.1.0-rc.1").CanOffer().CanUpdate.Should().BeTrue();

    /// <summary>
    /// El caso caro: interrumpir una auditoría en marcha tira trabajo YA pagado a Copilot. No se
    /// avisa ni se pregunta — no se ofrece.
    /// </summary>
    [Fact]
    public void Con_una_auditoria_en_curso_no_se_ofrece_actualizar()
    {
        _busy.TryEnter(AgentWork.Auditoria).Should().BeTrue();

        UpdateReadiness readiness = Service().CanOffer();

        readiness.CanUpdate.Should().BeFalse();
        readiness.Reason.Should().Contain("auditoría en curso");
    }

    [Fact]
    public void Con_un_arreglo_en_curso_tampoco_se_ofrece()
    {
        _busy.TryEnter(AgentWork.Arreglo).Should().BeTrue();

        Service().CanOffer().CanUpdate.Should().BeFalse();
    }

    [Fact]
    public void Terminada_la_sesion_vuelve_a_ofrecerse()
    {
        _busy.TryEnter(AgentWork.Auditoria);
        _busy.Exit(AgentWork.Auditoria);

        Service().CanOffer().CanUpdate.Should().BeTrue();
    }

    [Fact]
    public void Sin_cuenta_conectada_no_se_ofrece()
    {
        _account.Disconnect();

        UpdateReadiness readiness = Service().CanOffer();

        readiness.CanUpdate.Should().BeFalse();
        readiness.Reason.Should().Contain("cuenta");
    }

    [Fact]
    public void Sin_repositorio_declarado_no_se_ofrece()
        => Service(repoUrl: string.Empty).CanOffer().Reason.Should().Contain("appRepoUrl");

    [Fact]
    public void Sin_el_relevo_en_la_carpeta_no_se_ofrece()
    {
        File.Delete(Path.Combine(_appDir, SelfUpdateService.RunnerExe));

        UpdateReadiness readiness = Service().CanOffer();

        readiness.CanUpdate.Should().BeFalse();
        readiness.Reason.Should().Contain(SelfUpdateService.RunnerExe);
    }

    /// <summary>
    /// Un arreglo abierto NO impide actualizar —sus cambios viven en el clon, fuera de la carpeta
    /// de la aplicación— pero sí se dice, para que nadie se pregunte después si se los comimos.
    /// </summary>
    [Fact]
    public void Un_arreglo_abierto_avisa_pero_no_impide()
    {
        _fixes.Save(new FixSnapshotSet
        {
            SessionId = "S1",
            Slug = "app",
            CloneRoot = Path.Combine(_root, "clon"),
            StartedUtc = DateTimeOffset.UtcNow,
            Entries = { new FixSnapshotEntry("src/A.cs", "a.bak", true) },
        });

        UpdateReadiness readiness = Service().CanOffer();

        readiness.CanUpdate.Should().BeTrue();
        readiness.Warning.Should().Contain("arreglo abierto");
        readiness.Warning.Should().Contain("no los toca");
    }

    // ------------------------------------------------------------------ el camino bueno

    [Fact]
    public async Task Descarga_verifica_descomprime_y_cede_el_relevo()
    {
        (byte[] zip, string hash) = MakePackage("nueva");
        var stub = new HttpStub()
            .Json(ReleaseJson())
            .Text($"{hash}  Atalaya-v1.0.4-win-x64.zip\n")
            .Bytes(zip);

        var progress = new List<UpdateProgress>();
        UpdateStart result = await Service(stub).UpdateAsync(
            "v1.0.4", RepoUrl, new SyncProgress(progress), CancellationToken.None);

        result.HandedOff.Should().BeTrue();
        result.Message.Should().Contain("1.0.4");

        // La carpeta preparada existe y trae la versión nueva; la instalación NO se ha tocado —
        // eso es trabajo del relevo, que corre cuando este proceso ya no está.
        string staged = Path.Combine(_appDir, SelfUpdateService.StagedName);
        File.ReadAllText(Path.Combine(staged, "Atalaya.exe")).Should().Be("nueva");
        File.ReadAllText(Path.Combine(_appDir, "Atalaya.exe")).Should().Be("vieja");

        progress.Select(p => p.Phase).Should().Contain(
            new[] { UpdatePhase.Descargando, UpdatePhase.Verificando, UpdatePhase.Descomprimiendo });
    }

    /// <summary>
    /// El progreso avisa por punto porcentual, no por trozo leído. En la prueba real contra el
    /// paquete de 221 MB, sin freno salían 14.021 avisos —uno por cada 80 KB— y cada uno saltaba
    /// al hilo de la interfaz para mover una barra que no se movía.
    /// </summary>
    [Fact]
    public async Task El_progreso_de_descarga_no_inunda_la_interfaz()
    {
        (byte[] zip, string hash) = MakePackage();
        var stub = new HttpStub().Json(ReleaseJson()).Text(hash).Bytes(zip);

        var seen = new List<UpdateProgress>();
        await Service(stub).UpdateAsync("v1.0.4", RepoUrl, new SyncProgress(seen), CancellationToken.None);

        List<UpdateProgress> descarga = seen.Where(p => p.Phase == UpdatePhase.Descargando).ToList();
        descarga.Should().NotBeEmpty();
        descarga.Should().HaveCountLessThanOrEqualTo(101, "como mucho un aviso por punto porcentual");
        descarga.Select(p => (int)p.Percent!.Value).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// El relevo se lanza desde FUERA de la carpeta que va a sustituir, y con el pid de quien le
    /// llama: sin las dos cosas, o no puede reemplazarse a sí mismo o empieza antes de tiempo.
    /// </summary>
    [Fact]
    public async Task El_relevo_se_lanza_fuera_de_la_carpeta_y_con_el_pid()
    {
        (byte[] zip, string hash) = MakePackage();
        var stub = new HttpStub().Json(ReleaseJson()).Text(hash).Bytes(zip);

        await Service(stub).UpdateAsync("v1.0.4", RepoUrl, null, CancellationToken.None);

        _launched.Should().ContainSingle();
        ProcessStartInfo info = _launched[0];
        info.FileName.Should().StartWith(_paths.Update);
        info.FileName.Should().NotStartWith(_appDir);
        info.ArgumentList.Should().Contain("--pid");
        info.ArgumentList.Should().Contain(Environment.ProcessId.ToString());
        info.ArgumentList.Should().Contain("1.0.3");
        info.ArgumentList.Should().Contain("1.0.4");
    }

    [Fact]
    public async Task El_intento_queda_registrado_con_origen_y_destino()
    {
        (byte[] zip, string hash) = MakePackage();
        var stub = new HttpStub().Json(ReleaseJson()).Text(hash).Bytes(zip);

        await Service(stub).UpdateAsync("v1.0.4", RepoUrl, null, CancellationToken.None);

        UpdateAttempt last = _journal.Read().Last();
        last.From.Should().Be("1.0.3");
        last.To.Should().Be("1.0.4");
        last.Outcome.Should().Be(UpdateOutcome.Iniciada);
    }

    // ------------------------------------------------------------------ abortar sin tocar nada

    /// <summary>
    /// El caso central: lo descargado no es lo que la Release dice que es. Se aborta ANTES de
    /// tocar la instalación, se borra lo descargado, y se ofrece el camino manual.
    /// </summary>
    [Fact]
    public async Task Un_checksum_que_no_cuadra_aborta_con_la_instalacion_intacta()
    {
        (byte[] zip, _) = MakePackage("nueva");
        string mentira = new('a', 64);
        var stub = new HttpStub().Json(ReleaseJson()).Text(mentira).Bytes(zip);

        UpdateStart result = await Service(stub).UpdateAsync(
            "v1.0.4", RepoUrl, null, CancellationToken.None);

        result.HandedOff.Should().BeFalse();
        result.Message.Should().Contain("checksum");
        result.ReleaseUrl.Should().NotBeNullOrEmpty("el camino manual sigue estando");

        File.ReadAllText(Path.Combine(_appDir, "Atalaya.exe")).Should().Be("vieja");
        Directory.Exists(Path.Combine(_appDir, SelfUpdateService.StagedName)).Should().BeFalse();
        Directory.EnumerateFiles(_paths.Update, "*.zip").Should().BeEmpty("lo corrupto no se guarda");
        _launched.Should().BeEmpty("no se cede el relevo sobre algo sin verificar");

        UpdateAttempt last = _journal.Read().Last();
        last.Outcome.Should().Be(UpdateOutcome.Abortada);
        last.Detail.Should().Contain("checksum");
    }

    /// <summary>
    /// Una Release anterior a F11 no publica checksum. No se instala «confiando»: eso es
    /// exactamente lo que este trabajo vino a impedir.
    /// </summary>
    [Fact]
    public async Task Una_release_sin_checksum_no_se_instala()
    {
        var stub = new HttpStub().Json(ReleaseJson(withChecksum: false));

        UpdateStart result = await Service(stub).UpdateAsync(
            "v1.0.4", RepoUrl, null, CancellationToken.None);

        result.HandedOff.Should().BeFalse();
        result.Message.Should().Contain("verificar");
        File.ReadAllText(Path.Combine(_appDir, "Atalaya.exe")).Should().Be("vieja");
    }

    [Fact]
    public async Task Una_release_sin_zip_no_se_instala()
    {
        var stub = new HttpStub().Json("""
            {"tag_name":"v1.0.4","html_url":"https://github.com/o/r/releases/tag/v1.0.4","assets":[]}
            """);

        UpdateStart result = await Service(stub).UpdateAsync(
            "v1.0.4", RepoUrl, null, CancellationToken.None);

        result.HandedOff.Should().BeFalse();
        result.Message.Should().Contain("paquete");
    }

    /// <summary>
    /// Caso real en equipos corporativos: Atalaya bajo una carpeta donde el usuario no puede
    /// escribir. Se detecta ANTES de bajar 200 MB, y se dice qué hacer.
    /// </summary>
    [Fact]
    public async Task Sin_permiso_de_escritura_se_explica_y_se_ofrece_el_camino_manual()
    {
        // El caso corporativo de verdad: la instalación está COMPLETA —con su relevo y todo— pero
        // el usuario no puede escribir en su carpeta. Se deniega el permiso con una ACL real, que
        // es lo que hace una carpeta bajo «Archivos de programa»; simularlo con una ruta
        // imposible probaría otra cosa.
        DenyWrite(_appDir);
        try
        {
            UpdateStart result = await Service().UpdateAsync(
                "v1.0.4", RepoUrl, null, CancellationToken.None);

            result.HandedOff.Should().BeFalse();
            result.Message.Should().Contain("No se puede escribir");
            result.Message.Should().Contain("a mano");
            result.ReleaseUrl.Should().Be(RepoUrl);
            _journal.Read().Last().Outcome.Should().Be(UpdateOutcome.Abortada);
        }
        finally
        {
            AllowWrite(_appDir);
        }
    }

    /// <summary>Sin red no hay error críptico: hay una frase que dice qué pasa y qué mirar.</summary>
    [Fact]
    public async Task Sin_red_se_explica_en_una_frase_y_no_se_toca_nada()
    {
        var stub = new HttpStub().Throws(new HttpRequestException("sin ruta al host"));

        UpdateStart result = await Service(stub).UpdateAsync(
            "v1.0.4", RepoUrl, null, CancellationToken.None);

        result.HandedOff.Should().BeFalse();
        result.Message.Should().Contain("conexión");
        File.ReadAllText(Path.Combine(_appDir, "Atalaya.exe")).Should().Be("vieja");
    }

    /// <summary>Entre el aviso y el clic la Release pudo borrarse. Eso no es un error del programa.</summary>
    [Fact]
    public async Task Una_release_que_ya_no_esta_se_dice_sin_reventar()
    {
        var stub = new HttpStub().Status(System.Net.HttpStatusCode.NotFound);

        UpdateStart result = await Service(stub).UpdateAsync(
            "v1.0.4", RepoUrl, null, CancellationToken.None);

        result.HandedOff.Should().BeFalse();
        result.Message.Should().Contain("ya no está publicada");
    }

    /// <summary>
    /// Y si la sesión arrancó DESPUÉS de pintar el botón: se vuelve a mirar al pulsar, no solo al
    /// ofrecer. Lo que decide es el estado del momento en que se va a actuar.
    /// </summary>
    [Fact]
    public async Task Una_sesion_que_arranca_tras_pintar_el_boton_frena_la_actualizacion()
    {
        _busy.TryEnter(AgentWork.Auditoria);

        UpdateStart result = await Service().UpdateAsync("v1.0.4", RepoUrl, null, CancellationToken.None);

        result.HandedOff.Should().BeFalse();
        result.Message.Should().Contain("auditoría en curso");
        _journal.Read().Last().Outcome.Should().Be(UpdateOutcome.NoOfrecida);
    }

    /// <summary>Un paquete que no trae el ejecutable no se instala, aunque su checksum cuadre.</summary>
    [Fact]
    public async Task Un_paquete_verificado_pero_sin_el_ejecutable_no_se_instala()
    {
        (byte[] zip, string hash) = MakePackage(exeName: "OtraCosa.exe");
        var stub = new HttpStub().Json(ReleaseJson()).Text(hash).Bytes(zip);

        UpdateStart result = await Service(stub).UpdateAsync(
            "v1.0.4", RepoUrl, null, CancellationToken.None);

        result.HandedOff.Should().BeFalse();
        result.Message.Should().Contain(".exe");
        Directory.Exists(Path.Combine(_appDir, SelfUpdateService.StagedName)).Should().BeFalse();
    }

    // ------------------------------------------------------------------ el día después

    [Fact]
    public void El_parte_de_una_actualizacion_correcta_borra_la_copia_y_queda_registrado()
    {
        string backup = Path.Combine(_appDir, SelfUpdateService.BackupName);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "Atalaya.exe"), "vieja");
        WriteResult("Actualizada", backup);

        UpdateAftermath? aftermath = Service(mine: "1.0.4").TakeAftermath();

        aftermath!.Ok.Should().BeTrue();
        aftermath.Message.Should().Contain("1.0.4");
        Directory.Exists(backup).Should().BeFalse("la nueva ya arrancó: la copia deja de hacer falta");
        _journal.Read().Last().Outcome.Should().Be(UpdateOutcome.Completada);
        File.Exists(_paths.UpdateResultJson).Should().BeFalse("el parte se lee una sola vez");
    }

    /// <summary>
    /// Si hubo que restaurar, la copia NO se borra y el mensaje explica qué pasó: quien arranca es
    /// la versión de antes, y la copia es lo único que prueba que la sustitución se intentó.
    /// </summary>
    [Fact]
    public void El_parte_de_una_restauracion_se_cuenta_y_conserva_la_copia()
    {
        string backup = Path.Combine(_appDir, SelfUpdateService.BackupName);
        Directory.CreateDirectory(backup);
        WriteResult("Restaurada", backup, message: "La sustitución falló a mitad y se ha restaurado.");

        UpdateAftermath? aftermath = Service().TakeAftermath();

        aftermath!.Ok.Should().BeFalse();
        aftermath.Message.Should().Contain("restaurado");
        Directory.Exists(backup).Should().BeTrue();
        _journal.Read().Last().Outcome.Should().Be(UpdateOutcome.Restaurada);
    }

    [Fact]
    public void Sin_parte_no_se_cuenta_nada()
        => Service().TakeAftermath().Should().BeNull();

    // ------------------------------------------------------------------ el checksum, en detalle

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Un_fichero_que_no_trae_un_sha256_se_rechaza(string text)
        => FluentActions.Invoking(() => SelfUpdateService.ParseChecksum(text))
            .Should().Throw<InvalidOperationException>();

    /// <summary>Vale el hash a secas y vale el formato de <c>sha256sum</c>: son los dos que llegan.</summary>
    [Fact]
    public void El_checksum_se_lee_en_los_dos_formatos()
    {
        string hash = new('a', 64);

        SelfUpdateService.ParseChecksum(hash).Should().Be(hash);
        SelfUpdateService.ParseChecksum($"{hash}  Atalaya-v1.0.4-win-x64.zip\n").Should().Be(hash);
        SelfUpdateService.ParseChecksum($"  {hash.ToUpperInvariant()}  \r\n").Should().Be(hash);
    }

    // ------------------------------------------------------------------ herramientas

    private static string ReleaseJson(bool withChecksum = true)
    {
        string sum = withChecksum
            ? ""","name":"Atalaya-v1.0.4-win-x64.zip.sha256","size":80}"""
            : string.Empty;
        string assets = withChecksum
            ? """{"id":11,"name":"Atalaya-v1.0.4-win-x64.zip","size":128},{"id":12"""
            : """{"id":11,"name":"Atalaya-v1.0.4-win-x64.zip","size":128}""";

        return $$"""
            {"tag_name":"v1.0.4","html_url":"https://github.com/o/r/releases/tag/v1.0.4",
             "assets":[{{assets}}{{sum}}]}
            """;
    }

    /// <summary>Un zip de verdad con su SHA-256 de verdad: lo que verifica el servicio.</summary>
    private (byte[] Zip, string Hash) MakePackage(string marker = "nueva", string exeName = "Atalaya.exe")
    {
        string source = Path.Combine(_root, "paquete", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, exeName), marker);
        File.WriteAllText(Path.Combine(source, "Atalaya.dll"), marker);

        string zipPath = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(source, zipPath);
        byte[] bytes = File.ReadAllBytes(zipPath);
        File.Delete(zipPath);

        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private void WriteResult(string outcome, string backup, string message = "Atalaya se ha actualizado.")
    {
        Directory.CreateDirectory(_paths.Update);
        File.WriteAllText(_paths.UpdateResultJson, $$"""
            {
              "fromVersion": "1.0.3",
              "toVersion": "1.0.4",
              "outcome": "{{outcome}}",
              "message": "{{message}}",
              "detail": "",
              "appDir": "{{_appDir.Replace("\\", "\\\\")}}",
              "backupDir": "{{backup.Replace("\\", "\\\\")}}",
              "whenUtc": "2026-08-31T19:00:00.0000000Z"
            }
            """);
    }

    private static FileSystemAccessRule Rule(AccessControlType type) => new(
        WindowsIdentity.GetCurrent().User!,
        FileSystemRights.WriteData | FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        type);

    private static void DenyWrite(string dir)
    {
        var info = new DirectoryInfo(dir);
        DirectorySecurity acl = info.GetAccessControl();
        acl.AddAccessRule(Rule(AccessControlType.Deny));
        info.SetAccessControl(acl);
    }

    private static void AllowWrite(string dir)
    {
        var info = new DirectoryInfo(dir);
        DirectorySecurity acl = info.GetAccessControl();
        acl.RemoveAccessRule(Rule(AccessControlType.Deny));
        info.SetAccessControl(acl);
    }

    /// <summary>Un <see cref="IProgress{T}"/> que apunta en el hilo que llama, sin bomba de mensajes.</summary>
    private sealed class SyncProgress : IProgress<UpdateProgress>
    {
        private readonly List<UpdateProgress> _seen;

        public SyncProgress(List<UpdateProgress> seen) => _seen = seen;

        public void Report(UpdateProgress value) => _seen.Add(value);
    }
}
