using Atalaya.Agents;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// F25 §3 — <b>el hilo de Copilot: la forma, que es lo que aquí se puede comprobar.</b>
/// <para>
/// Esta máquina no tiene asiento —<c>models.list</c> contesta 403—, así que crear una sesión de
/// verdad es imposible y afirmar que las pasadas 2..N leen caché en vez de escribirla sería dar por
/// hecho el comportamiento de una casa que no se tiene delante, que es exactamente lo que N-2
/// prohíbe. Lo que sí se comprueba, y se comprueba entero: que el catálogo es el mismo que ve la
/// otra casa, que <c>submit_finding</c> devuelve el ULID, que <b>una unidad es una sesión</b> y que
/// una conversación rota no se lleva por delante la unidad.
/// </para>
/// <para>
/// La verificación del ahorro la hace el usuario con una sesión suya, y lo que la enseñaría es la
/// escritura de caché por pasada del anexo técnico.
/// </para>
/// </summary>
public sealed class UnitThreadSurfaceTests
{
    /// <summary>
    /// <b>Las herramientas son las mismas en los dos transportes</b> (PROV-2 §3): misma lista,
    /// mismo orden y misma descripción.
    /// <para>
    /// <b>Y se comparan los dos catálogos ENTRE SÍ</b>, no cada uno contra una lista escrita a
    /// mano. Aquí antes había una lista de seis nombres tecleados, y precisamente por eso
    /// <c>unit_done</c> pudo divergir durante siete fases: la descripción de MCP llevaba una frase
    /// de más y ningún test miraba las dos. Lo que se rompería en silencio si esto no existiera es
    /// que el mismo prompt de unidad significara dos cosas según quién lo leyera, y entonces una
    /// discrepancia entre las dos casas ya no se podría atribuir al modelo — que es la única razón
    /// por la que hay dos.
    /// </para>
    /// <para>El orden también se afirma: <c>unit_done</c> la última es D-883, no una casualidad.</para>
    /// </summary>
    [Fact]
    public void El_catalogo_de_la_auditoria_es_el_mismo_en_los_dos_transportes()
    {
        SessionConfig config = new RealCopilotAgent().BuildAuditSessionConfig(new RecordingToolbox());
        IReadOnlyList<McpTool> mcp = AuditorTools.ForAudit(new RecordingToolbox());

        config.Tools!.Select(t => t.Name).Should().Equal(mcp.Select(t => t.Name));
        config.Tools!.Select(t => t.Name).Should().Equal(AuditToolText.Audit);
        config.Tools!.Select(t => t.Description).Should().Equal(mcp.Select(t => t.Description));
    }

    /// <summary>
    /// <b>La terminalidad la declara el TRANSPORTE, y la descripción compartida no la menciona</b>
    /// (PROV-2 §3). El SDK de Copilot tiene el concepto —<c>IsTerminal</c>— y no hace falta
    /// decirlo con palabras; MCP no lo tiene, así que su transporte lo compone a partir de la
    /// misma marca declarada, y las palabras son las mismas para cualquier terminal futura.
    /// <para>
    /// Lo que se rompería en silencio sin esto: que la frase volviera a la descripción compartida
    /// —donde estaba— y las dos casas recibieran otra vez instrucciones distintas.
    /// </para>
    /// </summary>
    [Fact]
    public void La_terminalidad_no_viaja_en_la_descripcion_compartida()
    {
        AuditToolText.UnitDoneDescription.Should().NotContain("HAS TERMINADO",
            "la frase es de la terminalidad, y la terminalidad es una marca, no un texto");
        AuditToolText.Terminal.Should().Equal(AuditToolText.UnitDone);

        McpTool unitDone = AuditorTools.ForAudit(new RecordingToolbox())
            .Single(t => t.Name == AuditToolText.UnitDone);

        unitDone.IsTerminal.Should().BeTrue();
        McpTerminal.Describe(unitDone).Should()
            .Be(AuditToolText.UnitDoneDescription + McpTerminal.Suffix,
                "por MCP la terminalidad solo se puede decir con palabras, y las pone el transporte");

        SessionConfig config = new RealCopilotAgent().BuildAuditSessionConfig(new RecordingToolbox());
        Describe(config, AuditToolText.UnitDone).Should().Be(AuditToolText.UnitDoneDescription,
            "el SDK ya tiene `IsTerminal`: aquí decirlo con palabras sería decirlo dos veces");
    }

