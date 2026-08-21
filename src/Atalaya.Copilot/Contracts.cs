using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Copilot;

/// <summary>The <c>submit_finding</c> payload as the agent delivers it (§6.2). All strings.</summary>
public sealed record SubmitFindingArgs(
    string RuleId,
    string Pillar,
    string Tag,
    string Severity,
    string Title,
    string Description,
    string Impact,
    string Recommendation,
    IReadOnlyList<SubmitLocation> Locations,
    string? Symbol);

public sealed record SubmitLocation(string Path, int Line, string? Snippet);

/// <summary>Result returned to the agent from <c>submit_finding</c> (§6.2).</summary>
public sealed record SubmitFindingResult(bool Accepted, string? DuplicateOf = null, string? Error = null);

/// <summary>Per-call token/cost sample from the SDK usage event (§6.3), isolated from the SDK types.</summary>
public sealed record UsageSample(long InputTokens, long OutputTokens, decimal? Cost, string? Model);

/// <summary>Whether the agent can run, with a human-readable reason (§6.1 help screen).</summary>
public sealed record AgentReadiness(bool Ready, string Message);

/// <summary>
/// Thrown when a Copilot operation fails because the CLI is not authenticated (§6.1). Carries the
/// help text so the UI can show "ejecuta `copilot` y autentícate" instead of a raw SDK error.
/// </summary>
public sealed class CopilotAuthenticationException : Exception
{
    public CopilotAuthenticationException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Canonical help text for the not-authenticated case (§6.1).</summary>
public static class CopilotHelp
{
    public const string NotAuthenticated =
        "Copilot no está autenticado en esta máquina. Instala el CLI (npm install -g @github/copilot), "
        + "ejecuta `copilot` en una terminal, usa /login una vez con tu cuenta con asiento de Copilot, y reintenta.";
}

/// <summary>What the app hands the agent to audit one unit (§5.1.3).</summary>
public sealed record AuditUnitRequest(
    string UnitPath,
    string UnitContent,
    string Prompt,
    TechStack Stack,
    AuditMode Mode);

/// <summary>A verify target (§5.4): a finding to re-check, re-anchored by snippet.</summary>
public sealed record VerifyTarget(string FindingUlid, string Path, int Line, string? Snippet, string Title, string Description);

public sealed record VerifyRequest(string Prompt, IReadOnlyList<VerifyTarget> Targets);

/// <summary>
/// The tools the app exposes to the agent for an audit (§6.2). The app validates and persists;
/// the agent only reports. <see cref="ReadSignatures"/> is the only extra code access allowed.
/// </summary>
public interface IAuditToolbox
{
    SubmitFindingResult SubmitFinding(SubmitFindingArgs args);

    void UnitDone(string unitPath, string summary);

    string ReadSignatures(string path);
}

/// <summary>The single verify tool (§6.2). Uses the ULID, never the mutable displayId.</summary>
public interface IVerifyToolbox
{
    void SubmitVerdict(string findingUlid, string verdict, string evidence);
}

/// <summary>
/// The audit agent abstraction (§11): the seam behind which either the real Copilot SDK or the
/// injectable fake sits. The whole session pipeline is driven through this, so tests exercise it
/// end-to-end without a Copilot seat.
/// </summary>
public interface ICopilotAgent
{
    /// <summary>Model in use, if known (for session records).</summary>
    string? ModelName { get; }

    /// <summary>Streamed assistant text, for the live session view (V5).</summary>
    event Action<string>? TextStreamed;

    /// <summary>Per-call usage, accumulated by the session (§6.3).</summary>
    event Action<UsageSample>? UsageReported;

    /// <summary>Verifies the agent can run; false means auth is needed (§6.1 help screen).</summary>
    Task<bool> EnsureReadyAsync(CancellationToken ct);

    /// <summary>Detailed readiness check (auth state + reason) for the "Comprobar Copilot" action.</summary>
    Task<AgentReadiness> CheckAsync(CancellationToken ct);

    /// <summary>Audits one unit, reporting via <paramref name="toolbox"/> and ending on unit_done.</summary>
    Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct);

    /// <summary>Re-verifies findings, reporting verdicts via <paramref name="toolbox"/>.</summary>
    Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct);
}
