using Atalaya.Agents;
using Atalaya.Copilot;
using FluentAssertions;
using GitHub.Copilot;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// F5.2 — el agente emite su TEXTO, no un punto por evento.
/// <para>
/// Hasta aquí <c>OnSessionEvent</c> hacía <c>TextStreamed(".")</c> para cualquier mensaje del
/// asistente, así que la columna de actividad de V5 era literalmente una fila de puntos: se veía
/// que el agente seguía vivo y nada más.
/// </para>
/// <para>
/// Esto SÍ se puede probar sin asiento de Copilot, al contrario que el resto de
/// <see cref="RealCopilotAgent"/> (D-017): los tipos de evento del SDK 1.0.11 son construibles, así
/// que el mapeo —incluida la deduplicación, que es la parte con riesgo real de imprimir el mensaje
/// dos veces— se ejercita de verdad.
/// </para>
/// </summary>
public sealed class StreamedTextTests
{
    private static (RealCopilotAgent Agent, List<string> Text) NewAgent()
    {
        var agent = new RealCopilotAgent();
        var captured = new List<string>();
        agent.TextStreamed += captured.Add;
        return (agent, captured);
    }

    private static AssistantMessageDeltaEvent Delta(string messageId, string content)
        => new() { Data = new AssistantMessageDeltaData { MessageId = messageId, DeltaContent = content } };

    private static AssistantMessageEvent Message(string messageId, string content)
        => new() { Data = new AssistantMessageData { MessageId = messageId, Content = content } };

    [Fact]
    public void Streaming_deltas_are_emitted_verbatim()
    {
        (RealCopilotAgent agent, List<string> text) = NewAgent();

        agent.OnSessionEvent(Delta("m1", "Revisando "));
        agent.OnSessionEvent(Delta("m1", "ReadCSV: "));
        agent.OnSessionEvent(Delta("m1", "el stream no se cierra."));

        string.Concat(text).Should().Be("Revisando ReadCSV: el stream no se cierra.");
        text.Should().NotContain(".", "un punto suelto era justo el bug");
    }

    /// <summary>
    /// Con streaming el SDK manda los deltas Y, al cerrar el turno, el mensaje entero. Emitir los
    /// dos duplicaría todo el texto en pantalla.
    /// </summary>
    [Fact]
    public void The_closing_message_is_not_repeated_when_its_deltas_already_arrived()
    {
        (RealCopilotAgent agent, List<string> text) = NewAgent();

        agent.OnSessionEvent(Delta("m1", "hola "));
        agent.OnSessionEvent(Delta("m1", "mundo"));
        agent.OnSessionEvent(Message("m1", "hola mundo"));

        string.Concat(text).Should().Be("hola mundo");
    }

    /// <summary>Sin streaming no hay deltas: entonces el mensaje completo SÍ es lo único que hay.</summary>
    [Fact]
    public void A_message_with_no_deltas_is_emitted_whole()
    {
        (RealCopilotAgent agent, List<string> text) = NewAgent();

        agent.OnSessionEvent(Message("m1", "resumen completo del turno"));

        string.Concat(text).Should().Be("resumen completo del turno");
    }

    /// <summary>Dos mensajes distintos no se estorban: cada uno lleva su propia contabilidad.</summary>
    [Fact]
    public void Each_message_is_deduplicated_independently()
    {
        (RealCopilotAgent agent, List<string> text) = NewAgent();

        agent.OnSessionEvent(Delta("m1", "uno"));
        agent.OnSessionEvent(Message("m1", "uno"));
        agent.OnSessionEvent(Message("m2", "dos"));

        string.Concat(text).Should().Be("unodos");
    }

    // ================================================================ F30 §2 · la herramienta que se escribe

