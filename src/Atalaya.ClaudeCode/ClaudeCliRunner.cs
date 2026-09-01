using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaya.Agents;

namespace Atalaya.ClaudeCode;

/// <summary>Lo que hace falta para una invocación del CLI.</summary>
/// <param name="Prompt">El prompt de la sesión. Viaja por STDIN — ver <see cref="ClaudeCliRunner"/>.</param>
/// <param name="AllowedTools">Los nombres cualificados de las únicas tools permitidas.</param>
/// <param name="McpConfigPath">El fichero con la declaración del servidor MCP de Atalaya.</param>
/// <param name="Model">El modelo, o vacío para dejar que el CLI elija el suyo.</param>
public sealed record ClaudeRun(
    string Prompt,
    IReadOnlyList<string> AllowedTools,
    string McpConfigPath,
    string? Model);

/// <summary>
/// Lanza <c>claude</c> en modo no interactivo y devuelve cómo fue (F14).
/// <para>
/// <b>Las decisiones de esta clase se comprobaron ejecutando el CLI real</b> (2.1.252), que es lo
/// que exige N-2, y cada una responde a algo que se vio:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>El prompt va por STDIN, nunca como argumento.</b> En Windows el CLI es un <c>claude.cmd</c>,
/// así que la línea de órdenes la vuelve a interpretar <c>cmd.exe</c>: un prompt con código dentro
/// —que es exactamente lo que Atalaya manda— trae <c>&amp;</c>, <c>|</c>, <c>^</c> y comillas, y
/// acabaría troceado o ejecutando cualquier cosa. Y además hay un tope de ~32 000 caracteres que
/// una unidad de tamaño normal se salta. Por stdin no hay ni escapado ni tope.
/// </item>
/// <item>
/// <b>El servidor MCP se declara en un FICHERO</b>, serializado con <c>System.Text.Json</c>. El
/// primer intento lo pasó como cadena JSON en la línea de órdenes y el CLI contestó «MCP config is
/// not a valid JSON»: las barras invertidas de una ruta de Windows no sobreviven al paso por la
/// consola. Con un fichero no hay nada que escapar.
/// </item>
/// <item>
/// <b><c>--tools ""</c> y <c>--allowedTools</c> con la lista exacta.</b> Lo primero quita TODAS las
/// herramientas propias del CLI —consola, ficheros, red—; lo segundo deja pasar solo las de
/// Atalaya. El auditor no necesita ninguna otra: el código viaja en el prompt, como siempre. Es la
/// misma salvaguarda que el <c>OnPermissionRequest</c> que rechaza todo en Copilot.
/// </item>
/// <item>
/// <b><c>--strict-mcp-config</c></b> para que NO se cuelen los servidores MCP que el usuario tenga
/// configurados en su máquina. Sin esto, la auditoría heredaría las herramientas —y los permisos—
/// de la configuración personal de quien lanza, y dos personas auditarían con superficies
/// distintas.
/// </item>
/// <item>
/// <b><c>--no-session-persistence</c></b>: la sesión es de Atalaya y no tiene por qué aparecer en
/// el historial de <c>claude</c> del usuario ni ocupar su disco.
/// </item>
/// </list>
/// </summary>
public sealed class ClaudeCliRunner
{
    private readonly string _executable;
    private readonly Action<string>? _trace;

    public ClaudeCliRunner(string executable, Action<string>? trace = null)
    {
        _executable = executable;
        _trace = trace;
    }

    /// <summary>
    /// Los argumentos fijos de toda invocación, en orden. Es <c>internal</c> y está separado del
    /// lanzamiento a propósito, por el mismo motivo que <c>BuildFixSessionConfig</c> en Copilot: la
    /// superficie que se le da al agente es la salvaguarda entera de este flujo, y una salvaguarda
    /// que solo se puede comprobar teniendo una suscripción delante no se comprueba nunca. Así el
    /// test lee la lista y afirma sobre ella.
    /// </summary>
    internal static List<string> BuildArguments(ClaudeRun run)
    {
        var args = new List<string>
        {
            "--print",
            "--output-format", "stream-json",
            "--verbose",
            "--mcp-config", run.McpConfigPath,
            "--strict-mcp-config",
            // Sin herramientas propias del CLI: ni consola, ni ficheros, ni red.
            "--tools", string.Empty,
            "--allowedTools", string.Join(",", run.AllowedTools),
            "--permission-mode", "dontAsk",
            "--no-session-persistence",
        };

        if (!string.IsNullOrWhiteSpace(run.Model))
        {
            args.Add("--model");
            args.Add(run.Model!);
        }

        return args;
    }

