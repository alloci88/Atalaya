using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atalaya.ClaudeCode;

/// <summary>
/// El servidor MCP de Atalaya: expone al auditor de Claude Code EXACTAMENTE las mismas
/// herramientas que ve Copilot, y nada más (F14).
/// <para>
/// <b>Habla JSON-RPC 2.0 delimitado por saltos de línea</b>, que es lo que el CLI de
/// <c>claude</c> emite y espera por stdio. La superficie se verificó contra el CLI real (2.1.252)
/// antes de escribir nada: <c>initialize</c> → <c>notifications/initialized</c> →
/// <c>tools/list</c> → N × <c>tools/call</c>. Es todo lo que el cliente usa; lo demás se contesta
/// con «método no encontrado» en vez de romper la conexión.
/// </para>
/// <para>
/// <b>Trabaja sobre streams, no sobre la consola</b>, y ésa es la decisión que lo hace
/// comprobable: en producción los streams son la tubería con nombre que le llega del puente
/// (<c>Atalaya.Mcp</c>), y en los tests son dos <c>MemoryStream</c>. El circuito entero —handshake,
/// catálogo, llamada, respuesta— se prueba sin lanzar un proceso ni tener suscripción de nadie.
/// </para>
/// </summary>
public sealed class AtalayaMcpServer
{
    /// <summary>
    /// La versión de protocolo que se contesta cuando el cliente no propone ninguna. Cuando la
    /// propone —y el CLI la propone— se le devuelve la SUYA: es lo que manda la especificación de
    /// MCP para negociar, y discutirla no aporta nada cuando se implementa el subconjunto común.
    /// </summary>
    public const string FallbackProtocolVersion = "2025-06-18";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IReadOnlyDictionary<string, McpTool> _tools;
    private readonly Action<string>? _trace;

