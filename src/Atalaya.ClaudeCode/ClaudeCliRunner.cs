using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaya.Agents;

namespace Atalaya.ClaudeCode;

/// <summary>Lo que hace falta para una invocación del CLI.</summary>
/// <param name="Prompt">El prompt de la sesión. Viaja por STDIN — ver <see cref="ClaudeCliRunner"/>.</param>
/// <param name="AllowedTools">Los nombres cualificados de las únicas tools permitidas.</param>
/// <param name="McpConfigPath">El fichero con la declaración del servidor MCP de Atalaya.</param>
/// <param name="Model">El modelo, o vacío para dejar que el CLI elija el suyo.</param>
/// <param name="Conversational">
/// La sesión dura VARIOS turnos y el usuario puede hablar mientras corre (F16, arreglo asistido).
/// Cambia la entrada a <c>stream-json</c>: cada mensaje del usuario es una línea JSON y la
/// conversación sigue viva mientras stdin siga abierto. Una auditoría es de un solo turno y no lo
/// necesita.
/// </param>
/// <param name="SystemPromptFile">
/// El fichero con el PREFIJO ESTABLE del prompt (F18 §2), que se añade al system prompt del CLI
/// con <c>--append-system-prompt-file</c>. Null = todo va por stdin, como antes.
/// <para>
/// <b>Por qué el system prompt y no el mensaje.</b> El CLI cachea su prefijo —system prompt y
/// herramientas— y lo reutiliza entre PROCESOS distintos: medido el 2026-09-03 contra el CLI real
/// (2.1.259), un bloque de 12.500 caracteres pasado así se escribe en caché una vez (8.974 tokens
/// de escritura la primera vez) y se lee de ella en todas las invocaciones siguientes (26.973 de
/// lectura, 0 de escritura). Dentro del mensaje de usuario ese mismo bloque va detrás del código
/// de la unidad en la clave de caché, así que cada unidad lo vuelve a pagar entero.
/// </para>
/// <para>
/// <b>Y por FICHERO y no como argumento</b>, por lo mismo que el prompt va por stdin: en Windows
/// el CLI es un <c>claude.cmd</c> y la línea de órdenes la reinterpreta <c>cmd.exe</c>.
/// </para>
/// </param>
/// <param name="PartialMessages">
/// Pide al CLI que reenvíe los eventos CRUDOS de la API (<c>--include-partial-messages</c>). Es lo
/// que hace que el consumo de cada llamada exista <b>antes</b> del evento final: el
/// <c>message_delta</c> que cierra un mensaje trae su consumo definitivo, mientras que el
/// <c>usage</c> del evento <c>assistant</c> es parcial (F21 §1).
/// </param>
public sealed record ClaudeRun(
    string Prompt,
    IReadOnlyList<string> AllowedTools,
    string McpConfigPath,
    string? Model,
    bool Conversational = false,
    string? SystemPromptFile = null,
    bool PartialMessages = false);

/// <summary>
/// <b>El corte de la pasada</b> (F21 §2): cerrar la sesión en cuanto el auditor ha entregado, sin
/// pagar la llamada de cortesía que el CLI exige después de un resultado de herramienta.
/// <para>
/// No es «matar el proceso pronto». El mecanismo es <see cref="ToolRetention"/>: con la respuesta
/// de <c>unit_done</c> retenida el CLI <b>no puede</b> mandar la petición siguiente, así que al
/// cortar no hay nada en vuelo que se facture sin quedar registrado. Y no se mata: se le manda su
/// propia orden de interrupción, con lo que el CLI <b>emite igualmente su evento final</b> y las
/// cuentas llegan enteras —modelo principal y modelo auxiliar—, que es lo que F19 no pudo
/// conseguir (D-865) y por lo que aquel corte se quedó fuera.
/// </para>
/// </summary>
public sealed class ClaudeCut
{
    /// <param name="retention">La retención de <c>unit_done</c> servida por el relay MCP.</param>
    /// <param name="toolsIdle">
    /// Si no queda ninguna herramienta del turno atendiéndose. Se espera a que sea cierto antes de
    /// cortar: las hermanas de <c>unit_done</c> son las que persisten los hallazgos, y cortar
    /// encima de una sería cambiar dinero por cobertura.
    /// </param>
    public ClaudeCut(ToolRetention retention, Func<bool> toolsIdle)
    {
        Retention = retention;
        ToolsIdle = toolsIdle;
    }

