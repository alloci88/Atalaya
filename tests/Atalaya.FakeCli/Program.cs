using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Atalaya.FakeCli;

/// <summary>
/// Un <c>claude</c> de mentira que sí habla MCP (F16).
/// <para>
/// <b>Por qué hacía falta uno nuevo.</b> El CLI falso de F14 es un <c>.cmd</c> que escupe un guion
/// de eventos: sirve para probar cómo se LEE la salida, y para eso sigue siendo el bueno. Pero una
/// sesión de arreglo no se puede probar así — lo que hay que ejercitar es el otro sentido: que el
/// agente LLAME a las herramientas, que la aplicación edite el clon de verdad, que un permiso
/// denegado vuelva como decisión y que <c>fix_done</c> cierre. Nada de eso ocurre si nadie llama a
/// nada.
/// </para>
/// <para>
/// <b>Qué hace exactamente lo que hace el de verdad.</b> Lee <c>--mcp-config</c>, LANZA el puente
/// declarado ahí como proceso hijo con su stdio redirigido, y habla JSON-RPC con él —
/// <c>initialize</c>, <c>tools/list</c>, N × <c>tools/call</c>—. Es la cadena de producción entera:
/// CLI → puente → tubería con nombre → servidor MCP → toolbox de la aplicación. Lo único de
/// mentira es quién decide qué llamar: en vez de un modelo, un guion escrito por el test.
/// </para>
/// <para>
/// <b>Y también hace de conversación.</b> Con <c>--input-format stream-json</c> el prompt y cada
/// mensaje del usuario llegan por stdin como una línea JSON, y cada turno se cierra con un evento
/// <c>result</c>; el guion se divide en turnos con <c>---</c>. Los tokens van por turno y el coste
/// ACUMULADO, que es como lo hace el CLI real (comprobado) y lo que el lector tiene que saber
/// restar.
/// </para>
/// <para>
/// <b>El guion se busca junto al fichero de <c>--mcp-config</c></b>, no en un argumento propio ni
/// en una variable de entorno. Ni argumento —los argumentos son EXACTAMENTE los que le pasa
/// Atalaya, y añadir uno haría que este programa dejara de recibir lo que recibe el de verdad— ni
/// variable de entorno, que es del proceso entero y dos tests a la vez se la pisarían. El
/// directorio de la configuración MCP es de una sola sesión, así que ahí no se pisa nadie.
/// </para>
/// </summary>
public static class Program
{
    /// <summary>El guion, junto al fichero de configuración MCP de esta sesión.</summary>
    public const string ScriptFile = "fake-script.txt";

    /// <summary>Dónde se deja lo que pasó: argumentos recibidos y respuesta de cada tool.</summary>
    public const string TranscriptFile = "fake-transcript.txt";

    private static string? _sessionDirectory;

    private static readonly List<string> Transcript = new();

    /// <summary>Lo que consume cada llamada al modelo en el CLI falso. Números redondos a propósito.</summary>
    private const int CallInput = 10;

    private const int CallOutput = 5;

    private const int CallCacheRead = 100;

    private const int CallCacheWrite = 20;

    /// <summary>
    /// Lo que el encargo del sistema añade a la primera llamada y que los eventos de la
    /// conversación NO informan. El CLI real hace justo esto: <c>modelUsage</c> declaraba 986
    /// tokens de entrada donde los eventos sumaban 40. Está aquí para que el ajuste del final del
    /// turno tenga algo que ajustar — si el fake cuadrara solo, no probaría nada.
    /// </summary>
    private const int SystemPromptInput = 900;

    /// <summary>
    /// Con este fichero en el directorio de la sesión, el CLI falso NO informa consumo: ni por
    /// llamada ni en el evento final. Sirve para ejercitar el único caso en el que «sin tokens
    /// registrados» es verdad — un proveedor que no cuenta nada.
    /// </summary>
    public const string SilentFile = "fake-silent.txt";

    private static Process? _bridge;
    private static int _rpcId = 100;
    private static decimal _costSoFar;

    /// <summary>Llamadas al modelo emitidas, para poder cuadrar como cuadra el de verdad.</summary>
    private static int _calls;

    private static int _turnCalls;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        // `claude auth status --json`: la comprobación de disponibilidad. Se contesta que sí, que
        // es lo que permite que el proveedor de producción llegue a lanzar una sesión.
        if (args.Length > 0 && args[0] == "auth")
        {
            Console.WriteLine("""{"loggedIn":true,"email":"banco@atalaya.test","authMethod":"claude.ai"}""");
            return 0;
        }