    /// <summary>
    /// Corre una sesión de punta a punta. Cancelar mata el proceso: un CLI vivo tras cancelar es
    /// un zombi gastando cuota, que es el fallo que D-086 costó descubrir en el runtime de Copilot.
    /// </summary>
    public async Task<ClaudeRunOutcome> RunAsync(
        ClaudeRun run, Action<string>? onText, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (string argument in BuildArguments(run))
        {
            psi.ArgumentList.Add(argument);
        }

        using Process process = Start(psi);

        // stderr se drena SIEMPRE y en paralelo. Si no se lee, el CLI se bloquea al llenar la
        // tubería y la sesión se queda colgada para siempre sin decir por qué.
        Task<string> errors = process.StandardError.ReadToEndAsync(ct);

        await WritePromptAsync(process, run.Prompt, ct);

        var reader = new ClaudeStreamReader(onText);
        ClaudeRunOutcome outcome;
        try
        {
            outcome = await reader.ReadAsync(process.StandardOutput, ct);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        string stderr = await SafeAsync(errors);
        _trace?.Invoke(
            $"claude terminó con {process.ExitCode}; mcp={outcome.McpConnected}; tools={outcome.ToolCalls}");

        return Explain(outcome, process.ExitCode, stderr);
    }

    /// <summary>
    /// El desenlace, ya interpretado. Aquí está la comprobación que <b>ninguna documentación
    /// contaba y que el CLI real enseñó</b>: con el servidor MCP caído la sesión sigue adelante,
    /// el modelo se queda sin herramientas y el CLI termina diciendo <c>"subtype":"success"</c> con
    /// <c>is_error:false</c>. Una auditoría así no reporta nada y parecería una unidad limpia. Se
    /// convierte en fallo explícito: es la diferencia entre «no hay defectos» y «no se pudo mirar».
    /// </summary>
    internal static ClaudeRunOutcome Explain(ClaudeRunOutcome outcome, int exitCode, string stderr)
    {
        // El orden importa. Solo se acusa al servidor MCP cuando la sesión ARRANCÓ de verdad: si
        // el CLI no llegó ni a emitir su evento de inicio, lo que falla es otra cosa —una salida
        // ilegible, un binario que no es el que creíamos— y culpar al MCP mandaría a mirar donde no
        // es. El diagnóstico de la lectura ya dice lo que pasó.
        if (outcome.Started && !outcome.McpConnected)
        {
            return outcome with
            {
                Failed = true,
                Problem = AgentProblem.Unknown,
                Message = ClaudeCodeHelp.McpUnavailable,
            };
        }

        if (outcome.Failed || exitCode == 0)
        {
            return outcome;
        }

        // Salió mal sin haberlo dicho en el flujo: se cuenta con lo que haya, sin inventar causa.
        string raw = stderr.Trim();
        return outcome with
        {
            Failed = true,
            Problem = ClaudeFailure.Classify(raw),
            Message = ClaudeCodeHelp.Unknown(
                raw.Length > 0 ? raw : $"el CLI terminó con código {exitCode} y sin mensaje"),
        };
    }

    private static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi)
                   ?? throw new AuditorAuthenticationException(
                       ClaudeCodeHelp.CliMissing, AgentProblem.CliMissing, null);
        }
        catch (Exception ex) when (ex is not AuditorProviderException)
        {
            // No se pudo ni lanzar: se ha borrado, o ha dejado de ser ejecutable. El remedio es
            // instalar, no iniciar sesión, y decir lo segundo mandaría a buscar un login que no
            // existe todavía.
            throw new AuditorAuthenticationException(
                ClaudeCodeHelp.CliMissing, AgentProblem.CliMissing, ClaudeFailure.Raw(ex), ex);
        }
    }

    private async Task WritePromptAsync(Process process, string prompt, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        }
        catch (IOException ex)
        {
            // El CLI murió antes de leer el prompt (modelo imposible, config inválida). No es un
            // fallo que reportar aquí: el flujo de salida trae la causa buena y llegará enseguida.
            _trace?.Invoke($"claude cerró stdin antes de tiempo: {ex.Message}");
        }
        finally
        {
            // Cerrar stdin es lo que le dice al CLI que el prompt terminó. Sin esto se queda
            // esperando más entrada y la sesión no arranca nunca.
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Ya se había ido, o el sistema no deja: en ninguno de los dos casos hay nada que hacer.
        }
    }

    private static async Task<string> SafeAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Escribe la declaración del servidor MCP y devuelve la ruta. El JSON lo serializa
    /// <c>System.Text.Json</c>: las rutas de Windows llevan barras invertidas y escaparlas a mano
    /// es exactamente lo que rompió el primer intento.
    /// </summary>
    public static string WriteMcpConfig(string directory, string bridgeExecutable, params string[] bridgeArguments)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"mcp-{Guid.NewGuid():N}.json");

        var arguments = new JsonArray();
        foreach (string argument in bridgeArguments)
        {
            // JsonValue.Create y no Add(string): el segundo envuelve la cadena en un valor
            // «personalizado» que revienta al serializar con opciones propias («must specify a
            // TypeInfoResolver»). Se escribe UNA vez por sesión, así que habría reventado siempre.
            arguments.Add(JsonValue.Create(argument));
        }

        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [AuditorTools.ServerName] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = bridgeExecutable,
                    ["args"] = arguments,
                },
            },
        };

        File.WriteAllText(path, config.ToJsonString());
        return path;
    }
}
