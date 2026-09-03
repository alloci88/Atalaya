using System.Diagnostics;
using System.Text.Json;
using Atalaya.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.ClaudeCode;

/// <summary>
/// El segundo auditor: Claude Code, por su CLI (F14).
/// <para>
/// <b>Qué aporta.</b> Una bolsa de cuota independiente de la de Copilot —cuando la organización
/// agota sus peticiones premium, Atalaya deja de estar parada— y, de propina, el «segundo auditor
/// de otra casa» que llevaba tiempo en el backlog: dos modelos de proveedores distintos
/// discrepando sobre el mismo hallazgo es una señal fuerte, y la mecánica de disputas (⚖) ya
/// existía esperándola.
/// </para>
/// <para>
/// <b>Qué NO cambia.</b> Nada del pipeline. Reconciliar, decidir veredictos, guardar la evidencia,
/// calcular la huella y redactar los informes siguen igual y siguen por encima de
/// <see cref="IAuditorProvider"/>. Este driver solo consigue que otro modelo lea el mismo prompt y
/// conteste por las mismas herramientas.
/// </para>
/// <para>
/// <b>Credenciales: ninguna.</b> Atalaya no gestiona ni guarda nada de Anthropic. Usa el login que
/// el CLI ya tiene en la máquina, exactamente igual que con Copilot usa el de GitHub. Si no hay
/// sesión iniciada, el proveedor aparece como no disponible con la instrucción de qué hacer — y
/// nunca falla mudo.
/// </para>
/// </summary>
public sealed class ClaudeCodeProvider : IAssistedFixProvider
{
    /// <summary>
    /// El identificador que se escribe en sesiones, hallazgos e informes. Constante, y no un
    /// literal repartido: lo lee Ajustes, lo escribe el pipeline y lo filtra Métricas.
    /// </summary>
    public const string Id = "claude-code";

    private readonly ILogger _logger;
    private readonly Func<string?> _modelProvider;
    private readonly Func<string> _workDirectory;
    private readonly Func<string?>? _locator;
    private readonly string _bridgeExecutable;

    /// <summary>Lo que contestó la ayuda del CLI sobre <c>--append-system-prompt-file</c>.</summary>
    private bool? _supportsSystemPromptFile;

    /// <summary>
    /// <b>Apagado, y así se queda</b> hasta que una medida diga otra cosa. Manda el prefijo estable
    /// por el system prompt del CLI en vez de por stdin. F18 lo midió y salió neutro (ver
    /// <see cref="AuditUnitAsync"/>); vive aquí para que el banco de medida pueda volver a
    /// comprobarlo contra el CLI del día, no para encenderlo desde la aplicación.
    /// </summary>
    public bool UseSystemPromptPrefix { get; init; }