    public ToolRetention Retention { get; }

    public Func<bool> ToolsIdle { get; }

    /// <summary>Cuánto se espera, como mucho, a que las hermanas del turno terminen.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Se cortó de verdad: la llamada de cortesía no se ha pagado.</summary>
    public bool Cut { get; internal set; }

    /// <summary>
    /// Por qué NO se cortó, cuando no se cortó. Va al informe: una pasada que costó una llamada de
    /// más tiene que decir por qué, en vez de dejar una cifra sin causa (N-2).
    /// </summary>
    public string? NotCutBecause { get; internal set; }
}

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
    /// <summary>
    /// Cómo dice el CLI que terminó cuando se le interrumpe <b>con herramientas pendientes y nada
    /// en vuelo</b>. Verificado contra el CLI real (2.1.259): es el sello de que el corte cayó
    /// donde tenía que caer.
    /// </summary>
    internal const string AbortedWithToolsPending = "aborted_tools";

    private readonly string _executable;
    private readonly Action<string>? _trace;
    private readonly string? _workDirectory;

    /// <param name="workDirectory">
    /// Dónde corre el CLI. <b>NUNCA el clon del usuario</b>, y eso es una decisión (F16): el
    /// directorio de trabajo es la puerta por la que el CLI se auto-carga el <c>CLAUDE.md</c> del
    /// proyecto y su memoria, y eso sería un segundo canal de instrucciones que Copilot no tiene —
    /// el mismo encargo significaría cosas distintas según la casa. Las convenciones del proyecto
    /// viajan por donde tienen que viajar: las directivas de F7, declaradas y con su traza. El
    /// agente no necesita el clon para nada: no tiene herramientas de fichero, y las rutas de
    /// <c>read_file</c> y <c>apply_edit</c> las resuelve el toolbox de la aplicación.
    /// </param>
    public ClaudeCliRunner(string executable, Action<string>? trace = null, string? workDirectory = null)
    {
        _executable = executable;
        _trace = trace;
        _workDirectory = workDirectory;
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
            // Ni ajustes de usuario, de proyecto ni locales. Es el mismo argumento que
            // --strict-mcp-config, extendido a lo que faltaba (F16): en esos ficheros viven
            // permisos y HOOKS —órdenes que el CLI ejecuta por su cuenta al usar una tool—, y con
            // ellos cargados la superficie de una sesión de Atalaya dependería de la máquina de
            // quien la lanza. El régimen de permisos de Atalaya no se delega en el del CLI.
            "--setting-sources", string.Empty,
            // Sin herramientas propias del CLI: ni consola, ni ficheros, ni red.
            "--tools", string.Empty,
            "--allowedTools", string.Join(",", run.AllowedTools),
            // Que NO pregunte él y que NO autorice él. Lo permitido es exactamente la lista de
            // arriba; para todo lo demás no hay a quién preguntar en un proceso sin consola, y una
            // pregunta sin respuesta sería una sesión colgada. El permiso que sí existe —tocar un
            // fichero que no es del hallazgo— lo gobierna Atalaya dentro de `apply_edit`.
            "--permission-mode", "dontAsk",
            "--no-session-persistence",
        };

        if (run.SystemPromptFile is { Length: > 0 } systemPrompt)
        {
            // Se AÑADE al system prompt del CLI, no lo sustituye: reemplazarlo del todo
            // (--system-prompt) ahorra otros ~6.100 tokens por llamada —medido— pero cambia las
            // instrucciones con las que el modelo trabaja, y eso no se toca sin una comparación de
            // CALIDAD delante. F18 mide y deja la palanca escrita; no la acciona a ciegas.
            args.Add("--append-system-prompt-file");
            args.Add(systemPrompt);
        }

        if (run.PartialMessages)
        {
            // Los eventos crudos de la API. Sin esto el consumo de una llamada solo se conoce en
            // el agregado del final; con esto, el `message_delta` que cierra cada mensaje trae su
            // consumo definitivo y las cuentas existen antes de cortar (F21 §1).
            args.Add("--include-partial-messages");
        }

        if (run.Conversational)
        {
            // La entrada por líneas JSON es lo que permite que el usuario hable a mitad de sesión.
            // Comprobado contra el CLI real (2.1.252): el session_id se conserva entre turnos y el
            // modelo recuerda lo anterior, aun con --no-session-persistence.
            args.Add("--input-format");
            args.Add("stream-json");
        }

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
        ClaudeRun run,
        Action<string>? onText,
        Action<UsageSample>? onUsage,
        CancellationToken ct,
        ClaudeCut? cut = null)
    {
        // Para cortar hay que poder hablarle al CLI mientras corre —su orden de interrupción viaja
        // por la entrada `stream-json`—, así que una sesión con corte es conversacional aunque solo
        // vaya a tener un turno. Sin corte, el prompt va por stdin como siempre.
        ClaudeRun actual = cut is null ? run : run with { Conversational = true };
        using Process process = Start(Describe(actual));

        // stderr se drena SIEMPRE y en paralelo. Si no se lee, el CLI se bloquea al llenar la
        // tubería y la sesión se queda colgada para siempre sin decir por qué.
        Task<string> errors = process.StandardError.ReadToEndAsync(ct);

        // Una sesión con corte habla por la entrada `stream-json`, y ésa NO se acaba sola: el CLI
        // cierra su turno y se queda esperando otro mensaje. Quien dice que se acabó es la
        // aplicación, cerrando stdin — y el momento es el mismo se haya cortado o no: en cuanto
        // llega el evento final del turno, que es el que trae las cuentas.
        var reader = cut is null
            ? new ClaudeStreamReader(onText, onUsage)
            : new ClaudeStreamReader(onText, onUsage, _ => CloseInput(process));

        using var finished = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Una sola pluma para stdin: el mensaje inicial y la orden de corte no pueden entrelazarse.
        using var pen = new SemaphoreSlim(1, 1);

        ClaudeRunOutcome outcome;
        Task? cutting = null;
        try
        {
            if (cut is null)
            {
                await WritePromptAsync(process, run.Prompt, ct);
            }
            else
            {
                await SendUserMessageAsync(process, pen, run.Prompt, ct);
                cutting = CutWhenDeliveredAsync(process, pen, reader, cut, finished.Token);
            }

            outcome = await reader.ReadAsync(process.StandardOutput, ct);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        finally
        {
            // Pase lo que pase: si nadie suelta la retención, el relay se queda esperando una
            // liberación que ya no va a llegar y con él la tubería entera.
            cut?.Retention.Release();
            finished.Cancel();
            if (cutting is not null)
            {
                await SafeAsync(cutting);
            }
        }

        string stderr = await SafeAsync(errors);
        _trace?.Invoke(
            $"claude terminó con {process.ExitCode}; mcp={outcome.McpConnected}; tools={outcome.ToolCalls}"
            + (cut is null ? string.Empty : $"; corte={cut.Cut}"));

        return Explain(outcome, process.ExitCode, stderr, cut);
    }

    /// <summary>
    /// <b>El corte, cuando el auditor ya ha entregado</b> (F21 §2). Espera a que <c>unit_done</c>
    /// quede retenida —momento en que el CLI está parado y no puede mandar nada—, comprueba las
    /// dos condiciones y, solo si se cumplen las dos, manda la orden de interrupción.
    /// <para>
    /// <b>Las condiciones, y por qué son ésas.</b> Primero, que no quede ninguna hermana del turno
    /// atendiéndose: son las que persisten los hallazgos. Segundo, y es la que manda, que las
    /// cuentas de todo lo consumido estén ya (<see cref="ClaudeStreamReader.AccountingIsComplete"/>).
    /// Si falta cualquiera de las dos <b>se suelta la retención y no se corta</b>: la pasada
    /// termina como siempre, paga su llamada y el informe dice por qué. Un ahorro pagado con un
    /// número falso no es un ahorro (D-865).
    /// </para>
    /// </summary>
    private async Task CutWhenDeliveredAsync(
        Process process, SemaphoreSlim pen, ClaudeStreamReader reader, ClaudeCut cut, CancellationToken ct)
    {
        try
        {
            if (!await ShouldCutAsync(cut, () => reader.AccountingIsComplete, ct))
            {
                _trace?.Invoke($"claude: NO se corta — {cut.NotCutBecause}");
                return;
            }

            _trace?.Invoke($"claude: se corta la pasada con {reader.SettledCalls} llamada(s) cuadradas");
            await SendInterruptAsync(process, pen, ct);
        }
        catch (OperationCanceledException)
        {
            // La sesión terminó por su cuenta antes de que hubiera nada que cortar.
        }
    }

    /// <summary>
    /// <b>La decisión</b>: espera a que <c>unit_done</c> quede retenida y luego a las dos
    /// condiciones del corte. Devuelve si se corta, y si no, deja el motivo escrito y <b>suelta la
    /// retención</b> para que la pasada termine como siempre.
    /// <para>
    /// <b>Se ESPERAN, no se preguntan una vez</b>, y eso costó una medida: las dos llegan
    /// milisegundos DESPUÉS de la herramienta —las hermanas porque se atienden en paralelo, y las
    /// cuentas porque el <c>message_delta</c> que cierra la llamada lo emite el CLI justo después
    /// de despachar las herramientas de ese mensaje—. Preguntándolo al llegar <c>unit_done</c>, la
    /// respuesta era «todavía no» <b>siempre</b> y no se cortaba nunca.
    /// </para>
    /// </summary>
    internal static async Task<bool> ShouldCutAsync(
        ClaudeCut cut, Func<bool> accountingIsComplete, CancellationToken ct)
    {
        await cut.Retention.Retained.WaitAsync(ct);

        DateTime until = DateTime.UtcNow + cut.IdleTimeout;
        while ((!cut.ToolsIdle() || !accountingIsComplete()) && DateTime.UtcNow < until)
        {
            await Task.Delay(25, ct);
        }

        if (!cut.ToolsIdle())
        {
            cut.NotCutBecause = "había herramientas del turno todavía atendiéndose";
        }
        else if (!accountingIsComplete())
        {
            cut.NotCutBecause = "el CLI no publicó el consumo de todas sus llamadas";
        }

        if (cut.NotCutBecause is not null)
        {
            cut.Retention.Release();
            return false;
        }

        cut.Cut = true;
        return true;
    }

    /// <summary>
    /// La orden de interrupción del propio CLI. <b>No es matar el proceso</b>, y ésa es toda la
    /// diferencia: el CLI cierra ordenadamente y <b>emite su evento final</b>, con el consumo
    /// completo de la invocación —incluido el del modelo auxiliar que usa por su cuenta, que no
    /// aparece en ningún otro sitio del flujo—. Matándolo, ese evento no llega y la sesión
    /// declararía menos de lo que gastó (D-865).
    /// </summary>
    private static async Task SendInterruptAsync(Process process, SemaphoreSlim pen, CancellationToken ct)
    {
        var order = new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = $"atalaya-corte-{Guid.NewGuid():N}",
            ["request"] = new JsonObject { ["subtype"] = "interrupt" },
        };

        await pen.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(order.ToJsonString().AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        catch (IOException)
        {
            // El CLI se fue solo. No hay nada que cortar y el flujo trae el desenlace bueno.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            pen.Release();
        }
    }

    /// <summary>
    /// Una CONVERSACIÓN de varios turnos: el arreglo asistido (F16).
    /// <para>
    /// La diferencia con <see cref="RunAsync"/> no es de grado. Allí se manda un prompt, se lee
    /// hasta el final y se acabó; aquí la sesión sigue viva mientras stdin siga abierto, cada
    /// mensaje del usuario es una línea JSON más, y el CLI cierra un <c>result</c> por turno sin
    /// terminar. <b>Quien decide que se acabó es la aplicación</b>: cuando el agente cierra con
    /// <c>fix_done</c> (<paramref name="closed"/>) o cuando <paramref name="nextTurn"/> dice que no
    /// hay nada más que decirle. Entonces se cierra stdin, que es la señal de fin para el CLI.
    /// </para>
    /// <para>
    /// <b>Verificado contra el CLI real (2.1.252)</b>, porque nada de esto lo promete su ayuda: que
    /// el <c>session_id</c> se conserva entre turnos, que el modelo recuerda lo anterior —también
    /// con <c>--no-session-persistence</c>—, y que <c>usage</c> es del turno mientras
    /// <c>total_cost_usd</c> viene acumulado.
    /// </para>
    /// </summary>
    /// <param name="onUsage">El consumo de cada turno, ya restado. Se acumula fuera.</param>
    /// <param name="nextTurn">
    /// Qué decirle al agente cuando su turno acaba sin haber cerrado. Devolver <c>null</c> termina
    /// la conversación. Es por donde llegan las órdenes que el usuario escribió mientras trabajaba.
    /// </param>
    /// <param name="closed">El agente ya cerró el arreglo: no se le da otro turno.</param>
    /// <param name="ready">El mando a distancia, en cuanto el proceso existe.</param>
    /// <param name="cut">
    /// <b>El corte de F21, dentro de una conversación</b> — y hay que saber lo que se pide. El
    /// corte interrumpe la INVOCACIÓN en cuanto el auditor entrega <c>unit_done</c>, y en una
    /// conversación la invocación es la conversación entera: cortar la mata. Se corta como mucho
    /// una vez, y lo que quede por hablar habrá que hablarlo en otra. Null —lo normal— es una
    /// conversación que sobrevive a sus turnos.
    /// </param>
    public async Task<ClaudeRunOutcome> RunConversationAsync(
        ClaudeRun run,
        Action<string>? onText,
        Action<UsageSample>? onUsage,
        Func<CancellationToken, Task<string?>> nextTurn,
        Func<bool> closed,
        Action<IFixSteering>? ready,
        CancellationToken ct,
        ClaudeCut? cut = null)
    {
        using Process process = Start(Describe(run with { Conversational = true }));

        // stderr se drena SIEMPRE y en paralelo, igual que en una auditoría: sin leerlo, el CLI se
        // bloquea al llenar la tubería y la sesión se cuelga sin decir por qué.
        Task<string> errors = process.StandardError.ReadToEndAsync(ct);

        var turns = Channel.CreateUnbounded<ClaudeTurn>();
        var pen = new SemaphoreSlim(1, 1);

        var reader = new ClaudeStreamReader(onText, onUsage, turn => turns.Writer.TryWrite(turn));

        ready?.Invoke(new ConversationSteering(process, _trace));

        Task<ClaudeRunOutcome> reading = ReadAndCloseAsync(reader, process, turns, ct);

        using var finished = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? cutting = null;

        try
        {
            await SendUserMessageAsync(process, pen, run.Prompt, ct);
            if (cut is not null)
            {
                cutting = CutWhenDeliveredAsync(process, pen, reader, cut, finished.Token);
            }

            while (await turns.Reader.WaitToReadAsync(ct))
            {
                if (!turns.Reader.TryRead(out ClaudeTurn? turn))
                {
                    continue;
                }

                // Un turno que falla no se contesta con otro turno: la causa ya viaja en el
                // desenlace y darle cuerda encima gastaría cuota contra una sesión rota.
                if (turn.Failed || closed() || ct.IsCancellationRequested)
                {
                    break;
                }

                string? next = await nextTurn(ct);
                if (string.IsNullOrWhiteSpace(next))
                {
                    break;
                }

                await SendUserMessageAsync(process, pen, next!, ct);
            }

            // Cerrar stdin ES el fin de la conversación para el CLI. Sin esto se quedaría esperando
            // otro mensaje que nadie va a escribir, y la sesión no terminaría nunca.
            CloseInput(process);

            ClaudeRunOutcome outcome = await reading;
            await process.WaitForExitAsync(ct);
            string stderr = await SafeAsync(errors);
            _trace?.Invoke(
                $"claude (conversación) terminó con {process.ExitCode}; mcp={outcome.McpConnected}");

            return Explain(outcome, process.ExitCode, stderr, cut);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        finally
        {
            // Igual que en una pasada suelta: si nadie suelta la retención, el relay se queda
            // esperando una liberación que ya no va a llegar y con él la tubería entera.
            cut?.Retention.Release();
            finished.Cancel();
            if (cutting is not null)
            {
                await SafeAsync(cutting);
            }

            pen.Dispose();
        }
    }

    /// <summary>Lee el flujo entero y cierra el canal de turnos pase lo que pase.</summary>
    private static async Task<ClaudeRunOutcome> ReadAndCloseAsync(
        ClaudeStreamReader reader, Process process, Channel<ClaudeTurn> turns, CancellationToken ct)
    {
        try
        {
            return await reader.ReadAsync(process.StandardOutput, ct);
        }
        finally
        {
            // Sin esto, un CLI que muere dejaría al bucle de arriba esperando un turno que ya no
            // va a llegar: exactamente el cuelgue que este driver no puede permitirse.
            turns.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Un mensaje del usuario, en el formato de entrada <c>stream-json</c> del CLI. Se serializa
    /// con <c>System.Text.Json</c> y no a mano: el mensaje puede llevar código, comillas y saltos
    /// de línea, y una línea mal escapada rompe la conversación entera.
    /// </summary>
    private static async Task SendUserMessageAsync(
        Process process, SemaphoreSlim pen, string text, CancellationToken ct)
    {
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                }),
            },
        };

        await pen.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        catch (IOException)
        {
            // El CLI se fue. El flujo de salida trae la causa buena y llegará enseguida.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            pen.Release();
        }
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// El mando a distancia de una conversación viva.
    /// <para>
    /// <b><see cref="SendAsync"/> devuelve siempre <c>false</c>, y es una decisión, no una carencia
    /// (F16).</b> El CLI acepta una línea escrita a mitad de turno y la ENCOLA, pero no dice cuándo
    /// la entregará ni la devuelve si la conversación se cierra antes; con eso, un mensaje podría
    /// quedarse dentro del CLI sin llegar nunca al modelo y sin que nadie lo supiera. La cola de
    /// Atalaya sí sabe lo que tiene, y la vacía en el límite del turno, que es cuando el modelo
    /// puede leerla de verdad. Es exactamente el camino que D-535 dejó escrito para cuando el
    /// runtime no acepta un mensaje a mitad: la interfaz lo dice con esas palabras y no promete una
    /// inmediatez que no puede garantizar.
    /// </para>
    /// </summary>
    private sealed class ConversationSteering : IFixSteering
    {
        private readonly Process _process;
        private readonly Action<string>? _trace;

        public ConversationSteering(Process process, Action<string>? trace)
        {
            _process = process;
            _trace = trace;
        }

        public Task<bool> SendAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task AbortAsync(CancellationToken ct)
        {
            _trace?.Invoke("claude: se aborta la conversación a petición del usuario");
            Kill(_process);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// El desenlace, ya interpretado. Aquí está la comprobación que <b>ninguna documentación
    /// contaba y que el CLI real enseñó</b>: con el servidor MCP caído la sesión sigue adelante,
    /// el modelo se queda sin herramientas y el CLI termina diciendo <c>"subtype":"success"</c> con
    /// <c>is_error:false</c>. Una auditoría así no reporta nada y parecería una unidad limpia. Se
    /// convierte en fallo explícito: es la diferencia entre «no hay defectos» y «no se pudo mirar».
    /// </summary>
    internal static ClaudeRunOutcome Explain(
        ClaudeRunOutcome outcome, int exitCode, string stderr, ClaudeCut? cut = null)
    {
        // F21 §2 — LO PRIMERO: una sesión que hemos cortado nosotros no es una sesión que ha
        // fallado. El CLI la marca con `is_error` y sale con código 1 —desde su punto de vista lo
        // han interrumpido—, así que sin esto toda pasada cortada se leería como una avería.
        //
        // Y se exige SU propia declaración de cómo terminó: `aborted_tools` significa que abortó
        // con herramientas pendientes y NINGUNA petición en vuelo, que es exactamente la condición
        // que hace que el corte no pierda cuentas. Si dijera otra cosa, el corte no cayó donde
        // creíamos y esto vuelve a ser un fallo — que es como tiene que leerse.
        if (cut?.Cut == true)
        {
            return outcome.TerminalReason == AbortedWithToolsPending
                ? outcome with { Failed = false, Problem = AgentProblem.None, Message = string.Empty }

                // Cortamos, pero el CLI dice que terminó de otra manera. NO se da por bueno: si el
                // corte no cayó con las herramientas pendientes, pudo haber una petición en vuelo
                // —que se factura y no se registra—, y eso hay que verlo, con el motivo delante.
                : outcome with
                {
                    Failed = true,
                    Problem = AgentProblem.Unknown,
                    Message = ClaudeCodeHelp.Unknown(
                        $"la pasada se cortó en unit_done pero el CLI terminó por «{outcome.TerminalReason ?? "(sin motivo)"}» "
                        + $"en vez de «{AbortedWithToolsPending}»"),
                };
        }

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

    /// <summary>
    /// Cómo se lanza el CLI. Está separado porque lo comparten la auditoría y la conversación del
    /// arreglo: dos copias de esta configuración serían dos superficies distintas para el agente,
    /// que es justo lo que este driver existe para impedir.
    /// </summary>
    private ProcessStartInfo Describe(ClaudeRun run)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (_workDirectory is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            psi.WorkingDirectory = directory;
        }

        foreach (string argument in BuildArguments(run))
        {
            psi.ArgumentList.Add(argument);
        }

        return psi;
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

    /// <summary>Esperar a algo que ya no importa cómo acabe: la sesión se está cerrando.</summary>
    private static async Task SafeAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
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
