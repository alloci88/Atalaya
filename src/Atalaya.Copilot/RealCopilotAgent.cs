using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.Copilot;

/// <summary>
/// The real agent: wraps the GitHub Copilot SDK (§6.1–6.3). ONE client per process.
/// <para>
/// Authentication (F2.3): the client is created with <c>GitHubToken</c> = the token from the
/// in-app GitHub login and <c>UseLoggedInUser = false</c>, so the run is billed to that user's
/// Copilot seat and no console login is needed. When there is no account token we fall back to
/// <c>UseLoggedInUser = true</c> — the pre-F2 behaviour, which reuses whatever the `copilot`
/// CLI stored on the machine — so existing users keep working untouched.
/// </para>
/// <para>
/// The runtime is ALWAYS the CLI bundled with the SDK package (<see cref="CopilotCliLocator"/>),
/// never a <c>copilot</c> resolved from PATH.
/// </para>
/// The agent only ever calls our registered tools; the permission handler rejects everything else
/// (shell, files, network), so it can touch nothing. Compiled against SDK 1.0.11.
/// </summary>
public sealed class RealCopilotAgent : ICopilotAgent, IAsyncDisposable
{
    private readonly string? _baseDirectory;
    private readonly ILogger _logger;
    private readonly string? _model;
    private readonly TimeSpan _sendTimeout;
    private readonly Func<string?>? _tokenProvider;
    private readonly Func<string?>? _loginProvider;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private CopilotClient? _client;
    private string? _startedWithToken;

    /// <param name="baseDirectory">
    /// Optional SDK base directory. LEAVE NULL by default: then the SDK uses its standard location,
    /// which is also where the `copilot` CLI stores its login — so the legacy
    /// <c>UseLoggedInUser</c> fallback still finds it.
    /// </param>
    /// <param name="tokenProvider">
    /// Supplies the current GitHub account token (<c>gho_</c>/<c>ghu_</c>/<c>github_pat_</c>), or
    /// null when the account is not connected. Read on every start so connecting, disconnecting
    /// or switching account takes effect without restarting the app.
    /// </param>
    /// <param name="loginProvider">
    /// The GitHub login of the connected account. When authenticating by token the runtime does
    /// not resolve a login of its own (<c>GetAuthStatusAsync</c> reports <c>authType: "token"</c>
    /// with no <c>Login</c>), so the profile we already fetched is the authoritative source.
    /// </param>
    public RealCopilotAgent(
        string? baseDirectory = null,
        ILogger? logger = null,
        string? model = null,
        TimeSpan? sendTimeout = null,
        Func<string?>? tokenProvider = null,
        Func<string?>? loginProvider = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? null : baseDirectory;
        _logger = logger ?? NullLogger.Instance;
        _model = model;
        // The SDK default (1 min) is too short for auditing a real code unit.
        _sendTimeout = sendTimeout is { TotalSeconds: > 0 } ? sendTimeout.Value : TimeSpan.FromMinutes(15);
        _tokenProvider = tokenProvider;
        _loginProvider = loginProvider;
    }

    public string? ModelName => _model;

    public event Action<string>? TextStreamed;

    public event Action<UsageSample>? UsageReported;

    public async Task<bool> EnsureReadyAsync(CancellationToken ct) => (await CheckAsync(ct)).Ready;

    /// <summary>
    /// Cheapest possible readiness check on SDK 1.0.11 (F2.1): <c>GetAuthStatusAsync</c> tells us
    /// whether the runtime holds a usable credential; <c>ListModelsAsync</c> (cached by the SDK
    /// after the first call) is the cheapest call that actually exercises the Copilot entitlement,
    /// so it is what separates "no seat" from "not authenticated". No session is created.
    /// </summary>
    public async Task<AgentReadiness> CheckAsync(CancellationToken ct)
    {
        bool hasToken = !string.IsNullOrWhiteSpace(CurrentToken());

        try
        {
            await EnsureStartedAsync(ct);
            GetAuthStatusResponse status = await _client!.GetAuthStatusAsync(ct);
            if (status is not { IsAuthenticated: true })
            {
                string extra = string.IsNullOrWhiteSpace(status?.StatusMessage) ? "" : $" [{status.StatusMessage}]";
                return hasToken
                    ? new AgentReadiness(false, CopilotHelp.TokenRejected + extra, AgentProblem.TokenRejected)
                    : new AgentReadiness(false, CopilotHelp.NoAccount + extra, AgentProblem.NotAuthenticated);
            }

            // Authenticated. Now check the entitlement — a valid token with no seat fails here.
            try
            {
                await _client.ListModelsAsync(ct);
            }
            catch (Exception ex) when (LooksLikeNoSeat(ex))
            {
                _logger.LogWarning(ex, "Copilot token valid but no seat");
                return new AgentReadiness(false, CopilotHelp.NoSeat, AgentProblem.NoSeat);
            }

            // The account profile wins: with a token credential the runtime has no login to report.
            // Falling back to status.Login keeps the legacy CLI path naming its own user.
            string? login = Blank(_loginProvider?.Invoke()) ?? Blank(status.Login);
            return new AgentReadiness(true,
                login is null ? "Copilot autenticado." : $"Copilot autenticado como {login}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot readiness check failed");
            return Classify(ex, hasToken);
        }
    }