    /// <summary>
    /// <b>Y los dos <c>submit</c> anuncian el id</b> (D-916). El prompt es el MISMO para las dos
    /// casas y nombra el id como la forma de dar veredicto sobre lo propio: si aquí no lo dijera,
    /// el mismo prompt significaría dos cosas.
    /// </summary>
    [Fact]
    public void Los_dos_submit_le_dicen_al_modelo_que_devuelven_el_ULID()
    {
        SessionConfig config = new RealCopilotAgent().BuildAuditSessionConfig(new RecordingToolbox());

        Describe(config, "submit_findings").Should().Contain("duplicateOf, error, id");
        Describe(config, "submit_finding").Should().Contain("id");
    }

    /// <summary>
    /// <b>Una unidad, una sesión.</b> Es lo único que produce el ahorro: si cada pasada abriera la
    /// suya, el prefijo se volvería a escribir igual y no habría hilo que valga.
    /// </summary>
    [Fact]
    public async Task Los_turnos_de_la_unidad_van_todos_por_la_misma_sesion()
    {
        var turns = new RecordingTurns();
        IUnitThread thread = Thread(turns);

        await thread.TurnAsync("el prompt entero", CancellationToken.None);
        await thread.TurnAsync("la continuación", CancellationToken.None);
        await thread.TurnAsync("la continuación", CancellationToken.None);

        turns.Sent.Should().HaveCount(3);
        turns.Sent[0].Should().Be("el prompt entero");
        thread.Closed.Should().BeFalse("la conversación sigue viva entre turnos");
    }

    /// <summary>Cerrar el hilo cierra la sesión, y una sola vez.</summary>
    [Fact]
    public async Task Cerrar_el_hilo_cierra_la_sesion_una_vez()
    {
        var turns = new RecordingTurns();
        IUnitThread thread = Thread(turns);

        await thread.TurnAsync("el prompt entero", CancellationToken.None);
        await thread.DisposeAsync();
        await thread.DisposeAsync();

        turns.Disposals.Should().Be(1);
        thread.Closed.Should().BeTrue();
    }

    /// <summary>
    /// <b>Una sesión que ya no existe no se lleva por delante la unidad.</b> Un fallo sin remedio
    /// propio —así llega una sesión caducada— se dice como lo que es, la conversación se rompió, y
    /// el barrido rehace la pasada con una petición nueva.
    /// </summary>
    [Fact]
    public async Task Una_sesion_que_no_se_puede_continuar_se_dice_como_conversacion_rota()
    {
        var turns = new RecordingTurns { Throw = new InvalidOperationException("session does not exist") };
        IUnitThread thread = Thread(turns, ex => new AuditorProviderException(ex.Message, AgentProblem.Unknown));

        Func<Task> turn = () => thread.TurnAsync("la continuación", CancellationToken.None);

        await turn.Should().ThrowAsync<UnitThreadBrokenException>();
        thread.Closed.Should().BeTrue("una sesión que ha contestado con un error no sirve para el turno siguiente");
    }

    /// <summary>
    /// <b>Y una cuota agotada sube tal cual.</b> Rehacer la pasada con una petición nueva contra una
    /// cuota agotada gastaría las peticiones del reset siguiente, que es lo que BUGFIX-CUOTA dejó
    /// decidido: eso se cierra en orden, no se reintenta.
    /// </summary>
    [Fact]
    public async Task Una_cuota_agotada_sube_tal_cual_y_no_se_reintenta()
    {
        var turns = new RecordingTurns { Throw = new InvalidOperationException("quota") };
        IUnitThread thread = Thread(
            turns, ex => new AuditorProviderException(ex.Message, AgentProblem.QuotaExhausted));

        Func<Task> turn = () => thread.TurnAsync("la continuación", CancellationToken.None);

        (await turn.Should().ThrowAsync<AuditorProviderException>())
            .Which.Should().NotBeOfType<UnitThreadBrokenException>();
    }

    private static IUnitThread Thread(ICopilotTurns turns, Func<Exception, Exception>? translate = null)
        => new CopilotUnitThread(
            turns,
            () => TimeSpan.FromMinutes(1),
            translate ?? (ex => ex),
            NullLogger.Instance);

    private static string Describe(SessionConfig config, string name)
        => config.Tools!.Single(t => t.Name == name).Description ?? string.Empty;

    /// <summary>Los turnos, sin sesión detrás: lo que hace falta para poder probar el hilo sin asiento.</summary>
    private sealed class RecordingTurns : ICopilotTurns
    {
        public List<string> Sent { get; } = new();

        public int Disposals { get; private set; }

        public Exception? Throw { get; init; }

        public Task SendAsync(string prompt, TimeSpan timeout, CancellationToken ct)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Sent.Add(prompt);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Un toolbox que acepta todo. No decide nada aquí: lo que se mira es el catálogo.</summary>
    private sealed class RecordingToolbox : IAuditToolbox
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

        public string ReadSignatures(string path) => "// firmas";
    }
}