    /// <param name="modelProvider">
    /// El modelo elegido en Ajustes. Es una FUNCIÓN y se llama en CADA sesión, por la misma razón
    /// que en Copilot (BUGFIX-AJUSTES): capturarlo al construir obligaba a reiniciar la aplicación
    /// para que un cambio en Ajustes surtiera efecto.
    /// </param>
    /// <param name="bridgeExecutable">
    /// La ruta de <c>Atalaya.Mcp</c>, el relé que el CLI lanza como servidor MCP.
    /// </param>
    /// <param name="locator">
    /// Quién encuentra el CLI. <b>Si se pasa, MANDA</b> —incluso devolviendo null, que significa
    /// «en esta máquina no está»—, y no se recurre al PATH. Esa autoridad es el punto: con un
    /// respaldo al PATH detrás, un test que quiere ejercitar «no hay CLI» encontraría el que tenga
    /// instalado quien ejecuta la suite y probaría lo contrario de lo que dice probar.
    /// </param>
    public ClaudeCodeProvider(
        string bridgeExecutable,
        Func<string?>? modelProvider = null,
        Func<string>? workDirectory = null,
        Func<string?>? locator = null,
        ILogger? logger = null)
    {
        _bridgeExecutable = bridgeExecutable;
        _modelProvider = modelProvider ?? (() => null);
        _workDirectory = workDirectory ?? (() => Path.Combine(Path.GetTempPath(), "atalaya-claude"));
        _locator = locator;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public string ProviderName => "Claude Code";

    /// <summary>
    /// <b>Siempre opcional.</b> Es un extra que da una bolsa de cuota independiente a quien lo
    /// tenga; a quien no, no se le pide nada ni se le recorta nada. Copilot sigue siendo el
    /// proveedor por defecto y el único requisito del equipo.
    /// </summary>
    public bool IsOptional => true;

    /// <inheritdoc/>
    public bool IsPresent => ResolveCli() is not null;

    /// <inheritdoc/>
    public string? ModelName
    {
        get
        {
            string? configured = _modelProvider();
            return string.IsNullOrWhiteSpace(configured) ? null : configured;
        }
    }

    public event Action<string>? TextStreamed;

    public event Action<UsageSample>? UsageReported;

    /// <summary>La ruta del CLI ahora mismo, o null si no está en esta máquina.</summary>
    public string? ResolveCli() => _locator is null ? ClaudeCliLocator.Resolve() : _locator();

    public async Task<bool> EnsureReadyAsync(CancellationToken ct) => (await CheckAsync(ct)).Ready;

    /// <summary>
    /// Las dos preguntas que decide la pantalla Cuenta, en orden y por separado: <b>¿está el
    /// CLI?</b> y <b>¿hay sesión iniciada?</b>
    /// <para>
    /// Están separadas porque tienen remedios distintos —instalar y hacer login— y darle a alguien
    /// el remedio equivocado le hace perder el tiempo: decirle «no estás autenticado» a quien no
    /// tiene el programa lo manda a buscar un login que todavía no existe.
    /// </para>
    /// <para>
    /// La segunda se contesta con <c>claude auth status --json</c>, que devuelve un objeto con
    /// <c>loggedIn</c> y la cuenta. Es la comprobación más barata que hay: no crea sesión, no llama
    /// al modelo y no gasta cuota.
    /// </para>
    /// </summary>
    public async Task<AgentReadiness> CheckAsync(CancellationToken ct)
    {
        string? cli = ResolveCli();
        if (cli is null)
        {
            return new AgentReadiness(false, ClaudeCodeHelp.CliMissing, AgentProblem.CliMissing);
        }

        try
        {
            (int exitCode, string stdout, string stderr) = await RunPlainAsync(
                cli, new[] { "auth", "status", "--json" }, ct);

            ClaudeAuthStatus? status = ClaudeAuthStatus.Parse(stdout);

            if (status is null)
            {
                // El CLI contestó algo que no es el JSON esperado. NO se da por bueno ni por malo
                // inventando: se dice lo que hay (N-2).
                string raw = (stderr.Trim().Length > 0 ? stderr : stdout).Trim();
                return new AgentReadiness(
                    false,
                    ClaudeCodeHelp.Unknown(raw.Length > 0 ? raw : $"código de salida {exitCode}"),
                    AgentProblem.Unknown,
                    raw);
            }

            return status.LoggedIn
                ? new AgentReadiness(true, ClaudeCodeHelp.Ready(status.Describe()))
                : new AgentReadiness(false, ClaudeCodeHelp.NotLoggedIn, AgentProblem.NotAuthenticated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude Code: falló la comprobación de disponibilidad");
            return new AgentReadiness(
                false, ClaudeCodeHelp.Unknown(ex.Message), AgentProblem.Unknown, ClaudeFailure.Raw(ex));
        }
    }

    /// <summary>
    /// Los modelos que ofrece el CLI (F5.1 aplicado a esta casa).
    /// <para>
    /// <b>Y aquí hay que ser honesto: el CLI no publica una lista.</b> No hay
    /// <c>claude models list</c> ni nada equivalente — se buscó. Lo que sí documenta su propia
    /// ayuda son los ALIAS de familia (<c>--model</c>: «an alias for the latest model (e.g.
    /// 'fable', 'opus', or 'sonnet')»), y ésos son justamente lo que conviene ofrecer: un alias
    /// apunta siempre al último modelo de su familia, así que no caduca como caducó el
    /// <c>gpt-5</c> escrito a mano que dejó rotas las máquinas nuevas (F5.15). Un identificador
    /// completo se puede seguir escribiendo a mano en Ajustes.
    /// </para>
    /// <para>
    /// No se inventa multiplicador: el CLI no lo publica, y ponerle uno sería fabricar un dato.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
    {
        if (ResolveCli() is null)
        {
            throw new AuditorAuthenticationException(ClaudeCodeHelp.CliMissing, AgentProblem.CliMissing, null);
        }

        IReadOnlyList<AgentModel> models = new List<AgentModel>
        {
            new("opus", "Opus (el más capaz)"),
            new("sonnet", "Sonnet (equilibrado)"),
            new("haiku", "Haiku (el más rápido)"),
        };

        return Task.FromResult(models);
    }

    /// <summary>
    /// Audita la unidad. <b>El prompt va entero por stdin</b>, que es lo que se venía haciendo.
    /// <para>
    /// <b>F18 §2 midió la alternativa y la dejó fuera.</b> La idea era mandar el prefijo estable
    /// por <c>--append-system-prompt-file</c>, donde la caché del CLI pudiera reutilizarlo entre
    /// unidades. Medido contra el CLI real (2.1.259, 2026-09-03) con las herramientas MCP puestas:
    /// las dos formas producen <b>la misma clave de caché</b> —una tanda con el prompt entero leyó
    /// de caché el 100 % de lo que había escrito la tanda partida, con la entrada idéntica al
    /// token— y en ninguna de las dos la unidad siguiente reutiliza el prefijo: su primera llamada
    /// lee siempre los mismos ~11.300 tokens del CLI y escribe todo lo nuestro. No hay un punto de
    /// corte de caché entre el prefijo y el código, y no lo decidimos nosotros.
    /// </para>
    /// <para>
    /// Así que no entra: un cambio que no mueve la tabla no se defiende (F18 §0). Lo que queda es
    /// <see cref="UseSystemPromptPrefix"/>, apagado, para poder <b>repetir la medida</b> el día que
    /// el CLI cambie sus cortes de caché — que es cuando esta decisión habría que revisarla.
    /// </para>
    /// </summary>
    public async Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
    {
        if (!UseSystemPromptPrefix || !request.CanSplit || !await SupportsSystemPromptFileAsync(ct))
        {
            await RunSessionAsync(request.Prompt, AuditorTools.ForAudit(toolbox), ct);
            return;
        }

        await RunSessionAsync(request.UnitPart!, AuditorTools.ForAudit(toolbox), ct, request.StablePrefix);
    }

    /// <inheritdoc/>
    public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
        => RunSessionAsync(request.Prompt, AuditorTools.ForVerify(toolbox), ct);

    /// <summary>
    /// ¿Admite este CLI <c>--append-system-prompt-file</c>? Se PREGUNTA a su ayuda, una vez por
    /// proceso, y no se supone por el número de versión: el flag existe en 2.1.259 pero no está en
    /// la lista principal de opciones, así que atarlo a una versión sería atarlo a una conjetura.
    /// <para>
    /// Que no esté no es un fallo: se manda el prompt entero por stdin, que es lo que se venía
    /// haciendo. Una optimización que rompiera la auditoría en una máquina con un CLI viejo sería
    /// mucho peor que la optimización.
    /// </para>
    /// </summary>
    private async Task<bool> SupportsSystemPromptFileAsync(CancellationToken ct)
    {
        if (_supportsSystemPromptFile is { } known)
        {
            return known;
        }

        bool supported = false;
        try
        {
            (_, string stdout, string stderr) = await RunPlainAsync(ResolveCli()!, new[] { "--help" }, ct);
            supported = (stdout + stderr).Contains("append-system-prompt-file", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Claude Code: no se pudo preguntar por --append-system-prompt-file: {Message}", ex.Message);
        }

        _supportsSystemPromptFile = supported;
        return supported;
    }

    /// <inheritdoc/>
    public AgentReadiness Diagnose(Exception ex)
    {
        if (ex is AuditorProviderException known)
        {
            return new AgentReadiness(false, known.Message, known.Problem, known.Detail);
        }

        AgentProblem problem = ClaudeFailure.Classify(ex.Message);
        return new AgentReadiness(
            false, ClaudeCodeHelp.For(problem, ex.Message, ModelName), problem, ClaudeFailure.Raw(ex));
    }

    /// <summary>
    /// El ARREGLO ASISTIDO con Claude Code (F16).
    /// <para>
    /// <b>Mismo contrato observable que Copilot, otro motor.</b> Las mismas cuatro herramientas con
    /// el mismo nombre y la misma descripción (<see cref="FixToolText"/>), la misma tarjeta de
    /// pregunta, el mismo diff por fichero y el mismo cierre. La aplicación no se entera de cuál de
    /// los dos está detrás: recibe un <see cref="FixConversation"/> y le contesta por él.
    /// </para>
    /// <para>
    /// <b>Lo único que cambia, y hay que decirlo:</b> aquí la conversación viaja por la entrada
    /// <c>stream-json</c> del CLI en vez de por una sesión del SDK, y la pregunta al usuario la
    /// sirve Atalaya como tool MCP porque el CLI se lanza sin herramientas propias. Ninguna de las
    /// dos cosas asoma a la pantalla.
    /// </para>
    /// <para>
    /// <b>Y lo que NO cambia por venir de otra casa:</b> el presupuesto de lecturas, la copia de
    /// seguridad antes de la primera edición, el permiso fichero a fichero, la pausa y la huella
    /// del arreglo son de la aplicación y viven por encima de esta interfaz. Un proveedor no decide
    /// qué se puede tocar.
    /// </para>
    /// </summary>
    public async Task FixAsync(FixRequest request, FixConversation conversation, CancellationToken ct)
    {
        AgentReadiness readiness = await CheckAsync(ct);
        if (!readiness.Ready)
        {
            throw new AuditorAuthenticationException(
                readiness.Message, readiness.Problem, readiness.Detail);
        }

        string cli = ResolveCli()!;
        string workDirectory = _workDirectory();

        FixToolSet fix = FixTools.ForFix(conversation.Toolbox, conversation.Questions, ct);

        await using var host = new McpPipeHost(fix.Tools, message => _logger.LogDebug("{Message}", message));
        host.Start();

        string configPath = ClaudeCliRunner.WriteMcpConfig(workDirectory, _bridgeExecutable, host.PipeName);

        try
        {
            var runner = new ClaudeCliRunner(cli, message => _logger.LogDebug("{Message}", message), workDirectory);
            ClaudeRunOutcome outcome = await runner.RunConversationAsync(
                new ClaudeRun(
                    request.Prompt,
                    fix.Tools.Select(t => AuditorTools.Qualified(t.Name)).ToList(),
                    configPath,
                    ModelName,
                    Conversational: true),
                text => TextStreamed?.Invoke(text),
                usage => UsageReported?.Invoke(usage with { Model = usage.Model ?? ModelName }),
                conversation.NextTurn ?? (_ => Task.FromResult<string?>(null)),
                () => fix.Closed,
                conversation.Ready,
                ct);

            if (outcome.Failed)
            {
                _logger.LogWarning(
                    "Claude Code rechazó el arreglo: {Problem} — {Message}", outcome.Problem, outcome.Message);

                throw outcome.Problem == AgentProblem.ModelUnavailable
                    ? new AuditorModelUnavailableException(ModelName, outcome.Message, outcome.Message)
                    : new AuditorProviderException(outcome.Message, outcome.Problem, outcome.Message);
            }
        }
        finally
        {
            TryDelete(configPath);
        }
    }

    /// <summary>
    /// Una sesión completa: levantar la tubería, lanzar el CLI con el prompt por stdin, dejar que
    /// llame a las tools, y traducir el desenlace.
    /// <para>
    /// <b>Termina SIEMPRE en un estado terminal con causa, nunca en zombi.</b> Sale bien, o sale
    /// una excepción tipada que la aplicación ya sabe enseñar. Los tres finales que se comprobaron
    /// contra el CLI real —CLI ausente, sesión sin iniciar, salida malformada— pasan por aquí, y
    /// el cuarto, el más traicionero, lo detecta <c>ClaudeCliRunner.Explain</c>: con el servidor
    /// MCP caído el CLI termina «con éxito» y sin herramientas, que se leería como una unidad sin
    /// defectos.
    /// </para>
    /// </summary>
    private async Task RunSessionAsync(
        string prompt, IReadOnlyList<McpTool> tools, CancellationToken ct, string? stablePrefix = null)
    {
        AgentReadiness readiness = await CheckAsync(ct);
        if (!readiness.Ready)
        {
            throw new AuditorAuthenticationException(
                readiness.Message, readiness.Problem, readiness.Detail);
        }

        string cli = ResolveCli()!;
        string workDirectory = _workDirectory();

        await using var host = new McpPipeHost(tools, message => _logger.LogDebug("{Message}", message));
        host.Start();

        string configPath = ClaudeCliRunner.WriteMcpConfig(workDirectory, _bridgeExecutable, host.PipeName);
        string? systemPromptPath = WriteSystemPrompt(workDirectory, stablePrefix);

        try
        {
            var runner = new ClaudeCliRunner(cli, message => _logger.LogDebug("{Message}", message), workDirectory);
            // El consumo viaja SEGÚN OCURRE, llamada a llamada, y no de una vez al terminar: es lo
            // que hace que el pie de la sesión en vivo se mueva mientras el agente trabaja. El
            // modelo que se apunta es el que el CLI resolvió de verdad, no el alias que se le pidió.
            ClaudeRunOutcome outcome = await runner.RunAsync(
                new ClaudeRun(
                    prompt, tools.Select(t => AuditorTools.Qualified(t.Name)).ToList(), configPath, ModelName,
                    SystemPromptFile: systemPromptPath),
                text => TextStreamed?.Invoke(text),
                usage => UsageReported?.Invoke(usage with { Model = usage.Model ?? ModelName }),
                ct);

            if (outcome.Failed)
            {
                _logger.LogWarning(
                    "Claude Code rechazó la operación: {Problem} — {Message}", outcome.Problem, outcome.Message);

                throw outcome.Problem == AgentProblem.ModelUnavailable
                    ? new AuditorModelUnavailableException(ModelName, outcome.Message, outcome.Message)
                    : new AuditorProviderException(outcome.Message, outcome.Problem, outcome.Message);
            }
        }
        finally
        {
            TryDelete(configPath);
        }
    }

    /// <summary>
    /// Escribe el prefijo estable donde el CLI pueda leerlo. <b>El nombre sale del contenido</b>:
    /// dos unidades con el mismo prefijo escriben el mismo fichero y no hay uno por unidad
    /// acumulándose en el directorio de trabajo. <b>Y no se borra al terminar</b>, a diferencia de
    /// la configuración MCP: la sesión siguiente lo va a querer idéntico, y borrarlo para volver a
    /// escribirlo igual solo sería trabajo. Vive en el directorio de trabajo de Atalaya, que es
    /// temporal por definición.
    /// </summary>
    private string? WriteSystemPrompt(string workDirectory, string? stablePrefix)
    {
        if (stablePrefix is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(workDirectory);
            string name = $"prefijo-{Hash(stablePrefix)}.txt";
            string path = Path.Combine(workDirectory, name);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, stablePrefix, new System.Text.UTF8Encoding(false));
            }

            return path;
        }
        catch (Exception ex)
        {
            // No poder escribirlo no puede tumbar una auditoría: se manda el prompt entero por
            // stdin, que es lo que se hacía antes de F18. Se pierde caché, no cobertura.
            _logger.LogDebug("Claude Code: no se pudo escribir el prefijo estable: {Message}", ex.Message);
            return null;
        }
    }

    private static string Hash(string text)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>Una invocación corta del CLI que no necesita ni MCP ni streaming.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPlainAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(psi)
            ?? throw new AuditorAuthenticationException(
                ClaudeCodeHelp.CliMissing, AgentProblem.CliMissing, null);

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdout, await stderr);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            // Un fichero temporal que no se borra no estropea nada; ocultarlo del log, sí.
            _logger.LogDebug("Claude Code: no se pudo borrar {Path}: {Message}", path, ex.Message);
        }
    }
}

