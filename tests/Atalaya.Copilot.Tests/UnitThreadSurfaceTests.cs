using Atalaya.Agents;
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
    /// <summary>El catálogo de la auditoría es el de siempre, palabra por palabra con el de la otra casa.</summary>
    [Fact]
    public void El_catalogo_de_la_auditoria_ofrece_las_seis_herramientas_de_siempre()
    {
        SessionConfig config = new RealCopilotAgent().BuildAuditSessionConfig(new RecordingToolbox());

        config.Tools!.Select(t => t.Name).Should().Equal(
            "submit_findings", "submit_finding", "report_verdicts",
            "add_locations", "unit_done", "read_signatures");
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
