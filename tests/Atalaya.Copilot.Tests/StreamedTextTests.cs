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
