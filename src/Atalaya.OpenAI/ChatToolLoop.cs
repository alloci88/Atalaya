using Atalaya.Agents;

namespace Atalaya.OpenAI;

/// <summary>
/// <b>El bucle de herramientas</b> (PROV-3 §4): mandar el prompt, ejecutar lo que el modelo pida,
/// devolverle cada resultado como mensaje <c>tool</c> y volver a preguntar hasta que cierre.
/// <para>
/// <b>No sabe una palabra de HTTP</b>, y ésa es toda la gracia: habla por <see cref="IChatEndpoint"/>
/// y el transporte es de otro. Por eso se prueba con un doble guionizado en memoria y no con un
/// servidor falso — probarlo por HTTP sería probar la capa de al lado.
/// </para>
/// <para>
/// <b>Y no juzga nada.</b> Ejecutar una herramienta es llamar al toolbox de la aplicación, que ya
/// trae sus guardas de dominio; aquí no se valida un ULID, ni se decide un duplicado, ni se
/// comprueba que una ubicación caiga dentro de la unidad. Un proveedor propone y la aplicación
/// juzga (D-775).
/// </para>
/// </summary>
internal static class ChatToolLoop
{
    /// <summary>
    /// Una conversación completa con herramientas.
    /// </summary>
    /// <param name="chat">Con quién se habla. El transporte, del que aquí solo se conoce el verbo.</param>
    /// <param name="tools">El catálogo que se ofrece, ya traducido a este dialecto.</param>
    /// <param name="prompt">El prompt de la aplicación, entero, como primer y único mensaje de usuario.</param>
    /// <param name="maxTurns">
    /// El techo de vueltas. Sale de <see cref="AuditLoopLimits"/> y <b>nunca es cero</b>.
    /// </param>
    /// <param name="onText">Cada trozo de texto, para el hilo de actividad.</param>
    /// <param name="onUsage">
    /// El consumo de CADA vuelta. Se llama siempre, también cuando el endpoint no mandó
    /// <c>usage</c>: la llamada ocurrió, y el contador de llamadas del coordinador vive de esto.
    /// </param>
    /// <param name="onTool">Qué herramienta se está atendiendo, para el hilo de actividad.</param>
    /// <param name="onNotCut">
    /// Cuando la conversación NO se cerró por donde debía, con el motivo escrito. En una auditoría
    /// es el <c>CutSkipped</c> del contrato; en una verificación —donde no hay terminal que
    /// esperar— solo suena al llegar al techo, que allí sigue siendo algo que hay que decir.
    /// </param>
    public static async Task RunAsync(
        IChatEndpoint chat,
        IReadOnlyList<ChatFunction> tools,
        string prompt,
        int maxTurns,
        Action<string>? onText,
        Action<ChatUsage?> onUsage,
        Action<ToolStream>? onTool,
        Action<string>? onNotCut,
        CancellationToken ct)
    {
        // Cuál de las ofrecidas cierra la conversación, si es que alguna lo hace. Sale del
        // catálogo compartido, no de un nombre escrito aquí: en una sesión de verificación no hay
        // ninguna, y entonces que el modelo deje de llamar herramientas ES el final normal.
        string? terminal = tools.FirstOrDefault(t => t.IsTerminal)?.Name;

        Dictionary<string, ChatFunction> byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        IReadOnlyList<ChatToolSpec> specs = tools.Select(t => t.Spec).ToList();
        var messages = new List<ChatMessage> { ChatMessage.User(prompt) };

        for (int turn = 1; ; turn++)
        {
            ct.ThrowIfCancellationRequested();

            ChatTurn answer = await chat.SendAsync(messages, specs, onText, ct).ConfigureAwait(false);

            // El consumo se publica ANTES de decidir nada más: si la vuelta se va a cerrar, el
            // coste de ESTA llamada tiene que estar contado igual. Una pasada que corta sin
            // publicar lo que gastó es una cifra que se pierde.
            onUsage(answer.Usage);

            if (answer.ToolCalls.Count == 0)
            {
                // El modelo dejó de hablar. No se insiste: insistir cuesta otra llamada y el
                // prompt ya le dijo cómo se cierra. Si HABÍA una terminal y no la llamó, la pasada
                // se queda sin cortar y eso no puede ser una cifra sin causa (N-2); si no la
                // había, esto es el final normal de una verificación y no hay nada que decir.
                if (terminal is not null)
                {
                    onNotCut?.Invoke(
                        $"el modelo acabó el turno sin llamar a {terminal} "
                        + $"(finish_reason={answer.FinishReason ?? "no lo dijo"})");
                }

                return;
            }

            messages.Add(new ChatMessage(
                "assistant",
                answer.Text is { Length: > 0 } said ? said : null,
                answer.ToolCalls));

            // Las llamadas de un turno llegan en UN array y se ejecutan TODAS, en el orden en que
            // el modelo las escribió — también las que vengan detrás de la terminal. El prompt
            // pone `unit_done` la última (D-883), pero si un modelo la adelanta, tirar sus
            // hermanas perdería hallazgos que ya están escritos y pagados. Lo que la terminal corta
            // es la vuelta SIGUIENTE, que es donde está el gasto.
            bool closed = false;
            foreach (ChatToolCall call in answer.ToolCalls)
            {
                onTool?.Invoke(new ToolStream(
                    ToolStreamPhase.Input, call.Name, AuditorFunctions.CountItems(call)));

                messages.Add(ChatMessage.Tool(call.Id, AuditorFunctions.Execute(byName, call)));

                closed |= byName.TryGetValue(call.Name, out ChatFunction? tool) && tool.IsTerminal;
            }

            if (closed)
            {
                // Cerrada: se ejecutó la terminal, el turno se da por acabado y NO se pide otro.
                return;
            }

            if (turn >= maxTurns)
            {
                // El techo. No es una alternativa al corte: es lo que impide que su ausencia se
                // convierta en un bucle. Y se dice con el número delante, que es lo que separa
                // «gastó mucho» de «dio muchas vueltas».
                onNotCut?.Invoke(
                    $"la conversación llegó al techo de {maxTurns} vueltas"
                    + (terminal is null ? string.Empty : $" sin que el modelo llamara a {terminal}"));
                return;
            }
        }
    }
}
