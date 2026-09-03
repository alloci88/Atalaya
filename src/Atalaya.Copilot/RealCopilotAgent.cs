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
public sealed class RealCopilotAgent : IAssistedFixProvider, IAsyncDisposable
{
    private readonly string? _baseDirectory;
    private readonly ILogger _logger;
    private readonly Func<string?>? _modelProvider;
    private readonly Func<TimeSpan> _sendTimeout;
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
    /// <param name="sendTimeout">
    /// Cuánto se espera una respuesta del modelo. Es una FUNCIÓN, y se llama en cada envío, por la
    /// misma razón que <paramref name="modelProvider"/>: cambiar el timeout en Ajustes tenía que
    /// esperar a un reinicio porque el valor se capturaba al construir el agente, que se construye
    /// una vez (BUGFIX-AJUSTES).
    /// </param>
    /// <param name="modelProvider">
    /// The model id chosen in Ajustes (F5.1). Read on EVERY session so changing the model takes
    /// effect on the next audit without restarting the app — same reason as
    /// <paramref name="tokenProvider"/>. Null/blank leaves <c>SessionConfig.Model</c> unset and
    /// the runtime picks its own default.
    /// </param>
    public RealCopilotAgent(
        string? baseDirectory = null,
        ILogger? logger = null,
        Func<string?>? modelProvider = null,
        Func<TimeSpan>? sendTimeout = null,
        Func<string?>? tokenProvider = null,
        Func<string?>? loginProvider = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? null : baseDirectory;
        _logger = logger ?? NullLogger.Instance;
        _modelProvider = modelProvider;
        // The SDK default (1 min) is too short for auditing a real code unit.
        _sendTimeout = sendTimeout ?? (() => DefaultSendTimeout);
        _tokenProvider = tokenProvider;
        _loginProvider = loginProvider;
    }

    /// <summary>Lo que se espera si nadie configura nada. El del SDK (1 min) no da para auditar.</summary>
    public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// El identificador que se escribe en sesiones, hallazgos e informes (F14). Es una constante y
    /// no un literal repartido: lo lee la selección de Ajustes, lo escribe el pipeline y lo filtra
    /// Métricas, y tres copias de la misma cadena son dos oportunidades de escribirla mal.
    /// </summary>
    public const string Id = "copilot";

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public string ProviderName => "GitHub Copilot";

    /// <summary>
    /// La clasificación de esta casa (F14): el clasificador de errores del SDK, que ya existía y
    /// que sabe distinguir cuota de asiento de credencial. La interfaz solo le pone nombre común.
    /// </summary>
    public AgentReadiness Diagnose(Exception ex)
        => CopilotFailure.Diagnose(ex, CurrentToken() is not null, ModelName);

    /// <summary>
    /// El plazo que se aplicaría AHORA. Existe para poder comprobar que sale del ajuste vigente y
    /// no de una constante capturada al arrancar.
    /// </summary>
    public TimeSpan SendTimeout
    {
        get
        {
            TimeSpan configured = _sendTimeout();
            return configured.TotalSeconds > 0 ? configured : DefaultSendTimeout;
        }
    }

