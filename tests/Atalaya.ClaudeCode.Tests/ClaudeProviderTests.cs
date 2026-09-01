using Atalaya.Agents;
using Atalaya.ClaudeCode;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// El driver de Claude Code por fuera: qué encuentra, qué le pide al CLI, y qué contesta cuando
/// algo falta (F14).
/// </summary>
public sealed class ClaudeProviderTests
{
    // ---------------------------------------------------------------- localizar el CLI

    /// <summary>
    /// La extensión importa, y se comprobó ejecutándolo: npm deja tres ficheros —<c>claude</c> sin
    /// extensión, <c>claude.ps1</c> y <c>claude.cmd</c>— y de los tres, el único que
    /// <c>Process.Start</c> sabe lanzar con <c>UseShellExecute=false</c> es el <c>.cmd</c>. El que
    /// no tiene extensión falla con «no es una aplicación válida para esta plataforma».
    /// </summary>
    [Fact]
    public void En_Windows_se_busca_el_cmd_que_es_el_unico_lanzable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ClaudeCliLocator.CandidateNames.Should().StartWith(new[] { "claude.cmd" });
        ClaudeCliLocator.CandidateNames.Should().NotContain("claude",
            "el shim sin extensión es un script sh: CreateProcess no lo ejecuta");
    }

    [Fact]
    public void Se_encuentra_el_CLI_recorriendo_el_PATH()
    {
        using var dir = new TempDir();
        string cli = Path.Combine(dir.Path, ClaudeCliLocator.CandidateNames[0]);
        File.WriteAllText(cli, "@echo off");

        ClaudeCliLocator.Resolve($"C:\\no-existe{Path.PathSeparator}{dir.Path}").Should().Be(cli);
    }

    [Fact]
    public void Sin_CLI_en_el_PATH_no_se_inventa_una_ruta()
    {
        using var dir = new TempDir();

        ClaudeCliLocator.Resolve(dir.Path).Should().BeNull();
    }

    /// <summary>Una entrada imposible del PATH no puede tumbar la búsqueda en las demás.</summary>
    [Fact]
    public void Una_entrada_corrupta_del_PATH_no_tumba_la_busqueda()
    {
        using var dir = new TempDir();
        string cli = Path.Combine(dir.Path, ClaudeCliLocator.CandidateNames[0]);
        File.WriteAllText(cli, "@echo off");

        ClaudeCliLocator.Resolve($"\"|<>*{Path.PathSeparator}{dir.Path}").Should().Be(cli);
    }

    // ---------------------------------------------------------------- «no disponible» se ve y se explica

    /// <summary>
    /// Sin CLI, el proveedor aparece como NO disponible con la instrucción exacta. Y la causa es
    /// <see cref="AgentProblem.CliMissing"/>, no «no autenticado»: son remedios distintos —instalar
    /// y hacer login— y darle a alguien el equivocado lo manda a buscar un login que aún no existe.
    /// </summary>
    [Fact]
    public async Task Sin_el_CLI_el_proveedor_no_esta_disponible_y_dice_que_hay_que_instalarlo()
    {
        var provider = new ClaudeCodeProvider("puente.exe", locator: () => null);

        AgentReadiness readiness = await provider.CheckAsync(CancellationToken.None);

        readiness.Ready.Should().BeFalse();
        readiness.Problem.Should().Be(AgentProblem.CliMissing);
        readiness.Message.Should().Contain("Instálalo").And.Contain("inicia sesión");
        readiness.Message.Should().Contain("no guarda ninguna credencial",
            "Atalaya usa el login del CLI y hay que decirlo donde se pide");
    }

    /// <summary>Y una sesión no se intenta siquiera: falla antes de gastar, con la causa.</summary>
    [Fact]
    public async Task Sin_el_CLI_una_auditoria_muere_con_causa_y_no_se_queda_colgada()
    {
        var provider = new ClaudeCodeProvider("puente.exe", locator: () => null);

        Func<Task> act = () => provider.AuditUnitAsync(
            new AuditUnitRequest("A.cs", "código", "prompt", TechStack.DotNet, AuditMode.Lotes,
                Array.Empty<ExistingFinding>()),
            new NullToolbox(),
            CancellationToken.None);

        (await act.Should().ThrowAsync<AuditorProviderException>())
            .Which.Problem.Should().Be(AgentProblem.CliMissing);
    }

    [Fact]
    public void Sin_el_CLI_tampoco_se_inventa_una_lista_de_modelos()
    {
        var provider = new ClaudeCodeProvider("puente.exe", locator: () => null);

        provider.Invoking(p => p.ListModelsAsync(CancellationToken.None))
            .Should().ThrowAsync<AuditorProviderException>();
    }

    // ---------------------------------------------------------------- estado de la sesión

    [Fact]
    public void Con_sesion_iniciada_se_reconoce_y_se_nombra_la_cuenta()
    {
        ClaudeAuthStatus? status = ClaudeAuthStatus.Parse(
            """{"loggedIn":true,"authMethod":"claude.ai","email":"alguien@example.com","subscriptionType":"max"}""");

        status!.LoggedIn.Should().BeTrue();
        status.Describe().Should().Be("alguien@example.com");
        ClaudeCodeHelp.Ready(status.Describe()).Should().Contain("alguien@example.com");
    }

    [Fact]
    public void Sin_sesion_iniciada_el_remedio_es_el_login_y_no_la_instalacion()
    {
        ClaudeAuthStatus.Parse("""{"loggedIn":false}""")!.LoggedIn.Should().BeFalse();
        ClaudeCodeHelp.NotLoggedIn.Should().Contain("no has iniciado sesión").And.Contain("`claude`");
    }

    /// <summary>
    /// Una respuesta que no es la esperada NO se lee como «no autenticado»: son cosas distintas y
    /// la segunda tiene un remedio que la primera no. Se devuelve null y arriba se dice el crudo.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("no soy json")]
    [InlineData("{}")]
    [InlineData("""{"loggedIn":"quiza"}""")]
    [InlineData("[]")]
    public void Una_respuesta_malformada_no_se_confunde_con_no_autenticado(string payload)
        => ClaudeAuthStatus.Parse(payload).Should().BeNull();

    // ---------------------------------------------------------------- la superficie que se le da al CLI

    /// <summary>
    /// <b>La salvaguarda entera de este flujo cabe en esta lista</b>, y por eso se comprueba aquí
    /// —igual que <c>BuildFixSessionConfig</c> en Copilot—: una salvaguarda que solo se puede ver
    /// teniendo una suscripción delante no se comprueba nunca.
    /// </summary>
    [Fact]
    public void El_CLI_se_lanza_sin_consola_sin_ficheros_y_solo_con_las_tools_de_Atalaya()
    {
        List<string> args = ClaudeCliRunner.BuildArguments(new ClaudeRun(
            "prompt",
            new[] { "mcp__atalaya__submit_finding", "mcp__atalaya__unit_done" },
            @"C:\tmp\mcp.json",
            "opus"));

        // Sin NINGUNA herramienta propia del CLI: ni Bash, ni Read, ni Write, ni WebFetch.
        args.Should().ContainInConsecutiveOrder("--tools", string.Empty);

        // Y solo las de Atalaya permitidas.
        args.Should().ContainInConsecutiveOrder(
            "--allowedTools", "mcp__atalaya__submit_finding,mcp__atalaya__unit_done");

        // Los servidores MCP del usuario NO se heredan: dos personas auditarían con superficies
        // distintas, y con permisos distintos.
        args.Should().Contain("--strict-mcp-config");

        // La sesión es de Atalaya: no ensucia el historial de `claude` del usuario.
        args.Should().Contain("--no-session-persistence");

        args.Should().ContainInConsecutiveOrder("--output-format", "stream-json");
        args.Should().ContainInConsecutiveOrder("--mcp-config", @"C:\tmp\mcp.json");
        args.Should().ContainInConsecutiveOrder("--model", "opus");
        args.Should().Contain("--print");
    }

    /// <summary>
    /// El prompt NO viaja como argumento, y ésa es la decisión más importante de la lista. En
    /// Windows el CLI es un <c>.cmd</c>, así que la línea de órdenes la reinterpreta
    /// <c>cmd.exe</c>: un prompt con código dentro —que es justo lo que Atalaya manda— trae
    /// <c>&amp;</c>, <c>|</c> y comillas, y acabaría troceado o ejecutando cualquier cosa. Además
    /// hay un tope de ~32 000 caracteres que una unidad normal se salta. Va por stdin.
    /// </summary>
    [Fact]
    public void El_prompt_no_viaja_nunca_en_la_linea_de_ordenes()
    {
        const string Nasty = "public void F() { if (a && b) { x |= 1; } } & echo tomado > z.txt";

        List<string> args = ClaudeCliRunner.BuildArguments(
            new ClaudeRun(Nasty, new[] { "mcp__atalaya__unit_done" }, "mcp.json", null));

        args.Should().NotContain(a => a.Contains("echo tomado"));
        args.Should().NotContain(a => a.Contains("public void"));
    }

    [Fact]
    public void Sin_modelo_configurado_no_se_le_impone_ninguno_al_CLI()
    {
        List<string> args = ClaudeCliRunner.BuildArguments(
            new ClaudeRun("p", new[] { "mcp__atalaya__unit_done" }, "mcp.json", null));

        args.Should().NotContain("--model", "vacío significa «que elija el CLI» (F5.15)");
    }

    /// <summary>
    /// El fichero de configuración MCP se escribe con un serializador. Pasarlo como cadena JSON en
    /// la línea de órdenes falló contra el CLI real —«MCP config is not a valid JSON»— porque las
    /// barras invertidas de una ruta de Windows no sobreviven al paso por la consola.
    /// </summary>
    [Fact]
    public void La_configuracion_MCP_se_escribe_a_fichero_con_las_rutas_bien_escapadas()
    {
        using var dir = new TempDir();

        string path = ClaudeCliRunner.WriteMcpConfig(
            dir.Path, @"C:\Program Files\Atalaya\Atalaya.Mcp.exe", "atalaya-mcp-deadbeef");

        string json = File.ReadAllText(path);
        System.Text.Json.JsonDocument.Parse(json);        // parsea, que es lo que fallaba

        json.Should().Contain("atalaya").And.Contain("stdio").And.Contain("atalaya-mcp-deadbeef");
        json.Should().Contain(@"C:\\Program Files\\Atalaya\\Atalaya.Mcp.exe",
            "el serializador escapa las barras; a mano era donde se rompía");
    }

    // ---------------------------------------------------------------- identidad

    [Fact]
    public void El_proveedor_se_identifica_para_quedar_escrito_en_el_hub()
    {
        var provider = new ClaudeCodeProvider("puente.exe", locator: () => null);

        provider.ProviderId.Should().Be("claude-code");
        provider.ProviderName.Should().Be("Claude Code");
    }

    /// <summary>El modelo se lee en CADA lectura, no se captura (BUGFIX-AJUSTES).</summary>
    [Fact]
    public void El_modelo_sigue_al_ajuste_sin_reconstruir_el_proveedor()
    {
        string configured = "opus";
        var provider = new ClaudeCodeProvider("puente.exe", modelProvider: () => configured);

        provider.ModelName.Should().Be("opus");

        configured = "sonnet";
        provider.ModelName.Should().Be("sonnet", "el mismo proveedor, sin reiniciar nada");

        configured = "";
        provider.ModelName.Should().BeNull("vacío es «que elija el CLI», no un modelo llamado «»");
    }

    // ---------------------------------------------------------------- ayudas

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "atalaya-claude-test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class NullToolbox : IAuditToolbox
    {
        public SubmitFindingResult SubmitFinding(SubmitFindingArgs args) => new(true);

        public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
            => new(findings.Select(_ => new SubmitFindingResult(true)).ToList());

        public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
            => new(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());

        public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations) => new(true);

        public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
        {
        }

        public string ReadSignatures(string path) => string.Empty;
    }
}
