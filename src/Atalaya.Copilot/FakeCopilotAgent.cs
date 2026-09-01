namespace Atalaya.Copilot;

/// <summary>
/// A deterministic, seat-free agent (§11) that exercises the entire audit pipeline. Tests script
/// which findings/verdicts it reports; production can inject it when no Copilot seat is available.
/// </summary>
public sealed class FakeCopilotAgent : IAssistedFixProvider
{
    private readonly Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>> _auditScript;
    private readonly Func<VerifyTarget, string> _verdictScript;
    private readonly Func<VerifyTarget, string> _verdictEvidence;
    private readonly Func<AuditUnitRequest, IEnumerable<VerdictArgs>>? _reconcileScript;
    private readonly Func<AuditUnitRequest, IEnumerable<AddLocationsArgs>>? _extendScript;
    private readonly Func<IReadOnlyList<AgentModel>>? _modelsScript;
    private readonly Func<AuditUnitRequest, IEnumerable<SuppressedByPatternArgs>>? _suppressScript;
    private readonly Func<FixRequest, IEnumerable<FixStep>>? _fixScript;
    private readonly Action<string, IFixToolbox>? _fixFollowUp;
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
    /// <param name="fixScript">
    /// F6.9: los pasos de una sesión de arreglo —narrar, preguntar, leer, editar, compilar y
    /// cerrar—. Es lo que permite ejercitar el circuito entero del arreglo asistido sin asiento.
    /// </param>
    /// <param name="fixFollowUp">
    /// Qué hace el agente falso con una orden que el usuario encoló para el turno siguiente.
    /// </param>
    /// <param name="verdictEvidence">
    /// F12 §A: la evidencia que acompaña al veredicto. Por defecto una frase de relleno; los tests
    /// que comprueban que la causa REAL del modelo llega al historial pasan la suya — que es lo que
    /// distingue «no concluyente» de «no concluyente, y por esto».
    /// </param>
    public FakeCopilotAgent(
        Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>>? auditScript = null,
        Func<VerifyTarget, string>? verdictScript = null,
        Func<AuditUnitRequest, IEnumerable<VerdictArgs>>? reconcileScript = null,
        Func<AuditUnitRequest, IEnumerable<AddLocationsArgs>>? extendScript = null,
        Func<IReadOnlyList<AgentModel>>? modelsScript = null,
        string? modelName = null,
        Func<AuditUnitRequest, IEnumerable<SuppressedByPatternArgs>>? suppressScript = null,
        Func<FixRequest, IEnumerable<FixStep>>? fixScript = null,
        Action<string, IFixToolbox>? fixFollowUp = null,
        Func<VerifyTarget, string>? verdictEvidence = null)
    {
        _auditScript = auditScript ?? (_ => Array.Empty<SubmitFindingArgs>());
        _verdictScript = verdictScript ?? (_ => "confirmado");
        _verdictEvidence = verdictEvidence ?? (_ => "veredicto (fake)");
        _reconcileScript = reconcileScript;
        _extendScript = extendScript;
        _modelsScript = modelsScript;
        _suppressScript = suppressScript;
        _fixScript = fixScript;
        _fixFollowUp = fixFollowUp;
        _modelName = modelName;
    }

    public string? ModelName => _modelName ?? "fake-model";

    /// <summary>
    /// No se hace pasar por Copilot (F14). Una sesión conducida por el agente falso queda escrita
    /// en el hub con ESTE identificador, así que si alguna vez uno de estos datos llega al hub de
    /// verdad se ve de dónde salió en lugar de disfrazarse de una casa real.
    /// </summary>
    public string ProviderId => "fake";

