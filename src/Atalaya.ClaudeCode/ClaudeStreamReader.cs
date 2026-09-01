using System.Text.Json;
using Atalaya.Agents;

namespace Atalaya.ClaudeCode;

/// <summary>
/// Cómo terminó una invocación del CLI. Es el resultado que el driver convierte en «la unidad se
/// auditó» o en una excepción con causa.
/// </summary>
/// <param name="Failed">
/// Hubo un fallo. Se lee de <c>is_error</c>, NO de <c>subtype</c>: verificado contra el CLI real,
/// un modelo inexistente devuelve <c>"subtype":"success"</c> con <c>"is_error":true</c> y un 404.
/// Creerle al <c>subtype</c> habría dado por buena una sesión que nunca corrió (D-782).
/// </param>
/// <param name="Problem">La causa ya clasificada, cuando se ha podido.</param>
/// <param name="Message">Lo que el proveedor dijo, para poder enseñarlo.</param>
/// <param name="Started">
/// El CLI llegó a emitir su evento de inicio. Distingue «la sesión arrancó y algo salió mal» de
/// «esto ni siquiera es el CLI contestando», y el diagnóstico de las dos NO puede ser el mismo:
/// decir «el servidor MCP no conectó» ante una salida ilegible manda a mirar el sitio equivocado.
/// </param>
/// <param name="McpConnected">
/// Si el servidor MCP de Atalaya llegó a conectar. Sale del evento <c>system/init</c>, que lista
/// los servidores con su estado. Solo significa algo cuando <paramref name="Started"/> es cierto.
/// </param>
/// <param name="Tools">Las tools que el CLI declaró disponibles en <c>system/init</c>.</param>
/// <param name="Model">El modelo que el CLI resolvió de verdad (el alias ya expandido).</param>
public sealed record ClaudeRunOutcome(
    bool Failed,
    AgentProblem Problem,
    string Message,
    bool Started,
    bool McpConnected,
    IReadOnlyList<string> Tools,
    string? Model,
    int ToolCalls,
    UsageSample? Usage);

/// <summary>
/// Cómo terminó UN turno de una conversación (F16). Una sesión de auditoría tiene exactamente uno;
/// una de arreglo tiene tantos como veces hable el usuario.
/// </summary>
/// <param name="Usage">
/// El consumo DE ESTE TURNO. Los tokens los da el CLI por turno; el coste, no —ver
/// <see cref="ClaudeStreamReader"/>—, así que aquí ya viene restado.
/// </param>
public sealed record ClaudeTurn(bool Failed, AgentProblem Problem, string Message, UsageSample? Usage);

/// <summary>
/// Lee la salida <c>--output-format stream-json</c> del CLI de <c>claude</c> (F14).
/// <para>
/// <b>El formato se verificó ejecutando el CLI real (2.1.252), no leyendo documentación.</b> Lo
/// que llega es una línea de JSON por evento: <c>system/init</c> con las tools y el estado de los
/// servidores MCP, N eventos <c>assistant</c> y <c>user</c> con el texto y las llamadas a tool, y
/// un <c>result</c> final con el uso, el coste y el desenlace. Por medio aparecen tipos que no nos
/// incumben (<c>rate_limit_event</c>, <c>system/thinking_tokens</c>) y, en stderr, líneas que NO
/// son JSON —<c>[claude-code:unrecognized_model] {...}</c>—. Todo eso se ignora sin ruido: un
/// parser que se rompiera con una línea desconocida convertiría cada versión nueva del CLI en una
/// avería.
/// </para>
/// </summary>
public sealed class ClaudeStreamReader
{
    private readonly Action<string>? _onText;
    private readonly Action<ClaudeTurn>? _onTurn;

