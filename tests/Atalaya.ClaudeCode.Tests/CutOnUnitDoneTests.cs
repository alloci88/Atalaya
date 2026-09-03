using System.Text;
using System.Text.Json.Nodes;
using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// F21 — CORTAR LA PASADA EN <c>unit_done</c> SIN PERDER LAS CUENTAS.
/// <para>
/// Cada pasada terminaba pagando una llamada que no hace nada: después del resultado de una
/// herramienta el modelo tiene que contestar, y contestar es una llamada entera con el prompt
/// dentro. F20 la midió en <b>29.786 tokens de escritura de caché</b> —cerca del 70 % del coste de
/// entrada de la pasada—. F19 ya intentó quitarla matando el proceso y lo dejó fuera con razón
/// (D-865): sin el evento final del CLI, la sesión declaraba 43 tokens de salida donde se
/// consumieron 51.451.
/// </para>
/// <para>
/// Lo que cambia aquí NO es la voluntad de cortar: es que el corte ya no cuesta las cuentas. Dos
/// piezas, y las dos se fijan abajo. <b>La retención</b> impide que el CLI llegue a MANDAR la
/// petición siguiente —una enviada y cortada a medias se factura igual y encima no se registra—, y
/// <b>la interrupción</b> (en vez de matar) deja que el CLI emita su evento final con el consumo
/// completo.
/// </para>
/// </summary>
public sealed class CutOnUnitDoneTests
{
    // ------------------------------------------------------- la retención del relay MCP