        Record("args: " + string.Join(" ", args.Select(a => a.Length == 0 ? "\"\"" : a)));

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Record("crash: " + ex);
            return 9;
        }
        finally
        {
            Flush();
            StopBridge();
        }
    }

    private static int Run(string[] args)
    {
        string? configPath = Argument(args, "--mcp-config");
        string pipeName = string.Empty;
        string? bridge = null;
        if (configPath is not null)
        {
            _sessionDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            pipeName = PipeNameOf(configPath, out string command) ?? string.Empty;
            bridge = command;
        }

        bool connected = false;
        var tools = new List<string>();

        if (bridge is { Length: > 0 } && pipeName.Length > 0)
        {
            connected = StartBridge(bridge, pipeName, tools);
        }

        string[] script = ReadScript();
        int turn = 0;
        int exitCode = 0;

        foreach (string[] block in Blocks(script))
        {
            // El prompt del primer turno y cada mensaje posterior del usuario llegan por stdin.
            if (Console.In.ReadLine() is not { } incoming)
            {
                break;                  // Atalaya cerró la conversación: fin normal.
            }

            Record($"turn {++turn} in: {Shorten(incoming)}");
            EmitInit(tools, connected);

            foreach (string line in block)
            {
                if (!Step(line, ref exitCode))
                {
                    return exitCode;
                }
            }

            EmitResult();
        }

        return exitCode;
    }

    /// <summary>Una orden del guion. Devuelve false cuando el guion manda terminar en seco.</summary>
    private static bool Step(string line, ref int exitCode)
    {
        string verb = line.Split(' ', 2)[0];
        string rest = line.Length > verb.Length ? line[(verb.Length + 1)..] : string.Empty;

        switch (verb)
        {
            case "text":
                EmitAssistant(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = rest,
                });
                return true;

            case "call":
            {
                string tool = rest.Split(' ', 2)[0];
                string arguments = rest.Length > tool.Length ? rest[(tool.Length + 1)..] : "{}";
                CallTool(tool, arguments);
                return true;
            }

            case "wait":
            {
                // Espera a que el test cree un fichero en el directorio de la sesión. Es lo que
                // permite escribir guiones SIN carreras: sin esto, un turno que solo dice una
                // frase termina en microsegundos y el test no llega a tiempo de reaccionar.
                // Un minuto de margen. No es que se espere tanto —el test crea el fichero en
                // milisegundos—, es que con la suite entera corriendo en paralelo la maquina se
                // satura y una espera corta convertiria una carrera perdida en un fallo de test
                // que no habla de nada.
                string flag = Path.Combine(_sessionDirectory ?? ".", rest);
                for (int i = 0; i < 1200 && !File.Exists(flag); i++)
                {
                    Thread.Sleep(50);
                }

                Record($"wait {rest} -> {(File.Exists(flag) ? "ok" : "timeout")}");
                return true;
            }

            case "die":
                // El CLI se muere a mitad, sin `result`. Lo importante es que Atalaya no se cuelgue.
                exitCode = rest.Length > 0 && int.TryParse(rest, out int code) ? code : 1;
                return false;

            default:
                Record("guion: verbo desconocido " + verb);
                return true;
        }
    }

    // ---------------------------------------------------------------- el puente y las tools

    private static bool StartBridge(string executable, string pipeName, List<string> tools)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(pipeName);

        _bridge = Process.Start(psi);
        if (_bridge is null)
        {
            Record("puente: no se pudo lanzar " + executable);
            return false;
        }

        JsonNode? init = Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" });
        if (init?["result"]?["serverInfo"]?["name"]?.GetValue<string>() != "atalaya")
        {
            Record("puente: el servidor no se identificó");
            return false;
        }

        Notify("notifications/initialized");

        JsonNode? list = Rpc("tools/list", null);
        if (list?["result"]?["tools"] is JsonArray declared)
        {
            foreach (JsonNode? tool in declared)
            {
                if (tool?["name"]?.GetValue<string>() is { Length: > 0 } name)
                {
                    tools.Add("mcp__atalaya__" + name);
                }
            }
        }

        Record("tools: " + string.Join(",", tools));
        return true;
    }

    private static void CallTool(string tool, string arguments)
    {
        EmitAssistant(new JsonObject
        {
            ["type"] = "tool_use",
            ["name"] = "mcp__atalaya__" + tool,
        });

        JsonNode? response = Rpc("tools/call", new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = JsonNode.Parse(arguments),
        });

        string text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "(sin respuesta)";
        bool isError = response?["result"]?["isError"]?.GetValue<bool>() ?? false;
        Record($"{tool} -> {(isError ? "error " : string.Empty)}{text}");
    }

    private static JsonNode? Rpc(string method, JsonNode? parameters)
    {
        if (_bridge is null)
        {
            return null;
        }

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = _rpcId++,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        _bridge.StandardInput.WriteLine(request.ToJsonString());
        _bridge.StandardInput.Flush();

        string? line = _bridge.StandardOutput.ReadLine();
        return line is null ? null : JsonNode.Parse(line);
    }

    private static void Notify(string method)
    {
        if (_bridge is null)
        {
            return;
        }

        _bridge.StandardInput.WriteLine(
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString());
        _bridge.StandardInput.Flush();
    }

    private static void StopBridge()
    {
        try
        {
            if (_bridge is { HasExited: false })
            {
                _bridge.StandardInput.Close();
                _bridge.WaitForExit(2000);
            }
        }
        catch (Exception)
        {
            // Se está cerrando de todos modos.
        }
    }

    // ---------------------------------------------------------------- eventos hacia Atalaya

    private static void EmitInit(IReadOnlyList<string> tools, bool connected)
        => Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["model"] = "claude-opus-5",
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["mcp_servers"] = new JsonArray(new JsonObject
            {
                ["name"] = "atalaya",
                ["status"] = connected ? "connected" : "failed",
            }),
        });

    /// <summary>
    /// Una respuesta del modelo, con su consumo. <b>El mismo <c>usage</c> se repite en cada bloque
    /// de contenido</b> —así lo hace el CLI real: una respuesta con pensamiento, texto y llamada a
    /// herramienta son tres eventos con el mismo <c>id</c> y el mismo <c>usage</c>—, y aquí se
    /// emite un bloque por evento con id propio, que es el caso simple del mismo formato.
    /// </summary>
    private static void EmitAssistant(JsonObject block)
    {
        _calls++;
        _turnCalls++;
        var message = new JsonObject
        {
            ["id"] = "msg_" + _calls,
            ["content"] = new JsonArray(block),
        };

        if (!Silent)
        {
            message["usage"] = new JsonObject
            {
                ["input_tokens"] = CallInput,
                ["output_tokens"] = CallOutput,
                ["cache_read_input_tokens"] = CallCacheRead,
                ["cache_creation_input_tokens"] = CallCacheWrite,
            };
        }

        Emit(new JsonObject { ["type"] = "assistant", ["message"] = message });
    }

    /// <summary>El guion pide un proveedor que no informa consumo.</summary>
    private static bool Silent
        => _sessionDirectory is not null && File.Exists(Path.Combine(_sessionDirectory, SilentFile));

    /// <summary>
    /// El cierre de un turno, con las TRES cifras que el CLI real informa y que no dicen lo mismo:
    /// <c>usage</c> es del turno y se queda corto, <c>modelUsage</c> es el agregado acumulado y es
    /// el bueno, y <c>total_cost_usd</c> es acumulado. Es lo que el lector de Atalaya tiene que
    /// saber restar y cuadrar.
    /// </summary>
    private static void EmitResult()
    {
        var result = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["result"] = "turno cerrado",
        };

        if (!Silent)
        {
            _costSoFar += 0.01m;
            result["total_cost_usd"] = _costSoFar;
            result["usage"] = new JsonObject
            {
                ["input_tokens"] = _turnCalls * CallInput,
                ["output_tokens"] = _turnCalls * CallOutput,
                ["cache_read_input_tokens"] = _turnCalls * CallCacheRead,
                ["cache_creation_input_tokens"] = _turnCalls * CallCacheWrite,
            };
            result["modelUsage"] = new JsonObject
            {
                ["claude-opus-5"] = new JsonObject
                {
                    ["inputTokens"] = (_calls * CallInput) + SystemPromptInput,
                    ["outputTokens"] = _calls * CallOutput,
                    ["cacheReadInputTokens"] = _calls * CallCacheRead,
                    ["cacheCreationInputTokens"] = _calls * CallCacheWrite,
                },
            };
        }

        Emit(result);
        _turnCalls = 0;
    }

    private static void Emit(JsonNode node)
    {
        Console.Out.WriteLine(node.ToJsonString());
        Console.Out.Flush();
    }

    // ---------------------------------------------------------------- ayudas

    private static string[] ReadScript()
    {
        string? path = _sessionDirectory is null ? null : Path.Combine(_sessionDirectory, ScriptFile);
        return path is not null && File.Exists(path)
            ? File.ReadAllLines(path)
            : Array.Empty<string>();
    }

    private static IEnumerable<string[]> Blocks(string[] script)
    {
        var current = new List<string>();
        foreach (string raw in script)
        {
            string line = raw.Trim();
            if (line == "---")
            {
                yield return current.ToArray();
                current.Clear();
                continue;
            }

            if (line.Length > 0)
            {
                current.Add(line);
            }
        }

        yield return current.ToArray();
    }

    private static string? Argument(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>El nombre de la tubería y el ejecutable del puente, leídos del fichero de MCP.</summary>
    private static string? PipeNameOf(string configPath, out string command)
    {
        command = string.Empty;
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(configPath));
            JsonNode? server = root?["mcpServers"]?["atalaya"];
            command = server?["command"]?.GetValue<string>() ?? string.Empty;
            return server?["args"]?[0]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            Record("mcp-config ilegible: " + ex.Message);
            return null;
        }
    }

    private static void Record(string line)
    {
        lock (Transcript)
        {
            Transcript.Add(line);
        }
    }

    private static void Flush()
    {
        if (_sessionDirectory is null)
        {
            return;
        }

        string path = Path.Combine(_sessionDirectory, TranscriptFile);

        try
        {
            lock (Transcript)
            {
                File.WriteAllLines(path, Transcript, new UTF8Encoding(false));
            }
        }
        catch (IOException)
        {
        }
    }

    private static string Shorten(string text)
    {
        string flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= 400 ? flat : flat[..400] + "…";
    }
}
