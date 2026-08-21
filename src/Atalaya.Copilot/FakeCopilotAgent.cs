namespace Atalaya.Copilot;

/// <summary>
/// A deterministic, seat-free agent (§11) that exercises the entire audit pipeline. Tests script
/// which findings/verdicts it reports; production can inject it when no Copilot seat is available.
/// </summary>
public sealed class FakeCopilotAgent : ICopilotAgent
{
    private readonly Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>> _auditScript;
    private readonly Func<VerifyTarget, string> _verdictScript;

    public FakeCopilotAgent(
        Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>>? auditScript = null,
        Func<VerifyTarget, string>? verdictScript = null)
    {
        _auditScript = auditScript ?? (_ => Array.Empty<SubmitFindingArgs>());
        _verdictScript = verdictScript ?? (_ => "confirmado");
    }

    public string? ModelName => "fake-model";

    public event Action<string>? TextStreamed;

    public event Action<UsageSample>? UsageReported;

    public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<AgentReadiness> CheckAsync(CancellationToken ct)
        => Task.FromResult(new AgentReadiness(true, "Agente falso listo (sin Copilot real)."));

    public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
    {
        TextStreamed?.Invoke($"[fake] auditando {request.UnitPath}\n");

        foreach (SubmitFindingArgs finding in _auditScript(request))
        {
            ct.ThrowIfCancellationRequested();
            SubmitFindingResult result = toolbox.SubmitFinding(finding);
            TextStreamed?.Invoke(result.Accepted
                ? $"[fake] hallazgo aceptado: {finding.Title}\n"
                : $"[fake] hallazgo rechazado ({result.Error}): {finding.Title}\n");
        }

        // Simulate token usage proportional to the unit size.
        long input = Math.Max(1, request.UnitContent.Length / 4);
        UsageReported?.Invoke(new UsageSample(input, input / 3, null, ModelName));

        toolbox.UnitDone(request.UnitPath, "revisión completa (fake)");
        return Task.CompletedTask;
    }

    public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
    {
        foreach (VerifyTarget target in request.Targets)
        {
            ct.ThrowIfCancellationRequested();
            toolbox.SubmitVerdict(target.FindingUlid, _verdictScript(target), "veredicto (fake)");
        }

        UsageReported?.Invoke(new UsageSample(request.Targets.Count * 50L, request.Targets.Count * 10L, null, ModelName));
        return Task.CompletedTask;
    }
}
