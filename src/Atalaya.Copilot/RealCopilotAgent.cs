using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.Copilot;

/// <summary>
/// The real agent: wraps the GitHub Copilot SDK (§6.1–6.3). ONE client per process, created with
/// <c>UseLoggedInUser = true</c> and a BaseDirectory under LOCALAPPDATA so each user consumes their
/// own seat. The agent only ever calls our registered tools; the permission handler rejects
/// everything else (shell, files, network), so it can touch nothing. Compiled against SDK 1.0.11;
/// its runtime path needs a Copilot seat (unavailable in CI — the fake covers tests).
/// </summary>
public sealed class RealCopilotAgent : ICopilotAgent, IAsyncDisposable
{
    private readonly string _baseDirectory;
    private readonly ILogger _logger;
    private readonly string? _model;
    private CopilotClient? _client;
    private bool _started;

    public RealCopilotAgent(string baseDirectory, ILogger? logger = null, string? model = null)
    {
        _baseDirectory = baseDirectory;
        _logger = logger ?? NullLogger.Instance;
        _model = model;
    }

    public string? ModelName => _model;

    public event Action<string>? TextStreamed;

    public event Action<UsageSample>? UsageReported;

    public async Task<bool> EnsureReadyAsync(CancellationToken ct)
    {
        try
        {
            await EnsureStartedAsync(ct);
            await _client!.PingAsync("atalaya", ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot not ready (auth?). Show the 'run copilot in a terminal' help.");
            return false;
        }
    }

    public async Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);

        SubmitFindingResult SubmitFinding(
            string ruleId, string pillar, string tag, string severity, string title,
            string description, string impact, string recommendation, SubmitLocation[] locations, string? symbol)
            => toolbox.SubmitFinding(new SubmitFindingArgs(
                ruleId, pillar, tag, severity, title, description, impact, recommendation, locations, symbol));

        void UnitDone(string unitPath, string summary) => toolbox.UnitDone(unitPath, summary);

        string ReadSignatures(string path) => toolbox.ReadSignatures(path);

        var config = NewSessionConfig();
        AddTool(config, SubmitFinding, "submit_finding",
            "Reporta un hallazgo. La app valida y persiste; devuelve {accepted, duplicateOf}.");
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
        CopilotSession session = await _client!.CreateSessionAsync(config, ct);
        try
        {
            await session.SendAndWaitAsync(prompt, null, ct);
        }
        finally
        {
            await session.DisposeAsync();
        }
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
        if (_started)
        {
            return;
        }

        _client ??= new CopilotClient(new CopilotClientOptions
        {
            UseLoggedInUser = true,
            BaseDirectory = _baseDirectory,
            Logger = _logger,
        });
        await _client.StartAsync(ct);
        _started = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
