using Atalaya.Agents;
using Atalaya.ClaudeCode;
using FluentAssertions;
using Xunit;

namespace Atalaya.ClaudeCode.Tests;

/// <summary>
/// F30 §2d — <b>EL FLUJO REAL DEL CLI, GRABADO Y REPRODUCIDO</b>.
/// <para>
/// <b>Por qué grabado y no inventado.</b> Los demás tests del lector construyen sus eventos a mano,
/// y eso prueba lo que uno cree que manda el CLI. Aquí se reproduce un turno <b>real</b>, capturado
/// del CLI 2.1.263 con exactamente los flags con los que Atalaya lo invoca
/// (<c>--output-format stream-json --verbose --include-partial-messages --input-format stream-json</c>),
/// recortado solo de lo que identifica la máquina. Lo que se ve ahí es lo que hay:
/// <c>system/init</c>, un <c>system/status</c> y un <c>rate_limit_event</c> que nadie esperaba,
/// <c>message_start</c>, <c>content_block_start</c>, los <c>text_delta</c>, el <c>assistant</c> con
/// el texto YA completo, <c>content_block_stop</c>, <c>message_delta</c> con el consumo final,
/// <c>message_stop</c> y <c>result</c>.
/// </para>
/// <para>
/// Fija las tres cosas que la entrega 1 tocó y que no se pueden romper en silencio: que el texto
/// llegue <b>delta a delta</b>, que el <c>assistant</c> que lo repite entero <b>no lo duplique</b>,
/// y que las cuentas de F21 —de las que depende el corte— salgan del turno intactas.
/// </para>
/// </summary>
public sealed class RealStreamReplayTests
{
    [Fact]
    public async Task El_texto_de_un_turno_real_llega_delta_a_delta_y_sin_repetirse()
    {
        var chunks = new List<string>();

        await new ClaudeStreamReader(onText: chunks.Add)
            .ReadAsync(new StringReader(Recording()), CancellationToken.None);

        chunks.Should().HaveCountGreaterThan(1,
            "el CLI manda el texto por trozos: si llega en uno solo, se están leyendo otra vez los "
            + "eventos `assistant` cerrados y la pantalla se queda quieta hasta el final del mensaje");
        string.Concat(chunks).Should().Be("hola",
            "y el `assistant` que repite el mensaje entero al cerrarlo no puede duplicarlo");
    }

    /// <summary>
    /// <b>Y las cuentas del turno real cuadran</b>: una llamada, y el consumo definitivo del
    /// <c>message_delta</c>, no el anticipo del <c>assistant</c>. Es la condición del corte de F21
    /// medida contra el flujo de verdad y no contra uno escrito a mano.
    /// </summary>
    [Fact]
    public async Task Las_cuentas_de_un_turno_real_salen_enteras()
    {
        var samples = new List<UsageSample>();

        var reader = new ClaudeStreamReader(onUsage: samples.Add);
        ClaudeRunOutcome outcome = await reader.ReadAsync(
            new StringReader(Recording()), CancellationToken.None);

        outcome.Failed.Should().BeFalse();
        outcome.TerminalReason.Should().Be("completed");
        reader.SettledCalls.Should().Be(1, "el turno cerró su única llamada");
        reader.AccountingIsComplete.Should().BeTrue("sin esto no se podría cortar la pasada (F21)");

        samples.Where(s => !s.Reconciliation).Sum(s => s.OutputTokens)
            .Should().Be(4, "manda el consumo del `message_delta`, no el 1 del anticipo");
    }

    /// <summary>
    /// El flujo trae eventos que Atalaya no espera —<c>system/status</c>, <c>rate_limit_event</c>—
    /// y tiene que ignorarlos sin ruido: un lector que se rompiera con una línea desconocida
    /// convertiría cada versión nueva del CLI en una avería.
    /// </summary>
    [Fact]
    public async Task Los_eventos_que_no_nos_incumben_se_ignoran_sin_ruido()
    {
        Recording().Should().Contain("\"rate_limit_event\"", "la grabación tiene que traerlos");
        Recording().Should().Contain("\"subtype\":\"status\"");

        ClaudeRunOutcome outcome = await new ClaudeStreamReader()
            .ReadAsync(new StringReader(Recording()), CancellationToken.None);

        outcome.Failed.Should().BeFalse();
        outcome.Model.Should().NotBeNullOrEmpty("el `init` se leyó igual");
    }

    /// <summary>La grabación, junto a los tests para que se pueda mirar sin ejecutar nada.</summary>
    private static string Recording()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(
            dir!.FullName, "tests", "Atalaya.ClaudeCode.Tests", "Grabaciones", "turno-real.jsonl"));
    }
}
