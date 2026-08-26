namespace Atalaya.Copilot;

/// <summary>
/// A deterministic, seat-free agent (§11) that exercises the entire audit pipeline. Tests script
/// which findings/verdicts it reports; production can inject it when no Copilot seat is available.
/// </summary>
public sealed class FakeCopilotAgent : ICopilotAgent
{
    private readonly Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>> _auditScript;
    private readonly Func<VerifyTarget, string> _verdictScript;
    private readonly Func<AuditUnitRequest, IEnumerable<VerdictArgs>>? _reconcileScript;
    private readonly Func<AuditUnitRequest, IEnumerable<AddLocationsArgs>>? _extendScript;
    private readonly Func<IReadOnlyList<AgentModel>>? _modelsScript;
    private readonly Func<AuditUnitRequest, IEnumerable<SuppressedByPatternArgs>>? _suppressScript;
    private readonly string? _modelName;

    /// <param name="reconcileScript">
    /// F4: qué veredictos emite el agente sobre los hallazgos existentes de la unidad. Por defecto
    /// declara TODOS "presente" — el comportamiento de un auditor que reconcilia completo. Pasa un
    /// script propio para simular omisiones, "arreglado", o IDs inexistentes.
    /// </param>
    /// <param name="extendScript">
    /// F4.1: qué hallazgos existentes extiende el agente con ubicaciones nuevas.
    /// </param>
    /// <param name="modelsScript">
    /// F5.1: qué devuelve <see cref="ListModelsAsync"/>. Puede lanzar, para ejercitar el camino
    /// "no se pudo obtener la lista" de Ajustes sin quedarse sin red de verdad.
    /// </param>
    /// <param name="modelName">
    /// F5.1: el modelo que la sesión registra. Por defecto <c>fake-model</c>; los tests que
    /// comprueban que el modelo elegido llega al informe pasan el suyo.
    /// </param>
    /// <param name="suppressScript">
    /// F5.12: qué declara el auditor haberse callado por patrón en <c>unit_done</c>. Por defecto no
    /// declara nada. Es lo que permite ejercitar el circuito entero de la supresión por patrón
    /// —prompt, contadores, sesión, informe y contador de trabajo del patrón— sin asiento de
    /// Copilot y sin depender del juicio de un modelo real.
    /// </param>
    public FakeCopilotAgent(
        Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>>? auditScript = null,
        Func<VerifyTarget, string>? verdictScript = null,
        Func<AuditUnitRequest, IEnumerable<VerdictArgs>>? reconcileScript = null,
        Func<AuditUnitRequest, IEnumerable<AddLocationsArgs>>? extendScript = null,
        Func<IReadOnlyList<AgentModel>>? modelsScript = null,
        string? modelName = null,
        Func<AuditUnitRequest, IEnumerable<SuppressedByPatternArgs>>? suppressScript = null)
    {
        _auditScript = auditScript ?? (_ => Array.Empty<SubmitFindingArgs>());
        _verdictScript = verdictScript ?? (_ => "confirmado");
        _reconcileScript = reconcileScript;
        _extendScript = extendScript;
        _modelsScript = modelsScript;
        _suppressScript = suppressScript;
        _modelName = modelName;
    }

    public string? ModelName => _modelName ?? "fake-model";

    public event Action<string>? TextStreamed;

    public event Action<UsageSample>? UsageReported;

    public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<AgentReadiness> CheckAsync(CancellationToken ct)
        => Task.FromResult(new AgentReadiness(true, "Agente falso listo (sin Copilot real)."));

    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
        => Task.FromResult(_modelsScript is null
            ? (IReadOnlyList<AgentModel>)new[] { new AgentModel("fake-model", "Fake model", 1.0) }
            : _modelsScript());

    public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
    {
        TextStreamed?.Invoke($"[fake] auditando {request.UnitPath}\n");

        // Reconciliación primero (F4): el auditor se pronuncia sobre lo que ya existe antes de
        // reportar nada nuevo. Por defecto, todo "presente".
        VerdictArgs[] verdicts = (_reconcileScript is not null
                ? _reconcileScript(request)
                : request.Existing.Select(e => new VerdictArgs(e.FindingId, "presente", "sigue en el código (fake)")))
            .ToArray();
        if (verdicts.Length > 0)
        {
            ReportVerdictsResult verdictResult = toolbox.ReportVerdicts(verdicts);
            for (int i = 0; i < verdicts.Length; i++)
            {
                ReportVerdictResult r = verdictResult.Results[i];
                TextStreamed?.Invoke(r.Accepted
                    ? $"[fake] veredicto {verdicts[i].Verdict} sobre {verdicts[i].FindingId}\n"
                    : $"[fake] veredicto rechazado ({r.Error})\n");
            }
        }

        // The fake agent honours the batching contract (F3 Hito 1c): all findings for a unit go
        // in a single tool call. This is what the real agent is instructed to do too.
        SubmitFindingArgs[] batch = _auditScript(request).ToArray();
        ct.ThrowIfCancellationRequested();
        if (batch.Length > 0)
        {
            SubmitFindingsResult batchResult = toolbox.SubmitFindings(batch);
            for (int i = 0; i < batch.Length; i++)
            {
                SubmitFindingResult r = batchResult.Results[i];
                TextStreamed?.Invoke(r.Accepted
                    ? $"[fake] hallazgo aceptado: {batch[i].Title}\n"
                    : $"[fake] hallazgo rechazado ({r.Error}): {batch[i].Title}\n");
            }
        }

        foreach (AddLocationsArgs ext in _extendScript?.Invoke(request) ?? Array.Empty<AddLocationsArgs>())
        {
            AddLocationsResult r = toolbox.AddLocations(ext.FindingId, ext.Locations);
            TextStreamed?.Invoke(r.Accepted
                ? $"[fake] {r.Added} ubicacion(es) anadidas a {ext.FindingId}\n"
                : $"[fake] extension rechazada ({r.Error})\n");
        }

        // Simulate token usage proportional to the unit size.
        long input = Math.Max(1, request.UnitContent.Length / 4);
        UsageReported?.Invoke(new UsageSample(input, input / 3, null, ModelName));

        SuppressedByPatternArgs[] suppressed = (_suppressScript?.Invoke(request)
                                                ?? Array.Empty<SuppressedByPatternArgs>()).ToArray();
        foreach (SuppressedByPatternArgs s in suppressed)
        {
            TextStreamed?.Invoke($"[fake] {s.Count} deteccion(es) calladas por el patron {s.PatternId}\n");
        }

        toolbox.UnitDone(request.UnitPath, "revisión completa (fake)", suppressed);
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