    /// <summary>
    /// Lo que se retiene es la RESPUESTA, no la ejecución. El resumen de cobertura tiene que estar
    /// apuntado antes de cortar: si el corte llegara antes que el efecto, se ahorraría una llamada
    /// perdiendo el cierre de la unidad, que es exactamente el cambio que no se quiere.
    /// </summary>
    [Fact]
    public async Task La_herramienta_retenida_SE_EJECUTA_y_lo_unico_que_espera_es_su_respuesta()
    {
        var toolbox = new RecordingToolbox();
        var retention = new ToolRetention("unit_done");
        var server = new AtalayaMcpServer(AuditorTools.ForAudit(toolbox), retention: retention);

        Task serving = Serve(server, UnitDone());

        await retention.Retained.WaitAsync(TimeSpan.FromSeconds(5));

        toolbox.UnitPath.Should().Be("A.cs", "el efecto ya se aplicó; lo retenido es la contestación");
        serving.IsCompleted.Should().BeFalse("el relay sigue sin contestar, que es lo que para al CLI");

        retention.Release();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// <b>Y es reversible.</b> Si las cuentas no están, se suelta y la pasada termina como siempre:
    /// paga su llamada de cortesía y lo dice. Nunca un corte con hueco.
    /// </summary>
    [Fact]
    public async Task Soltar_la_retencion_devuelve_la_respuesta_y_la_sesion_sigue_como_si_nada()
    {
        var retention = new ToolRetention("unit_done");
        var server = new AtalayaMcpServer(AuditorTools.ForAudit(new RecordingToolbox()), retention: retention);

        using var output = new MemoryStream();
        Task serving = server.ServeAsync(Script(UnitDone()), output, CancellationToken.None);

        await retention.Retained.WaitAsync(TimeSpan.FromSeconds(5));
        retention.Release();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        Replies(output).Should().ContainSingle().Which
            .Should().Contain("\"isError\":false", "la respuesta acaba llegando, solo que más tarde");
    }

    /// <summary>
    /// Las HERMANAS del mismo turno se contestan al instante. Retener a una no puede convertir el
    /// relay en un cuello de botella: son las que persisten los hallazgos, y el corte espera a que
    /// terminen.
    /// </summary>
    [Fact]
    public async Task Las_hermanas_del_turno_se_contestan_mientras_unit_done_espera()
    {
        var toolbox = new RecordingToolbox();
        var retention = new ToolRetention("unit_done");
        var server = new AtalayaMcpServer(AuditorTools.ForAudit(toolbox), retention: retention);

        using var output = new MemoryStream();
        Task serving = server.ServeAsync(
            Script(Verdicts(), UnitDone()), output, CancellationToken.None);

        await retention.Retained.WaitAsync(TimeSpan.FromSeconds(5));

        // Se ESPERA a la hermana: se atiende en su propia tarea, así que puede no haber terminado
        // en el instante exacto en que unit_done queda retenida. Lo que se afirma es que termina
        // SIN que nadie suelte la retención — que es lo que dice este test.
        await Until(() => toolbox.Verdicts.Count == 1);
        toolbox.Verdicts.Should().HaveCount(1, "la hermana se atendió sin esperar a la retenida");
        retention.Release();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        Replies(output).Should().HaveCount(2);
    }

    /// <summary>Sin retención declarada, el relay es exactamente el de siempre.</summary>
    [Fact]
    public async Task Sin_retencion_no_cambia_nada()
    {
        using var output = new MemoryStream();
        await new AtalayaMcpServer(AuditorTools.ForAudit(new RecordingToolbox()))
            .ServeAsync(Script(UnitDone()), output, CancellationToken.None);

        Replies(output).Should().ContainSingle();
    }

    // ------------------------------------------------------- el desenlace de una pasada cortada

    /// <summary>
    /// Una pasada que hemos cortado NO es una pasada que ha fallado. El CLI la marca con
    /// <c>is_error</c> y sale con código 1 —desde su punto de vista lo han interrumpido—, así que
    /// sin esto toda pasada cortada se leería como una avería.
    /// </summary>
    [Fact]
    public void Un_corte_nuestro_no_se_lee_como_averia()
    {
        ClaudeCut cut = Cut(done: true);

        ClaudeRunOutcome outcome = ClaudeCliRunner.Explain(
            Interrupted("aborted_tools"), exitCode: 1, stderr: string.Empty, cut);

        outcome.Failed.Should().BeFalse();
        outcome.Problem.Should().Be(AgentProblem.None);
    }

    /// <summary>
    /// <b>Pero solo con el sello del propio CLI.</b> <c>aborted_tools</c> es SU declaración de que
    /// abortó con herramientas pendientes y <b>ninguna petición en vuelo</b> — que es la condición
    /// que hace que el corte no pierda cuentas ni pague nada a medias. Si dijera otra cosa, el
    /// corte no cayó donde creíamos, y eso tiene que verse con el motivo delante en vez de pasar
    /// por bueno.
    /// </summary>
    [Fact]
    public void Un_corte_que_no_cayo_con_las_herramientas_pendientes_se_denuncia_con_su_motivo()
    {
        ClaudeRunOutcome outcome = ClaudeCliRunner.Explain(
            Interrupted("max_turns"), exitCode: 1, stderr: string.Empty, Cut(done: true));

        outcome.Failed.Should().BeTrue();
        outcome.Message.Should().Contain("max_turns").And.Contain("aborted_tools");
    }

    /// <summary>Y sin corte, el desenlace se juzga como se juzgaba: aquí no se ha tocado nada.</summary>
    [Fact]
    public void Sin_corte_una_sesion_interrumpida_sigue_siendo_un_fallo()
        => ClaudeCliRunner.Explain(Interrupted("aborted_tools"), exitCode: 1, stderr: string.Empty)
            .Failed.Should().BeTrue();

    // ------------------------------------------------------- la decisión: cuándo SÍ y cuándo NO

    /// <summary>
    /// Con las dos condiciones puestas se corta, y la retención <b>no</b> se suelta: el CLI se va y
    /// no hay a quién contestar.
    /// </summary>
    [Fact]
    public async Task Con_las_hermanas_terminadas_y_las_cuentas_puestas_se_corta()
    {
        var retention = new ToolRetention("unit_done");
        var cut = new ClaudeCut(retention, toolsIdle: () => true);
        retention.Hold();

        bool corta = await ClaudeCliRunner.ShouldCutAsync(cut, () => true, CancellationToken.None);

        corta.Should().BeTrue();
        cut.Cut.Should().BeTrue();
        cut.NotCutBecause.Should().BeNull();
        retention.IsReleased.Should().BeFalse();
    }

    /// <summary>
    /// <b>Sin las cuentas NO se corta</b>, y ésta es la regla que hace que esta fase no repita el
    /// error que F19 evitó: un ahorro pagado con un número falso no es un ahorro (D-865). Se suelta
    /// la retención, la pasada termina como siempre y queda escrito por qué costó una llamada más.
    /// </summary>
    [Fact]
    public async Task Sin_las_cuentas_NO_se_corta_y_la_pasada_termina_como_siempre()
    {
        var retention = new ToolRetention("unit_done");
        var cut = new ClaudeCut(retention, toolsIdle: () => true)
        {
            IdleTimeout = TimeSpan.FromMilliseconds(150),
        };
        retention.Hold();

        bool corta = await ClaudeCliRunner.ShouldCutAsync(cut, () => false, CancellationToken.None);

        corta.Should().BeFalse();
        cut.Cut.Should().BeFalse();
        cut.NotCutBecause.Should().Contain("consumo");
        retention.IsReleased.Should().BeTrue("soltarla es lo que deja que la pasada acabe sola");
    }

    /// <summary>
    /// Y tampoco se corta encima de una herramienta a medias: las hermanas de <c>unit_done</c> son
    /// las que persisten los hallazgos, así que cortar ahí sería cambiar dinero por cobertura.
    /// </summary>
    [Fact]
    public async Task Con_una_herramienta_a_medias_NO_se_corta()
    {
        var retention = new ToolRetention("unit_done");
        var cut = new ClaudeCut(retention, toolsIdle: () => false)
        {
            IdleTimeout = TimeSpan.FromMilliseconds(150),
        };
        retention.Hold();

        bool corta = await ClaudeCliRunner.ShouldCutAsync(cut, () => true, CancellationToken.None);

        corta.Should().BeFalse();
        cut.NotCutBecause.Should().Contain("herramientas");
        retention.IsReleased.Should().BeTrue();
    }

    /// <summary>
    /// <b>Las condiciones se ESPERAN, no se preguntan una vez</b>, y esto costó una medida: el
    /// <c>message_delta</c> que cierra la llamada —el que trae su consumo definitivo— lo emite el
    /// CLI justo DESPUÉS de despachar las herramientas de ese mensaje. Preguntando al llegar
    /// <c>unit_done</c>, la respuesta era «todavía no» siempre y no se cortaba nunca.
    /// </summary>
    [Fact]
    public async Task Las_cuentas_que_llegan_un_instante_tarde_NO_impiden_el_corte()
    {
        var retention = new ToolRetention("unit_done");
        var cut = new ClaudeCut(retention, toolsIdle: () => true)
        {
            IdleTimeout = TimeSpan.FromSeconds(5),
        };
        retention.Hold();

        int asked = 0;
        bool corta = await ClaudeCliRunner.ShouldCutAsync(
            cut, () => ++asked > 3, CancellationToken.None);

        corta.Should().BeTrue("llegaron tarde, pero llegaron");
        asked.Should().BeGreaterThan(1, "se esperó en vez de preguntar una sola vez");
    }

    /// <summary>Y si `unit_done` no llega nunca, no hay nada que cortar y no se estorba a nadie.</summary>
    [Fact]
    public async Task Sin_unit_done_no_hay_corte_que_decidir()
    {
        var cut = new ClaudeCut(new ToolRetention("unit_done"), toolsIdle: () => true);
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        Func<Task> esperar = () => ClaudeCliRunner.ShouldCutAsync(cut, () => true, ct.Token);

        await esperar.Should().ThrowAsync<OperationCanceledException>();
        cut.Cut.Should().BeFalse();
    }

    // ------------------------------------------------------- ayudas

    /// <summary>Espera a que algo se cumpla. Lo que se afirma es que ocurre, no cuándo.</summary>
    private static async Task Until(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }
    }