/// <summary>
/// Lo que dice <c>claude auth status --json</c>. Se leen SOLO los campos que se usan: el resto
/// —organización, directorio de proyectos, tipo de suscripción— es del usuario y Atalaya no tiene
/// nada que hacer con ellos.
/// </summary>
/// <param name="LoggedIn">Si hay sesión iniciada. Es la única respuesta que decide.</param>
/// <param name="Email">La cuenta, cuando el CLI la da; solo para poder nombrarla en Cuenta.</param>
/// <param name="AuthMethod">Cómo se autenticó (<c>claude.ai</c>, API key…).</param>
public sealed record ClaudeAuthStatus(bool LoggedIn, string? Email, string? AuthMethod)
{
    /// <summary>
    /// Lee la respuesta. Devuelve null si no es el JSON esperado — y eso NO se interpreta como
    /// «no autenticado»: son cosas distintas y la segunda tiene un remedio que la primera no.
    /// </summary>
    public static ClaudeAuthStatus? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            JsonElement root = JsonDocument.Parse(json).RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("loggedIn", out JsonElement loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            return new ClaudeAuthStatus(
                loggedIn.ValueKind == JsonValueKind.True,
                Text(root, "email"),
                Text(root, "authMethod"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Cómo nombrar la sesión en la pantalla Cuenta.</summary>
    public string? Describe() => Email ?? AuthMethod;

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