    public AtalayaMcpServer(IEnumerable<McpTool> tools, Action<string>? trace = null)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _trace = trace;
    }

    private int _toolCalls;

    /// <summary>Cuántas veces llamó el auditor a una tool. Va a las métricas de la sesión.</summary>
    /// <remarks>
    /// Se incrementa con <see cref="Interlocked"/> porque desde F16 las peticiones se atienden en
    /// paralelo: un <c>++</c> desde dos hilos pierde cuentas en silencio, que es la peor forma de
    /// equivocarse en un contador que va a una métrica.
    /// </remarks>
    public int ToolCalls => Volatile.Read(ref _toolCalls);

    /// <summary>
    /// Atiende peticiones hasta que <paramref name="input"/> se cierra (el CLI terminó) o se
    /// cancela. No lanza por una línea mal formada ni por una tool que revienta: contestar un error
    /// tipado deja al modelo reintentar, mientras que tirar la conexión lo deja sin herramientas y
    /// convierte una unidad recuperable en una sesión perdida.
    /// <para>
    /// <b>Cada petición se atiende EN PARALELO, y no es una optimización</b> (F16). Con las tools
    /// de auditoría —todas instantáneas— un bucle secuencial bastaba. Las del arreglo no lo son:
    /// <c>ask_user</c> espera a una persona, <c>apply_edit</c> se queda en la puerta mientras la
    /// sesión está en pausa y <c>run_build_and_tests</c> compila la solución entera. Atendiendo de
    /// una en una, cualquiera de las tres dejaría al servidor mudo durante minutos —sin contestar
    /// ni siquiera un <c>ping</c>—, y un cliente que no obtiene respuesta da al servidor por caído.
    /// Las respuestas pueden salir desordenadas y eso es legítimo: JSON-RPC correlaciona por
    /// <c>id</c>, no por orden.
    /// </para>
    /// </summary>
    public async Task ServeAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        // Una sola pluma para todos: dos respuestas escribiéndose a la vez se entrelazarían y
        // ninguna de las dos sería JSON.
        using var pen = new SemaphoreSlim(1, 1);
        var inFlight = new List<Task>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    return;             // El cliente cerró la tubería: fin normal.
                }

                if (line.Trim().Length == 0)
                {
                    continue;
                }

                inFlight.RemoveAll(t => t.IsCompleted);
                inFlight.Add(Task.Run(() => AnswerAsync(line, writer, pen, ct), ct));
            }
        }
        finally
        {
            // Lo que quedara a medio contestar se deja terminar antes de soltar la pluma: una
            // respuesta escrita sobre un writer ya dispuesto es una excepción sin dueño.
            try
            {
                await Task.WhenAll(inFlight);
            }
            catch (Exception)
            {
                // Ya se está cerrando; el detalle de cada una ya se trazó donde ocurrió.
            }
        }
    }

    /// <summary>Contesta UNA petición. Una tool que revienta no puede tumbar la conexión.</summary>
    private async Task AnswerAsync(string line, TextWriter writer, SemaphoreSlim pen, CancellationToken ct)
    {
        JsonNode? response;
        try
        {
            response = Handle(line);
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"MCP: fallo atendiendo una petición ({ex.Message})");
            return;
        }

        if (response is null)
        {
            return;
        }

        await pen.WaitAsync(ct);
        try
        {
            await writer.WriteLineAsync(response.ToJsonString());
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"MCP: no se pudo escribir la respuesta ({ex.Message})");
        }
        finally
        {
            pen.Release();
        }
    }

    /// <summary>
    /// Una petición, una respuesta (o null si era una notificación, que por definición no se
    /// contesta). Es <c>internal</c> y separada del bucle a propósito: así el test puede afirmar
    /// sobre el JSON exacto que se devuelve sin montar streams.
    /// </summary>
    internal JsonNode? Handle(string line)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            // Sin JSON no hay id al que contestar; la especificación dice que se responde con id
            // nulo. Y sobre todo: NO se corta la conexión.
            _trace?.Invoke($"MCP: linea ilegible ({ex.Message})");
            return Error(null, -32700, "Parse error");
        }

        if (request is not JsonObject message)
        {
            return Error(null, -32600, "Invalid Request");
        }

        string method = message["method"]?.GetValue<string>() ?? string.Empty;
        JsonNode? id = message["id"];

        // Una notificación no lleva id y no se contesta NUNCA — contestarla es lo que hace que
        // algunos clientes se queden esperando un turno que ya dieron por cerrado.
        if (id is null)
        {
            _trace?.Invoke($"MCP: notificación {method}");
            return null;
        }

        switch (method)
        {
            case "initialize":
                return Ok(id, Initialize(message["params"]));

            case "ping":
                return Ok(id, new JsonObject());

            case "tools/list":
                return Ok(id, ToolCatalog());

            case "tools/call":
                return Ok(id, CallTool(message["params"]));

            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private JsonNode Initialize(JsonNode? parameters)
    {
        string version = parameters?["protocolVersion"]?.GetValue<string>() ?? FallbackProtocolVersion;
        _trace?.Invoke($"MCP: initialize (protocolo {version})");

        return new JsonObject
        {
            ["protocolVersion"] = version,
            // Solo tools. Atalaya no ofrece recursos ni prompts al auditor: el código viaja en el
            // prompt de la sesión, como siempre, y darle un canal para pedir ficheros sería
            // justamente la superficie que este proveedor no debe tener.
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "atalaya",
                ["version"] = "1.0.0",
            },
        };
    }

    private JsonNode ToolCatalog()
    {
        var tools = new JsonArray();
        foreach (McpTool tool in _tools.Values)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private JsonNode CallTool(JsonNode? parameters)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
        if (!_tools.TryGetValue(name, out McpTool? tool))
        {
            // Un nombre que no existe se contesta como error DE LA TOOL (isError), no como error
            // de protocolo: así el modelo lo lee, se corrige y sigue, que es lo que se quiere.
            return ToolResult($"No existe la herramienta «{name}».", isError: true);
        }

        Interlocked.Increment(ref _toolCalls);

        try
        {
            JsonElement arguments = ToElement(parameters?["arguments"]);
            object? result = tool.Handler(arguments);
            return ToolResult(JsonSerializer.Serialize(result, Json), isError: false);
        }
        catch (Exception ex)
        {
            // La aplicación rechaza payloads inválidos con un error tipado y eso es NORMAL: un
            // ULID que no está en la lista, una severidad inventada. El modelo tiene que poder
            // leerlo y corregirse, así que viaja como resultado, no como excepción.
            _trace?.Invoke($"MCP: {name} rechazada ({ex.Message})");
            return ToolResult($"{{\"accepted\":false,\"error\":{JsonSerializer.Serialize(ex.Message)}}}", isError: true);
        }
    }

    /// <summary>
    /// Los argumentos como <see cref="JsonElement"/>. Se pasa por texto a propósito: los handlers
    /// leen con la misma API que usarían sobre cualquier JSON, y un <c>null</c> se convierte en un
    /// objeto vacío para que nadie tenga que comprobarlo.
    /// </summary>
    private static JsonElement ToElement(JsonNode? node)
        => JsonDocument.Parse(node?.ToJsonString() ?? "{}").RootElement.Clone();

    private static JsonNode ToolResult(string text, bool isError)
        => new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };

    private static JsonNode Ok(JsonNode? id, JsonNode result)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

    private static JsonNode Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
}