    private static ClaudeCut Cut(bool done)
        => new(new ToolRetention("unit_done"), () => true) { Cut = done };

    private static ClaudeRunOutcome Interrupted(string terminalReason)
        => new(
            true, AgentProblem.Unknown, "interrumpido", Started: true, McpConnected: true,
            Tools: new[] { "mcp__atalaya__unit_done" }, Model: "claude-sonnet-5", ToolCalls: 4,
            Usage: null, TerminalReason: terminalReason);

    private static Task Serve(AtalayaMcpServer server, params string[] requests)
        => server.ServeAsync(Script(requests), new MemoryStream(), CancellationToken.None);

    private static MemoryStream Script(params string[] requests)
        => new(Encoding.UTF8.GetBytes(string.Join('\n', requests) + "\n"));

    private static string[] Replies(MemoryStream output)
        => Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string UnitDone()
        => """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"unit_done","arguments":{"unitPath":"A.cs","summary":"limpia"}}}""";

    private static string Verdicts()
        => """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"report_verdicts","arguments":{"verdicts":[{"findingId":"01AAA","verdict":"presente","evidence":"sigue"}]}}}""";

    private sealed class RecordingToolbox : IAuditToolbox
    {
        public List<VerdictArgs> Verdicts { get; } = new();

        public string? UnitPath { get; private set; }

        public SubmitFindingResult SubmitFinding(SubmitFindingArgs args) => new(true);

        public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
            => new(findings.Select(_ => new SubmitFindingResult(true)).ToList());

        public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
        {
            Verdicts.AddRange(verdicts);
            return new ReportVerdictsResult(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());
        }

        public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
            => new(true, locations.Length);

        public void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern = null)
            => UnitPath = unitPath;

        public string ReadSignatures(string path) => "// firmas";
    }
}