    /// <param name="onTurn">
    /// Se invoca al cerrar CADA turno (el evento <c>result</c>). En una sesión de un solo turno da
    /// lo mismo que leer el desenlace al final; en una conversación es la única forma de saber que
    /// le toca hablar al usuario.
    /// </param>
    /// <param name="onText">
    /// El texto del auditor según llega, para la columna de actividad de V5. Es el equivalente de
    /// <c>AssistantMessageDeltaEvent</c> en Copilot: se emite el TEXTO, no un punto por evento
    /// (F5.2).
    /// </param>
    public ClaudeStreamReader(Action<string>? onText = null, Action<ClaudeTurn>? onTurn = null)
    {
        _onText = onText;
        _onTurn = onTurn;
    }

    /// <summary>
    /// Consume el flujo hasta que se acaba y devuelve cómo terminó. No lanza por contenido: un
    /// flujo que se corta sin <c>result</c> es un desenlace —y de los importantes—, no una
    /// excepción de parseo.
    /// </summary>
    public async Task<ClaudeRunOutcome> ReadAsync(TextReader output, CancellationToken ct)
    {
        bool sawInit = false;
        bool mcpConnected = false;
        var tools = new List<string>();
        string? model = null;
        int toolCalls = 0;

        bool failed = false;
        var problem = AgentProblem.None;
        string message = string.Empty;
        UsageSample? usage = null;
        bool sawResult = false;

        // El coste acumulado que el CLI lleva declarado. Ver ReadUsage: `total_cost_usd` es de la
        // SESIÓN entera y `usage` es del turno, así que el coste de un turno es la diferencia.
        decimal costSoFar = 0m;

        while (await output.ReadLineAsync(ct) is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            JsonElement e;
            try
            {
                e = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException)
            {
                continue;               // stderr suelto, banners, trazas: no son eventos.
            }

            if (e.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            switch (Str(e, "type"))
            {
                case "system" when Str(e, "subtype") == "init":
                    sawInit = true;
                    model = Str(e, "model") is { Length: > 0 } m ? m : null;
                    // En una conversación el CLI emite un `init` por turno: se acumulan SIN
                    // repetir, o la lista de tools crecería con copias a cada vuelta.
                    tools.AddRange(Strings(e, "tools").Where(t => !tools.Contains(t, StringComparer.Ordinal)));
                    mcpConnected = McpIsConnected(e);
                    break;

                case "assistant":
                    (int calls, string text) = ReadAssistant(e);
                    toolCalls += calls;
                    if (text.Length > 0)
                    {
                        _onText?.Invoke(text);
                    }

                    break;

                case "result":
                    sawResult = true;
                    (failed, problem, message, usage) = ReadResult(e, ref costSoFar);
                    _onTurn?.Invoke(new ClaudeTurn(failed, problem, message, usage));
                    break;
            }
        }

        // El flujo se acabó sin un `result`: el CLI murió a media sesión. Es un final terminal y
        // honesto —con su causa— y NUNCA un éxito silencioso.
        if (!sawResult)
        {
            return new ClaudeRunOutcome(
                true,
                AgentProblem.Unknown,
                sawInit
                    ? "Claude Code terminó sin dar un resultado: la sesión se cortó a mitad."
                    : "Claude Code no llegó a arrancar la sesión (no emitió el evento de inicio).",
                sawInit,
                mcpConnected,
                tools,
                model,
                toolCalls,
                usage);
        }

        return new ClaudeRunOutcome(
            failed, problem, message, sawInit, mcpConnected, tools, model, toolCalls, usage);
    }

    /// <summary>
    /// ¿Conectó NUESTRO servidor? Se pregunta por el nombre: que otro servidor MCP del usuario
    /// esté arriba no significa que el de Atalaya lo esté, y es el de Atalaya el que trae las
    /// herramientas sin las cuales la auditoría no puede reportar nada.
    /// </summary>
    private static bool McpIsConnected(JsonElement init)
    {
        if (!init.TryGetProperty("mcp_servers", out JsonElement servers)
            || servers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement server in servers.EnumerateArray())
        {
            if (Str(server, "name") == AuditorTools.ServerName)
            {
                return string.Equals(Str(server, "status"), "connected", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static (int ToolCalls, string Text) ReadAssistant(JsonElement e)
    {
        if (!e.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return (0, string.Empty);
        }

        int calls = 0;
        var text = new System.Text.StringBuilder();

        foreach (JsonElement block in content.EnumerateArray())
        {
            switch (Str(block, "type"))
            {
                case "tool_use":
                    calls++;
                    break;

                case "text" when Str(block, "text") is { Length: > 0 } chunk:
                    text.Append(chunk);
                    break;

                // El pensamiento NO se emite. La columna de actividad es lo que el auditor dice,
                // no lo que rumia, y Copilot tampoco lo manda: enseñar uno de los dos razonando y
                // el otro no haría que parecieran distintos por el continente y no por el fondo.
            }
        }

        return (calls, text.ToString());
    }

    private static (bool Failed, AgentProblem Problem, string Message, UsageSample? Usage) ReadResult(
        JsonElement e, ref decimal costSoFar)
    {
        // is_error manda. `subtype` dice "success" incluso cuando la sesión murió con un 404 del
        // modelo — comprobado contra el CLI real.
        bool failed = Bool(e, "is_error");
        string text = Str(e, "result");
        UsageSample? usage = ReadUsage(e, ref costSoFar);

        if (!failed)
        {
            return (false, AgentProblem.None, text, usage);
        }

        AgentProblem problem = ClaudeFailure.Classify(
            text, Str(e, "terminal_reason"), Int(e, "api_error_status"));

        return (true, problem, ClaudeCodeHelp.For(problem, text), usage);
    }

    /// <summary>
    /// El uso de un turno. El CLI da tokens de verdad y un <c>total_cost_usd</c> que es
    /// <b>tarifa de lista</b> —lo dice él mismo con <c>costBasis: "list"</c>—, no lo que factura
    /// una suscripción. Se guarda con su unidad puesta para que nadie lo sume con las peticiones
    /// premium de Copilot; el porqué está en <see cref="ClaudeUsage.ListPriceUnit"/>.
    /// <para>
    /// <b>Y las dos cifras no tienen el mismo alcance</b>, que es algo que hubo que medir (N-2, F16)
    /// porque en una sesión de un solo turno —lo único que había hasta ahora— no se distingue.
    /// En una conversación de dos turnos, <c>usage</c> es de CADA turno y <c>total_cost_usd</c> es
    /// ACUMULADO: el segundo coste menos el primero da exactamente lo que cuestan los tokens del
    /// segundo turno a las tarifas publicadas, al último decimal. Sumar la cifra de cada turno
    /// habría contado el primero tantas veces como turnos hubiera. Así que aquí se resta.
    /// </para>
    /// </summary>
    private static UsageSample? ReadUsage(JsonElement e, ref decimal costSoFar)
    {
        if (!e.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        decimal? cost = null;
        if (e.TryGetProperty("total_cost_usd", out JsonElement c) && c.ValueKind == JsonValueKind.Number)
        {
            decimal cumulative = c.GetDecimal();
            // Nunca negativo: si el CLI dejara de acumular, un turno gratis es una lectura mucho
            // menos dañina que un coste que resta de los agregados.
            cost = Math.Max(0m, cumulative - costSoFar);
            costSoFar = Math.Max(costSoFar, cumulative);
        }

        return new UsageSample(
            Long(usage, "input_tokens"),
            Long(usage, "output_tokens"),
            cost,
            null,
            Long(usage, "cache_read_input_tokens"),
            Long(usage, "cache_creation_input_tokens"),
            cost is null ? null : ClaudeUsage.ListPriceUnit);
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out JsonElement v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out long n)
            ? n
            : 0;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out int n)
            ? n
            : null;

    private static IEnumerable<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
            {
                yield return s;
            }
        }
    }
}