    public string? ModelName => Blank(_modelProvider?.Invoke());

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
            // Pero NO todo lo que falla aqui es un asiento que falta: con la cuota agotada esta
            // misma llamada revienta, y decir «no tienes asiento» seria inventarse la causa. Se
            // clasifica, y el clasificador mira la cuota primero (BUGFIX-CUOTA).
            try
            {
                await _client.ListModelsAsync(ct);
            }
            catch (Exception ex) when (CopilotFailure.Classify(ex, hasToken)
                                       is AgentProblem.NoSeat or AgentProblem.QuotaExhausted)
            {
                AgentReadiness bad = CopilotFailure.Diagnose(ex, hasToken, ModelName);
                _logger.LogWarning(ex, "Copilot entitlement check failed: {Problem}", bad.Problem);
                return bad;
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
            return CopilotFailure.Diagnose(ex, hasToken, ModelName);
        }
    }

    /// <summary>
    /// The models this account may use, straight from the runtime (F5.1). Never a hand-written
    /// list: <c>ListModelsAsync</c> resolves the caller's Copilot plan, so what Ajustes offers is
    /// exactly what the seat can run. The SDK caches the answer after the first successful call.
    /// </summary>
    public async Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
    {
        await EnsureStartedAsync(ct);
        IList<ModelInfo> models = await _client!.ListModelsAsync(ct);
        return models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new AgentModel(
                m.Id,
                Blank(m.Name) ?? m.Id,
                m.Billing?.Multiplier))
            .ToList();
    }

    public async Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);

        SubmitFindingResult SubmitFinding(
            string ruleId, string pillar, string severity, string title,
            string description, string impact, string recommendation, SubmitLocation[] locations, string? symbol,
            string? distinctFrom = null, string? distinctReason = null)
            => toolbox.SubmitFinding(new SubmitFindingArgs(
                ruleId, pillar, severity, title, description, impact, recommendation, locations, symbol,
                distinctFrom, distinctReason));

        SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
            => toolbox.SubmitFindings(findings ?? Array.Empty<SubmitFindingArgs>());

        ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
            => toolbox.ReportVerdicts(verdicts ?? Array.Empty<VerdictArgs>());

        AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
            => toolbox.AddLocations(findingId, locations ?? Array.Empty<SubmitLocation>());

        void UnitDone(string unitPath, string summary, SuppressedByPatternArgs[]? suppressedByPattern)
            => toolbox.UnitDone(unitPath, summary, suppressedByPattern);

        string ReadSignatures(string path) => toolbox.ReadSignatures(path);

        var config = NewSessionConfig();
        AddTool(config, SubmitFindings, "submit_findings",
            "PREFERIDA. Reporta TODOS los hallazgos de la unidad en UNA sola llamada, pasando un array. "
            + "Devuelve un array de {accepted, duplicateOf, error} en el mismo orden. Un hallazgo que se "
            + "parezca demasiado a uno ya existente se devuelve sin aceptar, nombrándolo: si es el mismo "
            + "defecto no lo reportes (o extiéndelo con add_locations), y si de verdad es otro reenvíalo con "
            + "distinctFrom = ese ULID y distinctReason.");
        AddTool(config, SubmitFinding, "submit_finding",
            "Fallback singular. Úsala solo si por alguna razón no puedes agrupar; cada llamada añade un turno.");
        AddTool(config, ReportVerdicts, "report_verdicts",
            "OBLIGATORIA cuando la unidad tiene hallazgos existentes. Un array con un veredicto por CADA "
            + "hallazgo listado: {findingId (ULID exacto de la lista), verdict "
            + "(presente|arreglado|no-es-defecto|no-verificable), evidence}. Usa 'arreglado' SOLO si el "
            + "código cambió y por eso el problema ya no está; si lo que ocurre es que discrepas de quien "
            + "lo reportó, usa 'no-es-defecto' con tu razonamiento. Devuelve un array de {accepted, error} "
            + "en el mismo orden.");
        AddTool(config, AddLocations, "add_locations",
            "Extiende un hallazgo YA existente con ubicaciones nuevas de esta misma unidad. Úsala cuando "
            + "el MISMO defecto aparece en varios sitios: un defecto sistémico es UN hallazgo con N "
            + "ubicaciones, no N hallazgos. findingId debe ser un ULID de la lista de existentes o de uno "
            + "que hayas reportado en esta unidad.");
        AddTool(config, UnitDone, "unit_done",
            "Cierra la unidad en curso con un resumen. Si el prompt trae TIPOS DE PROBLEMA SILENCIADOS y "
            + "te has callado alguna detección por uno de ellos, declara cuántas en suppressedByPattern: "
            + "un array de {patternId (el id EXACTO del prompt, p. ej. P-2), count}. Déjalo vacío si no te "
            + "has callado nada.",
            terminal: true);
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

    /// <summary>
    /// Una sesión de ARREGLO (F6.9). Se parece poco a auditar o verificar y por eso no reutiliza
    /// <see cref="RunAsync"/>: aquella manda un prompt, espera y cierra; ésta mantiene la sesión
    /// viva mientras dure la conversación, contesta a las preguntas del agente y admite que el
    /// usuario la dirija desde fuera.
    /// <para>
    /// <b>Superficie del SDK verificada contra 1.0.11</b> (la lección del F2). La tool
    /// <c>ask_user</c> del runtime llega por <c>SessionConfig.OnUserInputRequest</c>, que es un
    /// <c>Func&lt;UserInputRequest, UserInputInvocation, Task&lt;UserInputResponse&gt;&gt;</c> —el
    /// mismo canal que <c>CopilotSession.RegisterUserInputHandler</c>—. Las convenientes
    /// <c>session.Ui.ConfirmAsync/SelectAsync/InputAsync</c> también existen, pero van del SDK
    /// HACIA el host y lanzan si <c>session.Capabilities.Ui?.Elicitation</c> no es true: no son
    /// este camino. <c>SendAsync</c> encola un mensaje sin esperar al turno, y <c>AbortAsync</c>
    /// corta el turno dejando la sesión utilizable.
    /// </para>
    /// </summary>
    public async Task FixAsync(FixRequest request, FixConversation conversation, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);
        SessionConfig config = BuildFixSessionConfig(request, conversation, ct);

        AgentReadiness readiness = await CheckAsync(ct);
        if (!readiness.Ready)
        {
            throw new AuditorAuthenticationException(readiness.Message);
        }

        try
        {
            CopilotSession session = await _client!.CreateSessionAsync(config, ct);
            try
            {
                conversation.Ready?.Invoke(new SessionSteering(session, _logger));

                string? next = request.Prompt;
                while (next is { Length: > 0 })
                {
                    await session.SendAndWaitAsync(next, SendTimeout, ct);
                    next = conversation.NextTurn is null
                        ? null
                        : await conversation.NextTurn(ct);
                }
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        // BUGFIX-CUOTA: UN solo camino de traduccion. Antes habia dos catch con sus propios
        // detectores y todo lo que no casara con ninguno se escapaba crudo hasta la vista.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// La configuración de una sesión de arreglo: sus CUATRO tools, el handler de <c>ask_user</c>,
    /// el directorio de trabajo y el permiso que rechaza todo lo demás.
    /// <para>
    /// Es <c>internal</c> y no está incrustada en <see cref="FixAsync"/> a propósito: la
    /// superficie que se le da al agente es la salvaguarda entera de este flujo, y una salvaguarda
    /// que solo se puede comprobar teniendo un asiento de Copilot delante no se comprueba nunca.
    /// Así el test puede leer la lista de tools y llamar al permission handler.
    /// </para>
    /// </summary>
    internal SessionConfig BuildFixSessionConfig(
        FixRequest request, FixConversation conversation, CancellationToken ct)
    {
        IFixToolbox toolbox = conversation.Toolbox;

        ReadFileResult ReadFile(string path) => toolbox.ReadFile(path);

        ApplyEditResult ApplyEdit(string path, string reason, FixEdit[] edits)
            => toolbox.ApplyEdit(path, reason ?? string.Empty, edits ?? Array.Empty<FixEdit>());

        BuildAndTestResult RunBuildAndTests() => toolbox.RunBuildAndTests();

        void FixDone(string summary, string commitTitle, string commitDescription, string? risks)
            => toolbox.FixDone(new FixDoneArgs(summary, commitTitle, commitDescription, risks));

        var config = NewSessionConfig();

        // El agente vive DENTRO del clon: nada de lo que haga tiene sentido fuera de él, y el
        // toolbox además rechaza cualquier ruta que se salga.
        config.WorkingDirectory = request.CloneRoot;

        // La tool ask_user del runtime desemboca aquí. Sin este handler el runtime no tiene a
        // quién preguntar y la elicitación —que es la mitad del producto— no existiría.
        config.OnUserInputRequest = async (input, _) =>
        {
            IReadOnlyList<string> choices = input?.Choices?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
            bool free = input?.AllowFreeform ?? true;
            string? answer = await conversation.Questions
                .AskAsync(input?.Question ?? string.Empty, choices, free, ct);

            return new UserInputResponse
            {
                Answer = answer ?? string.Empty,
                WasFreeform = answer is not null && !choices.Contains(answer, StringComparer.Ordinal),
            };
        };

        // F16 — los nombres y las descripciones salen de FixToolText, que los comparte con el
        // driver de Claude Code. Con dos motores detrás del mismo contrato, una copia a mano de
        // este texto es una copia esperando a divergir — y en cuanto divergiera, la diferencia
        // entre las dos casas dejaría de poder atribuirse al modelo.
        AddTool(config, ReadFile, FixToolText.ReadFile, FixToolText.ReadFileDescription);
        AddTool(config, ApplyEdit, FixToolText.ApplyEdit, FixToolText.ApplyEditDescription);
        AddTool(config, RunBuildAndTests, FixToolText.RunBuildAndTests, FixToolText.RunBuildAndTestsDescription);
        AddTool(config, FixDone, FixToolText.FixDone, FixToolText.FixDoneDescription, terminal: true);

        return config;
    }

    /// <summary>
    /// El mando a distancia de una sesión viva. Nunca lanza hacia la interfaz: que el runtime no
    /// acepte un mensaje a mitad de turno es una respuesta —la app lo encola y lo dice—, no un
    /// error que tumbe la vista.
    /// </summary>
    private sealed class SessionSteering : IFixSteering
    {
        private readonly CopilotSession _session;
        private readonly ILogger _logger;

        public SessionSteering(CopilotSession session, ILogger logger)
        {
            _session = session;
            _logger = logger;
        }

        public async Task<bool> SendAsync(string message, CancellationToken ct)
        {
            try
            {
                await _session.SendAsync(message, ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Arreglo: el runtime no aceptó el mensaje a mitad de turno");
                return false;
            }
        }

        public async Task AbortAsync(CancellationToken ct)
        {
            try
            {
                await _session.AbortAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Arreglo: fallo al abortar el turno");
            }
        }
    }

    private string? CurrentToken() => Blank(_tokenProvider?.Invoke());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private SessionConfig NewSessionConfig()
    {
        var config = new SessionConfig
        {
            Streaming = true,
            ClientName = "Atalaya",
            Model = ModelName,
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
            throw new AuditorAuthenticationException(readiness.Message);
        }

        try
        {
            CopilotSession session = await _client!.CreateSessionAsync(config, ct);
            try
            {
                await session.SendAndWaitAsync(prompt, SendTimeout, ct);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        // BUGFIX-CUOTA: UN solo camino de traduccion. Antes habia dos catch con sus propios
        // detectores y todo lo que no casara con ninguno se escapaba crudo hasta la vista.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// Un fallo del proveedor convertido en la excepcion que le corresponde (BUGFIX-CUOTA).
    /// <para>
    /// La clasificacion entera vive en <see cref="CopilotFailure"/> y no aqui: la misma pregunta
    /// —«de que se ha quejado el proveedor»— se hace desde el chequeo de disponibilidad y desde la
    /// ejecucion, y con dos copias del criterio una de las dos se queda vieja. Fue exactamente lo
    /// que paso: el detector de «sin asiento» llevaba «quota» dentro.
    /// </para>
    /// </summary>
    private Exception Translate(Exception ex)
    {
        bool hasToken = CurrentToken() is not null;
        AgentReadiness bad = CopilotFailure.Diagnose(ex, hasToken, ModelName);
        _logger.LogWarning(ex, "Copilot rechazo la operacion: {Problem}", bad.Problem);

        return bad.Problem == AgentProblem.ModelUnavailable
            ? new AuditorModelUnavailableException(
                ModelName, CopilotHelp.ModelUnavailable(ModelName), CopilotFailure.Raw(ex), ex)
            : new AuditorProviderException(bad.Message, bad.Problem, bad.Detail, ex);
    }

    /// <summary>
    /// El runtime rechazo el modelo (F5.15). Se conserva el nombre porque F5.15 lo dejo probado con
    /// su tabla de mensajes; el criterio vive ya en el clasificador comun.
    /// </summary>
    public static bool LooksLikeModelUnavailable(Exception ex)
        => CopilotFailure.Classify(ex, hasToken: true) == AgentProblem.ModelUnavailable;

    /// <summary>
    /// Mensajes cuyos deltas ya se han emitido: evita duplicar el texto cuando, al cerrar el turno,
    /// el SDK manda además el mensaje completo.
    /// </summary>
    private readonly HashSet<string> _streamedMessages = new(StringComparer.Ordinal);

    /// <summary>
    /// F5.2: se emite el TEXTO del agente, no un punto por evento.
    /// <para>
    /// Hasta aquí esto invocaba <c>TextStreamed(".")</c>, así que la columna de actividad de V5 era
    /// literalmente una fila de puntos: se veía que el agente seguía vivo y nada más. El SDK trae el
    /// contenido en <c>AssistantMessageDeltaData.DeltaContent</c> (streaming) y el mensaje entero en
    /// <c>AssistantMessageData.Content</c> al cerrar; emitir los dos duplicaría el texto, así que el
    /// mensaje completo solo se emite si de él no llegó ningún delta.
    /// </para>
    /// </summary>
    internal void OnSessionEvent(SessionEvent ev)
    {
        switch (ev)
        {
            case AssistantUsageEvent usage:
                UsageReported?.Invoke(UsageAdapter.From(usage.Data));
                break;

            case AssistantMessageDeltaEvent delta:
                string? chunk = delta.Data?.DeltaContent;
                if (!string.IsNullOrEmpty(chunk))
                {
                    if (delta.Data?.MessageId is { Length: > 0 } id)
                    {
                        lock (_streamedMessages)
                        {
                            _streamedMessages.Add(id);
                        }
                    }

                    TextStreamed?.Invoke(chunk!);
                }

                break;

            case AssistantMessageEvent message:
                string? full = message.Data?.Content;
                string? messageId = message.Data?.MessageId;
                bool alreadyStreamed;
                lock (_streamedMessages)
                {
                    alreadyStreamed = messageId is { Length: > 0 } && _streamedMessages.Remove(messageId);
                }

                if (!alreadyStreamed && !string.IsNullOrEmpty(full))
                {
                    TextStreamed?.Invoke(full!);
                }

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

    /// <summary>
    /// Cuánto se espera como MUCHO a que el runtime se libere. El cierre de la aplicación reparte
    /// 10 s entre todo lo que haya que soltar (<c>App.OnExit</c>), así que el agente no puede
    /// quedarse con el presupuesto entero: si el CLI no se va en 5 s, se le deja de esperar y el
    /// proceso sigue cerrando. Un cierre lento es molesto; uno que no termina deja un Atalaya
    /// zombi sondeando el hub, que es el fallo que D-086 costó descubrir.
    /// </summary>
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

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
            Task dispose = client.DisposeAsync().AsTask();
            if (await Task.WhenAny(dispose, Task.Delay(DisposeTimeout)) != dispose)
            {
                _logger.LogWarning(
                    "Copilot: el runtime no se liberó en {Seconds}s; se sigue cerrando sin esperarlo",
                    DisposeTimeout.TotalSeconds);
                return;
            }

            await dispose;
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