    public async Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);

        SubmitFindingResult SubmitFinding(
            string ruleId, string pillar, string severity, string title,
            string description, string impact, string recommendation, SubmitLocation[] locations, string? symbol)
            => toolbox.SubmitFinding(new SubmitFindingArgs(
                ruleId, pillar, severity, title, description, impact, recommendation, locations, symbol));

        SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
            => toolbox.SubmitFindings(findings ?? Array.Empty<SubmitFindingArgs>());

        ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
            => toolbox.ReportVerdicts(verdicts ?? Array.Empty<VerdictArgs>());

        AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
            => toolbox.AddLocations(findingId, locations ?? Array.Empty<SubmitLocation>());

        void UnitDone(string unitPath, string summary) => toolbox.UnitDone(unitPath, summary);

        string ReadSignatures(string path) => toolbox.ReadSignatures(path);

        var config = NewSessionConfig();
        AddTool(config, SubmitFindings, "submit_findings",
            "PREFERIDA. Reporta TODOS los hallazgos de la unidad en UNA sola llamada, pasando un array. "
            + "Devuelve un array de {accepted, duplicateOf, error} en el mismo orden.");
        AddTool(config, SubmitFinding, "submit_finding",
            "Fallback singular. Úsala solo si por alguna razón no puedes agrupar; cada llamada añade un turno.");
        AddTool(config, ReportVerdicts, "report_verdicts",
            "OBLIGATORIA cuando la unidad tiene hallazgos existentes. Un array con un veredicto por CADA "
            + "hallazgo listado: {findingId (ULID exacto de la lista), verdict (presente|arreglado|no-verificable), "
            + "evidence}. Devuelve un array de {accepted, error} en el mismo orden.");
        AddTool(config, AddLocations, "add_locations",
            "Extiende un hallazgo YA existente con ubicaciones nuevas de esta misma unidad. Úsala cuando "
            + "el MISMO defecto aparece en varios sitios: un defecto sistémico es UN hallazgo con N "
            + "ubicaciones, no N hallazgos. findingId debe ser un ULID de la lista de existentes o de uno "
            + "que hayas reportado en esta unidad.");
        AddTool(config, UnitDone, "unit_done", "Cierra la unidad en curso con un resumen.", terminal: true);
        AddTool(config, ReadSignatures, "read_signatures",
            "Devuelve las firmas (no cuerpos) de las dependencias directas de la unidad.");

        await RunAsync(config, request.Prompt, ct);
    }

    public async Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);

        void SubmitVerdict(string findingUlid, string verdict, string evidence)
            => toolbox.SubmitVerdict(findingUlid, verdict, evidence);

        var config = NewSessionConfig();
        AddTool(config, SubmitVerdict, "submit_verdict",
            "Registra el veredicto de un hallazgo por su ULID: confirmado | resuelto | no-verificable.");

        await RunAsync(config, request.Prompt, ct);
    }

    private string? CurrentToken() => Blank(_tokenProvider?.Invoke());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private SessionConfig NewSessionConfig()
    {
        var config = new SessionConfig
        {
            Streaming = true,
            ClientName = "Atalaya",
            Model = _model,
            // Reject EVERYTHING the agent tries beyond our tools (shell, files, network) — §6.2.
            OnPermissionRequest = (_, _) =>
                Task.FromResult(PermissionDecision.Reject("Atalaya audita en solo lectura; acción no permitida.")),
            OnEvent = OnSessionEvent,
        };
        config.Tools ??= new List<AIFunctionDeclaration>();
        return config;
    }

    private static void AddTool(SessionConfig config, Delegate method, string name, string description, bool terminal = false)
    {
        AIFunctionDeclaration tool = CopilotTool.DefineTool(
            method,
            new CopilotToolOptions { SkipPermission = true, IsTerminal = terminal },
            new AIFunctionFactoryOptions { Name = name, Description = description });
        (config.Tools ??= new List<AIFunctionDeclaration>()).Add(tool);
    }

    private async Task RunAsync(SessionConfig config, string prompt, CancellationToken ct)
    {
        // Fail fast with the friendly help message instead of a raw SDK auth error (§6.1).
        AgentReadiness readiness = await CheckAsync(ct);
        if (!readiness.Ready)
        {
            throw new CopilotAuthenticationException(readiness.Message);
        }

        try
        {
            CopilotSession session = await _client!.CreateSessionAsync(config, ct);
            try
            {
                await session.SendAndWaitAsync(prompt, _sendTimeout, ct);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        catch (Exception ex) when (LooksLikeAuthError(ex) || LooksLikeNoSeat(ex))
        {
            throw new CopilotAuthenticationException(Classify(ex, CurrentToken() is not null).Message, ex);
        }
    }

    /// <summary>Maps an SDK/transport failure onto a specific, actionable diagnosis (F2.3).</summary>
    private static AgentReadiness Classify(Exception ex, bool hasToken)
    {
        if (LooksLikeNoSeat(ex))
        {
            return new AgentReadiness(false, CopilotHelp.NoSeat, AgentProblem.NoSeat);
        }

        if (LooksLikeOffline(ex))
        {
            return new AgentReadiness(
                false,
                "No hay conexión con GitHub. Comprueba la red o el proxy y reintenta.",
                AgentProblem.Offline);
        }

        if (!hasToken)
        {
            return new AgentReadiness(false, CopilotHelp.NoAccount, AgentProblem.NotAuthenticated);
        }

        return LooksLikeAuthError(ex)
            ? new AgentReadiness(false, CopilotHelp.TokenRejected, AgentProblem.TokenRejected)
            : new AgentReadiness(false, CopilotHelp.TokenRejected, AgentProblem.Unknown);
    }

    private static bool LooksLikeAuthError(Exception ex)
    {
        string m = Flatten(ex);
        return m.Contains("authentication") || m.Contains("not authenticated")
            || m.Contains("custom provider") || m.Contains("unauthorized") || m.Contains("401");
    }

    private static bool LooksLikeNoSeat(Exception ex)
    {
        string m = Flatten(ex);
        return m.Contains("seat") || m.Contains("subscription") || m.Contains("not entitled")
            || m.Contains("entitlement") || m.Contains("quota") || m.Contains("403") || m.Contains("forbidden");
    }

    private static bool LooksLikeOffline(Exception ex)
    {
        if (ex is HttpRequestException)
        {
            return true;
        }

        string m = Flatten(ex);
        return m.Contains("no such host") || m.Contains("network") || m.Contains("connection refused")
            || m.Contains("name or service not known") || m.Contains("timed out while connecting");
    }

    private static string Flatten(Exception? ex)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            text.Append(e.Message).Append(' ');
        }

        return text.ToString().ToLowerInvariant();
    }

    private void OnSessionEvent(SessionEvent ev)
    {
        switch (ev)
        {
            case AssistantUsageEvent usage:
                UsageReported?.Invoke(UsageAdapter.From(usage.Data));
                break;
            case AssistantMessageDeltaEvent:
            case AssistantMessageEvent:
                TextStreamed?.Invoke(".");
                break;
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        string? token = CurrentToken();

        await _startGate.WaitAsync(ct);
        try
        {
            if (_client is not null && _startedWithToken == token)
            {
                return;
            }

            // The account changed (connected / disconnected / switched user): the token is baked
            // into the runtime process environment, so the client has to be rebuilt.
            if (_client is not null)
            {
                _logger.LogInformation("Copilot: credential changed, restarting the runtime client");
                await DisposeClientAsync();
            }

            var options = new CopilotClientOptions
            {
                // With an account token we authenticate explicitly and bill that user's seat;
                // without one we keep the pre-F2 behaviour (the `copilot` CLI login on this machine).
                GitHubToken = token,
                UseLoggedInUser = token is null,
                Logger = _logger,
            };

            // ALWAYS the CLI bundled by the SDK package — never one resolved from PATH (F2.1).
            string? bundled = CopilotCliLocator.ResolveBundled();
            if (bundled is not null)
            {
                options.Connection = RuntimeConnection.ForStdio(bundled);
            }
            else
            {
                // The SDK's own default is also the bundled runtime; log it so a broken deployment
                // (CLI missing from runtimes/) is visible instead of silently probing elsewhere.
                _logger.LogWarning(
                    "Copilot: bundled CLI not found under {Base}runtimes/*/native; falling back to the SDK default",
                    AppContext.BaseDirectory);
            }

            // Only override BaseDirectory when explicitly configured; otherwise the SDK's default
            // location matches the `copilot` CLI login the legacy fallback needs.
            if (_baseDirectory is not null)
            {
                options.BaseDirectory = _baseDirectory;
            }

            var client = new CopilotClient(options);
            await client.StartAsync(ct);
            _client = client;
            _startedWithToken = token;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task DisposeClientAsync()
    {
        CopilotClient? client = _client;
        _client = null;
        _startedWithToken = null;
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot: error disposing the runtime client");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
        _startGate.Dispose();
    }
}