    /// <summary>
    /// <b>La herramienta se anuncia al EMPEZAR y sus elementos van apareciendo</b> (F30 §2).
    /// <para>
    /// El SDK ya mandaba las dos cosas —<c>ToolExecutionStartEvent</c> y
    /// <c>AssistantToolCallDeltaEvent</c>, que trae <c>InputDelta</c>— y <c>OnSessionEvent</c> las
    /// tiraba: de la sesentena de tipos que declara, se atendían tres. Ahí estaba el minuto que
    /// D-1014 midió: reportar once hallazgos son ~2.700 tokens de escritura, unos 42 s, y no se
    /// veía nada hasta que la herramienta se ejecutaba.
    /// </para>
    /// </summary>
    [Fact]
    public void A_tool_is_announced_when_it_starts_and_counted_as_it_is_written()
    {
        var agent = new RealCopilotAgent();
        var narrated = new List<ToolStream>();
        agent.ToolStreamed += narrated.Add;

        agent.OnSessionEvent(ToolStart("t1", "mcp__atalaya__submit_findings"));
        agent.OnSessionEvent(ToolDelta("t1", "submit_findings", """{"findings":[{"title":"Credenciales emb"""));
        agent.OnSessionEvent(ToolDelta("t1", "submit_findings", """ebidas"},"""));
        agent.OnSessionEvent(ToolDelta("t1", "submit_findings", """{"title":"Fuga de stream"}]}"""));

        narrated[0].Phase.Should().Be(ToolStreamPhase.Started);
        narrated[0].Tool.Should().Be("submit_findings", "sin el prefijo del servidor MCP");

        List<ToolStream> input = narrated.Where(t => t.Phase == ToolStreamPhase.Input).ToList();
        input.Select(t => t.Items).Should().Equal(new[] { 1, 2 },
            "un título a medias no cuenta hasta que su comilla cierra");
        input[^1].Last.Should().Be("Fuga de stream");
    }

    /// <summary>
    /// Y el trozo que no completa nada <b>no</b> repinta: a 64 tokens por segundo serían decenas de
    /// avisos por segundo sin una sola información nueva.
    /// </summary>
    [Fact]
    public void A_chunk_that_completes_nothing_says_nothing()
    {
        var agent = new RealCopilotAgent();
        var narrated = new List<ToolStream>();
        agent.ToolStreamed += narrated.Add;

        agent.OnSessionEvent(ToolDelta("t1", "submit_findings", """{"findings":[{"tit"""));
        agent.OnSessionEvent(ToolDelta("t1", "submit_findings", """le":"a med"""));

        narrated.Should().BeEmpty("todavía no hay ni un elemento entero que enseñar");
    }

    /// <summary>
    /// Dos llamadas a la vez no se estorban: cada una lleva su propia cuenta, igual que los
    /// mensajes de texto.
    /// <para>
    /// F30 §2e — el trozo de cierre llevaba una comilla de más (<c>,"{"title"…</c>), y con ella el
    /// JSON del turno no era JSON. La expresión regular que contaba antes no miraba si estaba
    /// dentro de una cadena o fuera, así que lo daba por bueno; el autómata que la sustituye sí
    /// —y por eso también deja de contar un <c>"title"</c> que aparezca DENTRO de la evidencia de
    /// un hallazgo—. El dato se arregla: lo que este test protege es que dos llamadas simultáneas
    /// no se mezclen, no que se tolere un array mal escrito.
    /// </para>
    /// </summary>
    [Fact]
    public void Two_tool_calls_are_counted_independently()
    {
        var agent = new RealCopilotAgent();
        var narrated = new List<ToolStream>();
        agent.ToolStreamed += narrated.Add;

        agent.OnSessionEvent(ToolDelta("t1", "submit_findings", """{"findings":[{"title":"uno"}"""));
        agent.OnSessionEvent(ToolDelta("t2", "report_verdicts", """{"verdicts":[{"verdict":"presente"}"""));
        agent.OnSessionEvent(ToolDelta("t1", "submit_findings", """,{"title":"dos"}]}"""));

        narrated.Where(t => t.Tool == "submit_findings").Select(t => t.Items).Should().Equal(new[] { 1, 2 });
        narrated.Where(t => t.Tool == "report_verdicts").Select(t => t.Items).Should().Equal(new[] { 1 });
    }

    private static ToolExecutionStartEvent ToolStart(string id, string name)
        => new() { Data = new ToolExecutionStartData { ToolCallId = id, ToolName = name } };

    private static AssistantToolCallDeltaEvent ToolDelta(string id, string name, string chunk)
        => new()
        {
            Data = new AssistantToolCallDeltaData { ToolCallId = id, ToolName = name, InputDelta = chunk },
        };

    [Fact]
    public void Empty_chunks_never_reach_the_view()
    {
        (RealCopilotAgent agent, List<string> text) = NewAgent();

        agent.OnSessionEvent(Delta("m1", string.Empty));
        agent.OnSessionEvent(Delta("m2", string.Empty));
        agent.OnSessionEvent(Message("m3", string.Empty));

        text.Should().BeEmpty();
    }
}
