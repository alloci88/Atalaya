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
/// <param name="TerminalReason">
/// Cómo dice el CLI que terminó. Importa para el corte de F21: <c>aborted_tools</c> es <b>su</b>
/// declaración de que se abortó con herramientas pendientes y <b>ninguna petición en vuelo</b>, que
/// es exactamente la condición que hace que el corte no pierda cuentas ni pague nada a medias.
/// </param>
public sealed record ClaudeRunOutcome(
    bool Failed,
    AgentProblem Problem,
    string Message,
    bool Started,
    bool McpConnected,
    IReadOnlyList<string> Tools,
    string? Model,
    int ToolCalls,
    UsageSample? Usage,
    string? TerminalReason = null);

/// <summary>
/// Cómo terminó UN turno de una conversación (F16). Una sesión de auditoría tiene exactamente uno;
/// una de arreglo tiene tantos como veces hable el usuario.
/// </summary>
/// <remarks>
/// El consumo NO viaja aquí: va por su propio canal y según ocurre, llamada a llamada, porque el
/// pie de la pantalla tiene que moverse mientras el agente trabaja y no solo cuando acaba de
/// hablar. Ver <see cref="ClaudeStreamReader"/>.
/// </remarks>
public sealed record ClaudeTurn(bool Failed, AgentProblem Problem, string Message);

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
    private readonly Action<UsageSample>? _onUsage;
    private readonly Action<ClaudeTurn>? _onTurn;

    /// <summary>Peticiones al modelo abiertas y todavía sin cerrar. Ver <see cref="AccountingIsComplete"/>.</summary>
    private int _openCalls;

    /// <summary>Llamadas cuyo consumo FINAL ya se conoce, por su <c>message_delta</c>.</summary>
    private int _settledCalls;

    /// <summary>
    /// El CLI está reenviando los eventos crudos de la API (<c>--include-partial-messages</c>). Sin
    /// ellos no hay cuentas por llamada, solo el agregado del final.
    /// </summary>
    private bool _sawPartialMessages;

    /// <summary>
    /// <b>¿Están ya las cuentas de todo lo consumido hasta este instante?</b> (F21 §1). Es la
    /// condición que gobierna el corte, y por eso se pregunta desde fuera mientras el flujo corre.
    /// <para>
    /// Cierto cuando el CLI publica sus eventos crudos, alguna llamada ha cerrado con su consumo
    /// final, y <b>no queda ninguna petición en vuelo</b>. Esa última parte es la que importa: una
    /// petición ya enviada y cortada a medias <b>se factura igual y no aparece en ningún sitio</b>
    /// —medido: al interrumpir a mitad de respuesta, el modelo principal desaparece entero del
    /// <c>modelUsage</c> del <c>result</c>—, que es lo peor de los dos mundos. Con la respuesta de
    /// <c>unit_done</c> retenida no hay ninguna en vuelo por construcción, pero el corte se
    /// pregunta igual: una salvaguarda que depende de un razonamiento no es una salvaguarda.
    /// </para>
    /// </summary>
    public bool AccountingIsComplete
        => _sawPartialMessages && Volatile.Read(ref _openCalls) == 0 && Volatile.Read(ref _settledCalls) > 0;

    /// <summary>Cuántas llamadas han cerrado con su consumo final declarado por el CLI.</summary>
    public int SettledCalls => Volatile.Read(ref _settledCalls);

    /// <param name="onUsage">
    /// El consumo, <b>según ocurre</b>: una muestra por cada llamada al modelo, y un ajuste al
    /// cerrar el turno. Ver la nota de arriba sobre por qué hacen falta las dos cosas.
    /// </param>
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
    public ClaudeStreamReader(
        Action<string>? onText = null,
        Action<UsageSample>? onUsage = null,
        Action<ClaudeTurn>? onTurn = null)
    {
        _onText = onText;
        _onUsage = onUsage;
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
        bool sawResult = false;
        string? terminalReason = null;

        // Lo que ya se ha REPORTADO de esta invocación. El CLI cuenta en acumulado y la aplicación
        // suma lo que le llega, así que cada muestra es una diferencia contra esto.
        var emitted = new Consumption();

        // Y el respaldo, para el CLI que no publique `modelUsage`: el `usage` del evento final es
        // del turno, así que se va acumulando a mano.
        var perTurn = new Consumption();

        // Las llamadas de este turno cuya muestra ya se ha emitido, y los mensajes ya contados: el
        // CLI parte un mismo mensaje del modelo en varios eventos —uno por bloque de contenido— y
        // REPITE su usage en todos. Contarlos por evento multiplicaría el consumo por tres.
        var counted = new HashSet<string>(StringComparer.Ordinal);
        int callsThisTurn = 0;

        // Lo ya emitido de la llamada EN CURSO, y cuál es. El evento `assistant` trae un consumo
        // PARCIAL —el del instante en que el modelo empieza a contestar— y el `message_delta` que
        // cierra el mensaje trae el FINAL; emitir la diferencia deja el pie moviéndose con lo que
        // se sabe y acaba en la cifra exacta, sin contar nada dos veces (F21 §1).
        var callEmitted = new Consumption();
        string currentCall = string.Empty;
        bool currentCallCounted = false;

        void OpenCall(string id)
        {
            if (string.Equals(id, currentCall, StringComparison.Ordinal))
            {
                return;
            }

            currentCall = id;
            callEmitted = new Consumption();
            currentCallCounted = false;
        }

        // El coste acumulado que el CLI lleva declarado. Ver ReadCost: `total_cost_usd` es de la
        // SESIÓN entera, así que el coste de un turno es la diferencia.
        decimal costSoFar = 0m;

        // Y lo que suman los turnos, para el desenlace: lo mismo que va llegando suelto, junto.
        decimal? totalCost = null;

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

                    OpenCall(MessageId(e));

                    // El consumo de ESTA llamada, en cuanto se sabe. Es lo que hace que el pie se
                    // mueva mientras el agente trabaja: con un arreglo, todo —leer, preguntar,
                    // editar, compilar— cabe en un solo turno, así que esperar al `result` es
                    // esperar al final de la sesión entera y enseñar «0 llamadas» hasta entonces.
                    if (ReadCallUsage(e, counted) is { } call)
                    {
                        Consumption advance = call.Minus(callEmitted);
                        emitted.Add(advance);
                        callEmitted.Add(advance);
                        callsThisTurn++;
                        currentCallCounted = true;
                        _onUsage?.Invoke(advance.ToSample(model, calls: 1));
                    }

                    break;

                // Los eventos CRUDOS de la API, que el CLI reenvía con
                // `--include-partial-messages`. Solo interesan los dos que enmarcan una llamada:
                // `message_start` la abre y `message_delta` la cierra CON SU CONSUMO FINAL. Es lo
                // que hace que las cuentas existan antes del evento final (F21 §1) — el `usage`
                // del evento `assistant` es parcial y se queda corto (medido: decía 5 donde el
                // `result` decía 20). Lo demás del flujo crudo —cada trocito de texto— se ignora.
                case "stream_event" when e.TryGetProperty("event", out JsonElement raw):
                    switch (Str(raw, "type"))
                    {
                        case "message_start":
                            _sawPartialMessages = true;
                            Interlocked.Increment(ref _openCalls);
                            OpenCall(raw.TryGetProperty("message", out JsonElement started)
                                ? Str(started, "id")
                                : string.Empty);
                            break;

                        case "message_delta" when raw.TryGetProperty("usage", out JsonElement final)
                                                  && final.ValueKind == JsonValueKind.Object:
                            _sawPartialMessages = true;
                            Consumption closed = Consumption.Read(final);
                            closed.Reasoning = final.TryGetProperty("output_tokens_details", out JsonElement details)
                                ? Long(details, "thinking_tokens")
                                : 0;
                            Consumption rest = closed.Minus(callEmitted);
                            emitted.Add(rest);
                            callEmitted.Add(rest);

                            // La llamada ya se contó si trajo un `assistant` con consumo; si no
                            // —un turno que solo razona—, se cuenta aquí: haberla, la hubo.
                            int newCall = currentCallCounted ? 0 : 1;
                            callsThisTurn += newCall;
                            currentCallCounted = true;
                            Interlocked.Increment(ref _settledCalls);
                            if (Volatile.Read(ref _openCalls) > 0)
                            {
                                Interlocked.Decrement(ref _openCalls);
                            }

                            _onUsage?.Invoke(rest.ToSample(model, calls: newCall));
                            break;
                    }

                    break;

                case "result":
                    sawResult = true;
                    terminalReason = Str(e, "terminal_reason") is { Length: > 0 } tr ? tr : null;
                    (failed, problem, message) = ReadResult(e, ref costSoFar, out decimal? turnCost);

                    // El ajuste se calcula SIEMPRE, haya quien lo escuche o no: cuadra las cuentas
                    // de la invocación, y `_onUsage?.Invoke(Settle(...))` no habría llegado a
                    // llamarlo sin oyente — el operador condicional no evalúa ni los argumentos.
                    UsageSample settled = Settle(e, emitted, perTurn, model, turnCost, callsThisTurn);
                    totalCost = Sum(totalCost, turnCost);
                    callsThisTurn = 0;
                    _onUsage?.Invoke(settled);
                    _onTurn?.Invoke(new ClaudeTurn(failed, problem, message));
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
                Total(emitted, model, totalCost));
        }

        return new ClaudeRunOutcome(
            failed, problem, message, sawInit, mcpConnected, tools, model, toolCalls,
            Total(emitted, model, totalCost), terminalReason);
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

    /// <summary>El id del mensaje del modelo que trae este evento, o vacío si no lo dice.</summary>
    private static string MessageId(JsonElement e)
        => e.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object
            ? Str(message, "id")
            : string.Empty;

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

    private static (bool Failed, AgentProblem Problem, string Message) ReadResult(
        JsonElement e, ref decimal costSoFar, out decimal? turnCost)
    {
        // is_error manda. `subtype` dice "success" incluso cuando la sesión murió con un 404 del
        // modelo — comprobado contra el CLI real.
        bool failed = Bool(e, "is_error");
        string text = Str(e, "result");
        turnCost = ReadCost(e, ref costSoFar);

        if (!failed)
        {
            return (false, AgentProblem.None, text);
        }

        AgentProblem problem = ClaudeFailure.Classify(
            text, Str(e, "terminal_reason"), Int(e, "api_error_status"));

        return (true, problem, ClaudeCodeHelp.For(problem, text));
    }

    /// <summary>
    /// El coste de ESTE turno. El CLI informa un <c>total_cost_usd</c> que es <b>tarifa de lista</b>
    /// —lo dice él mismo con <c>costBasis: "list"</c>—, no lo que factura una suscripción. Se guarda
    /// con su unidad puesta para que nadie lo sume con las peticiones premium de Copilot; el porqué
    /// está en <see cref="ClaudeUsage.ListPriceUnit"/>.
    /// <para>
    /// <b>Y viene ACUMULADO</b>, cosa que hubo que medir (N-2) porque en una sesión de un solo turno
    /// no se distingue: en una conversación de dos turnos, el coste del segundo menos el del primero
    /// da exactamente lo que cuestan los tokens del segundo a las tarifas publicadas, al último
    /// decimal. Sumar la cifra de cada turno habría contado el primero tantas veces como turnos
    /// hubiera. Así que aquí se resta.
    /// </para>
    /// </summary>
    private static decimal? ReadCost(JsonElement e, ref decimal costSoFar)
    {
        if (!e.TryGetProperty("total_cost_usd", out JsonElement c) || c.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        decimal cumulative = c.GetDecimal();

        // Nunca negativo: si el CLI dejara de acumular, un turno gratis es una lectura mucho menos
        // dañina que un coste que resta de los agregados.
        decimal turn = Math.Max(0m, cumulative - costSoFar);
        costSoFar = Math.Max(costSoFar, cumulative);
        return turn;
    }

    /// <summary>
    /// El consumo de UNA llamada al modelo, o <c>null</c> si este evento no lo trae o si ya se
    /// contó.
    /// <para>
    /// <b>Se descuenta por id de mensaje</b>, y ésa es la trampa: el CLI parte una misma respuesta
    /// del modelo en varios eventos <c>assistant</c> —uno por bloque de contenido: el pensamiento,
    /// el texto, la llamada a herramienta— y <b>repite el mismo <c>usage</c> en todos</b>. Medido:
    /// una respuesta con tres bloques emite tres eventos con <c>input_tokens: 10</c> cada uno. Sin
    /// descontar, el consumo salía multiplicado por el número de bloques.
    /// </para>
    /// </summary>
    private static Consumption? ReadCallUsage(JsonElement e, HashSet<string> counted)
    {
        if (!e.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string id = Str(message, "id");
        if (id.Length > 0 && !counted.Add(id))
        {
            return null;
        }

        return message.TryGetProperty("usage", out JsonElement usage)
               && usage.ValueKind == JsonValueKind.Object
            ? Consumption.Read(usage)
            : null;
    }

    /// <summary>
    /// El AJUSTE del final del turno: la diferencia entre lo que el CLI dice que se lleva gastado y
    /// lo que ya se ha reportado llamada a llamada.
    /// <para>
    /// <b>Por qué hace falta ajustar, y de dónde sale el número bueno.</b> Se midió contra el CLI
    /// real (2.1.252) y las tres cifras del evento final no dicen lo mismo:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>modelUsage</c> es el <b>agregado acumulado de la invocación</b> y es el único que
    /// reproduce el coste que el propio CLI calcula. En una sesión de cuatro llamadas:
    /// <c>in 986 / out 434 / caché 22.760+7.921</c> → 0,021274 $, exacto al último decimal con las
    /// tarifas publicadas.
    /// </item>
    /// <item>
    /// <c>usage</c> es del <b>turno</b> y se queda corto: la misma sesión declaraba <c>in 40</c>
    /// —justo la suma de lo que traían los eventos <c>assistant</c>— contra los 986 reales, y
    /// <c>out 412</c> contra 434. Los eventos de la conversación informan lo que se sabe al empezar
    /// cada respuesta, no el total; la diferencia es sobre todo el encargo del sistema, que se
    /// cuenta una vez.
    /// </item>
    /// <item><c>total_cost_usd</c> es acumulado, como <c>modelUsage</c>, y coherente con él.</item>
    /// </list>
    /// <para>
    /// Así que lo que manda es <c>modelUsage</c>; lo que va llegando llamada a llamada es un
    /// anticipo para que el pie se mueva, y aquí se cuadra. Cuando el CLI no publique
    /// <c>modelUsage</c> —una versión más vieja, otra forma de salida— se cae al <c>usage</c> de
    /// cada turno acumulado a mano, que es lo que había antes de esto.
    /// </para>
    /// <para>
    /// El ajuste cuenta <b>cero llamadas</b>: no es una llamada nueva, es la misma contada mejor.
    /// Salvo que el turno no haya traído ninguna —un proveedor que solo informa al final—, y
    /// entonces cuenta una: un turno es, como mínimo, una llamada.
    /// </para>
    /// </summary>
    private static UsageSample Settle(
        JsonElement result,
        Consumption emitted,
        Consumption perTurn,
        string? model,
        decimal? turnCost,
        int callsThisTurn)
    {
        perTurn.Add(ReadTurnUsage(result));
        Consumption total = ReadModelUsage(result) ?? perTurn;

        Consumption adjustment = total.Minus(emitted);
        emitted.Add(adjustment);

        return adjustment.ToSample(
            model,
            calls: callsThisTurn == 0 ? 1 : 0,
            cost: turnCost,
            costUnit: turnCost is null ? null : ClaudeUsage.ListPriceUnit,
            reconciliation: true);
    }

    /// <summary>El agregado ACUMULADO de la invocación, sumando los modelos que hayan intervenido.</summary>
    private static Consumption? ReadModelUsage(JsonElement result)
    {
        if (!result.TryGetProperty("modelUsage", out JsonElement models)
            || models.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var total = new Consumption();
        bool any = false;
        foreach (JsonProperty model in models.EnumerateObject())
        {
            if (model.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            any = true;
            total.Add(new Consumption
            {
                Input = Long(model.Value, "inputTokens"),
                Output = Long(model.Value, "outputTokens"),
                CacheRead = Long(model.Value, "cacheReadInputTokens"),
                CacheWrite = Long(model.Value, "cacheCreationInputTokens"),
            });
        }

        return any ? total : null;
    }

    /// <summary>
    /// Todo lo que consumió la invocación: los tokens ya cuadrados y el coste de sus turnos
    /// sumado. Es lo mismo que fue llegando suelto por <c>onUsage</c>, junto — quien no escuche
    /// las muestras tiene aquí el total, y las dos cifras no pueden discrepar porque salen del
    /// mismo acumulador. Cuenta <b>cero llamadas</b>: no es una muestra más, es el resumen.
    /// </summary>
    private static UsageSample Total(Consumption emitted, string? model, decimal? cost)
        => emitted.ToSample(
            model, calls: 0, cost: cost, costUnit: cost is null ? null : ClaudeUsage.ListPriceUnit);

    /// <summary>La suma de dos costes que pueden no existir. Null + null sigue siendo null.</summary>
    private static decimal? Sum(decimal? a, decimal? b)
        => a is null ? b : b is null ? a : a + b;

    /// <summary>El <c>usage</c> del evento final, que es de ESTE turno.</summary>
    private static Consumption ReadTurnUsage(JsonElement result)
        => result.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object
            ? Consumption.Read(usage)
            : new Consumption();

    /// <summary>
    /// Cuatro contadores de tokens que se suman y se restan. Mutable a propósito: es un acumulador
    /// de un bucle, y un record inmutable aquí solo añadiría copias.
    /// </summary>
    private sealed class Consumption
    {
        public long Input { get; set; }

        public long Output { get; set; }

        public long CacheRead { get; set; }

        public long CacheWrite { get; set; }

        /// <summary>
        /// Cuántos de los <see cref="Output"/> fueron razonamiento. Es un desglose de la salida, NO
        /// un quinto concepto: no se suma a nada ni se resta de nada (F21 §3).
        /// </summary>
        public long Reasoning { get; set; }

        public static Consumption Read(JsonElement usage) => new()
        {
            Input = Long(usage, "input_tokens"),
            Output = Long(usage, "output_tokens"),
            CacheRead = Long(usage, "cache_read_input_tokens"),
            CacheWrite = Long(usage, "cache_creation_input_tokens"),
        };

        public void Add(Consumption other)
        {
            Input += other.Input;
            Output += other.Output;
            CacheRead += other.CacheRead;
            CacheWrite += other.CacheWrite;
            Reasoning += other.Reasoning;
        }

        /// <summary>
        /// La diferencia, <b>nunca negativa</b>. Si el acumulado del CLI se quedara por debajo de
        /// lo ya reportado, restar de verdad propagaría un consumo negativo a los agregados sin que
        /// nadie lo notara — el mismo blindaje que la fórmula de credits (D-785).
        /// </summary>
        public Consumption Minus(Consumption other) => new()
        {
            Input = Math.Max(0, Input - other.Input),
            Output = Math.Max(0, Output - other.Output),
            CacheRead = Math.Max(0, CacheRead - other.CacheRead),
            CacheWrite = Math.Max(0, CacheWrite - other.CacheWrite),
            Reasoning = Math.Max(0, Reasoning - other.Reasoning),
        };

        public UsageSample ToSample(
            string? model, int calls, decimal? cost = null, string? costUnit = null, bool reconciliation = false)
            => new(
                Input, Output, cost, model, CacheRead, CacheWrite, costUnit, calls, reconciliation, Reasoning);
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