    /// <inheritdoc/>
    public string ProviderName => "Agente falso";

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
            toolbox.SubmitVerdict(target.FindingUlid, _verdictScript(target), _verdictEvidence(target));
        }

        UsageReported?.Invoke(new UsageSample(request.Targets.Count * 50L, request.Targets.Count * 10L, null, ModelName));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Una sesión de arreglo guionizada (F6.9). Es lo que permite probar el circuito ENTERO —el
    /// ámbito de <c>apply_edit</c>, la elicitación, los snapshots, el descarte, el registro de la
    /// sesión <c>fix</c>— sin asiento de Copilot, igual que <c>AuditUnitAsync</c> hace con el
    /// barrido.
    /// </summary>
    public async Task FixAsync(FixRequest request, FixConversation conversation, CancellationToken ct)
    {
        TextStreamed?.Invoke($"[fake] arreglando en {request.CloneRoot}\n");
        conversation.Ready?.Invoke(new NoSteering());

        FixStep[] steps = (_fixScript?.Invoke(request) ?? Array.Empty<FixStep>()).ToArray();
        foreach (FixStep step in steps)
        {
            ct.ThrowIfCancellationRequested();

            if (step.Narration is { Length: > 0 } says)
            {
                TextStreamed?.Invoke(says);
            }

            if (step.Question is { Length: > 0 } question)
            {
                string? answer = await conversation.Questions.AskAsync(
                    question, step.Choices ?? Array.Empty<string>(), step.AllowFreeform, ct);
                TextStreamed?.Invoke($"[fake] el usuario respondió: {answer ?? "(nada)"}\n");
                step.OnAnswer?.Invoke(answer);
            }

            if (step.Read is { Length: > 0 } read)
            {
                ReadFileResult r = conversation.Toolbox.ReadFile(read);
                TextStreamed?.Invoke(r.Ok
                    ? $"[fake] leído {read} ({r.Remaining} lecturas restantes)\n"
                    : $"[fake] no se pudo leer {read}: {r.Error}\n");
            }

            if (step.Edit is { } edit)
            {
                ApplyEditResult r = conversation.Toolbox.ApplyEdit(edit.Path, edit.Reason, edit.Edits);
                TextStreamed?.Invoke(r.Applied
                    ? $"[fake] editado {edit.Path}\n"
                    : $"[fake] edición rechazada en {edit.Path}: {(r.Denied ? "denegada" : r.Error)}\n");
                step.OnEdit?.Invoke(r);
            }

            if (step.Build)
            {
                BuildAndTestResult r = conversation.Toolbox.RunBuildAndTests();
                TextStreamed?.Invoke($"[fake] build/tests: {(r.Ok ? "verde" : "rojo")}\n");
            }

            if (step.Done is { } done)
            {
                conversation.Toolbox.FixDone(done);
            }
        }

        UsageReported?.Invoke(new UsageSample(1200, 400, null, ModelName));

        // Igual que el agente real: al acabar el turno se pregunta si hay algo más que mandar.
        // Sin esto, la cola de órdenes del usuario nunca se ejercitaría en los tests.
        while (conversation.NextTurn is not null
               && await conversation.NextTurn(ct) is { Length: > 0 } more)
        {
            TextStreamed?.Invoke($"[fake] turno extra: {more}\n");
            _fixFollowUp?.Invoke(more, conversation.Toolbox);
        }
    }

    /// <summary>Un mando a distancia inerte: el agente falso no tiene sesión que dirigir.</summary>
    private sealed class NoSteering : IFixSteering
    {
        public Task<bool> SendAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>Lo que el agente falso hace en un paso de una sesión de arreglo (F6.9).</summary>
/// <param name="Narration">Texto que emite antes de actuar, como haría el agente real.</param>
/// <param name="Question">Una pregunta de elicitación, si este paso pregunta.</param>
/// <param name="Read">Un fichero que lee, si este paso lee.</param>
/// <param name="Edit">Una edición, si este paso edita.</param>
/// <param name="Build">Solicita compilar y pasar los tests.</param>
/// <param name="Done">Cierra la sesión con este resumen.</param>
public sealed record FixStep(
    string? Narration = null,
    string? Question = null,
    IReadOnlyList<string>? Choices = null,
    bool AllowFreeform = true,
    Action<string?>? OnAnswer = null,
    string? Read = null,
    FixStepEdit? Edit = null,
    Action<ApplyEditResult>? OnEdit = null,
    bool Build = false,
    FixDoneArgs? Done = null);

/// <summary>La edición de un paso guionizado.</summary>
public sealed record FixStepEdit(string Path, string Reason, FixEdit[] Edits);
