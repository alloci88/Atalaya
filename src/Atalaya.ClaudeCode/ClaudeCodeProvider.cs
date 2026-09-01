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
public sealed class ClaudeCodeProvider : IAuditorProvider
{
    /// <summary>
    /// El identificador que se escribe en sesiones, hallazgos e informes. Constante, y no un
    /// literal repartido: lo lee Ajustes, lo escribe el pipeline y lo filtra Métricas.
    /// </summary>
    public const string Id = "claude-code";

    private readonly ILogger _logger;
    private readonly Func<string?> _modelProvider;
    private readonly Func<string> _workDirectory;
    private readonly Func<string?> _cliOverride;
    private readonly string _bridgeExecutable;

    /// <param name="modelProvider">
    /// El modelo elegido en Ajustes. Es una FUNCIÓN y se llama en CADA sesión, por la misma razón
    /// que en Copilot (BUGFIX-AJUSTES): capturarlo al construir obligaba a reiniciar la aplicación
    /// para que un cambio en Ajustes surtiera efecto.
    /// </param>
    /// <param name="bridgeExecutable">
    /// La ruta de <c>Atalaya.Mcp</c>, el relé que el CLI lanza como servidor MCP.
    /// </param>
    /// <param name="cliOverride">
    /// Una ruta fija al CLI en vez de buscarlo en el PATH. Existe para los tests, que apuntan a un
    /// CLI de mentira con un guion de eventos, y así el driver entero —argumentos, tubería, tools,
    /// desenlace— se ejercita sin suscripción de nadie.
    /// </param>
    public ClaudeCodeProvider(
        string bridgeExecutable,
        Func<string?>? modelProvider = null,
        Func<string>? workDirectory = null,
        Func<string?>? cliOverride = null,
        ILogger? logger = null)
    {
        _bridgeExecutable = bridgeExecutable;
        _modelProvider = modelProvider ?? (() => null);
        _workDirectory = workDirectory ?? (() => Path.Combine(Path.GetTempPath(), "atalaya-claude"));
        _cliOverride = cliOverride ?? (() => null);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public string ProviderName => "Claude Code";

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
    public string? ResolveCli() => _cliOverride() ?? ClaudeCliLocator.Resolve();

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

    /// <inheritdoc/>
    public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        => RunSessionAsync(request.Prompt, AuditorTools.ForAudit(toolbox), ct);

    /// <inheritdoc/>
    public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
        => RunSessionAsync(request.Prompt, AuditorTools.ForVerify(toolbox), ct);

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
    private async Task RunSessionAsync(string prompt, IReadOnlyList<McpTool> tools, CancellationToken ct)
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

        try
        {
            var runner = new ClaudeCliRunner(cli, message => _logger.LogDebug("{Message}", message));
            ClaudeRunOutcome outcome = await runner.RunAsync(
                new ClaudeRun(prompt, tools.Select(t => AuditorTools.Qualified(t.Name)).ToList(), configPath, ModelName),
                text => TextStreamed?.Invoke(text),
                ct);

            if (outcome.Usage is { } usage)
            {
                UsageReported?.Invoke(usage with { Model = outcome.Model ?? ModelName });
            }

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
