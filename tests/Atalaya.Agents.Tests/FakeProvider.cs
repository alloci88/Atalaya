using Atalaya.Domain.Model;

namespace Atalaya.Agents.Tests;

/// <summary>
/// <b>Una casa que no es ninguna de las dos</b> (PROV-2 §5): el doble con el que se comprueba que
/// un proveedor nuevo puede conducir un barrido entero sin tocar el código de nadie.
/// <para>
/// <b>Por qué aquí y no en el proyecto de una casa.</b> <c>FakeCopilotAgent</c> vive en
/// <c>Atalaya.Copilot</c> por razones históricas y sostiene setecientos y pico tests, así que se
/// queda donde está. Pero eso significa que hoy el único doble que ejercita el pipeline entero
/// viaja dentro del paquete de un proveedor concreto: si un tercero necesitara algo que Copilot
/// da por hecho, nadie lo notaría. Éste vive en <c>Atalaya.Agents.Tests</c>, junto al contrato y
/// lejos de las dos casas, y no se llama como ninguna.
/// </para>
/// <para>
/// <b>Declara lo MÍNIMO.</b> Su identificador, cómo se llama y el modelo con el que dice haber
/// corrido; nada más. Todo lo demás —si es opcional, si está presente, si es el de fábrica, si
/// reclama el histórico sin atribuir, cómo cuenta sus tokens, cómo factura, con qué nombre
/// guardaba su modelo antes— se queda en el valor por defecto del contrato, que es exactamente lo
/// que tendrá una casa nueva el día que se añada. Si un camino de la aplicación necesitara algo
/// más, el barrido de punta a punta lo descubre aquí y no a mitad de la entrega siguiente.
/// </para>
/// <para>
/// <b>Y el identificador es inventado a propósito.</b> Una sesión conducida por este doble queda
/// escrita en el hub con <see cref="Id"/>, así que si alguno de estos datos llegara a un hub de
/// verdad se vería de dónde salió en vez de disfrazarse de una casa real.
/// </para>
/// </summary>
public sealed class FakeProvider : IAssistedFixProvider
{
    /// <summary>El identificador inventado. No es el de ninguna casa, y ésa es su gracia.</summary>
    public const string Id = "casa-de-pruebas";

    private readonly Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>> _auditScript;
    private readonly Func<AuditUnitRequest, IEnumerable<VerdictArgs>>? _reconcileScript;
    private readonly Func<VerifyTarget, string> _verdictScript;
    private readonly Func<IReadOnlyList<AgentModel>>? _modelsScript;
    private readonly Func<FixRequest, FixDoneArgs?>? _fixScript;

    /// <param name="auditScript">Qué hallazgos reporta en cada pasada. Por defecto, ninguno.</param>
    /// <param name="reconcileScript">
    /// Qué veredicto da sobre los hallazgos que ya existían. Por defecto los declara todos
    /// «presente», que es lo que hace un auditor que reconcilia completo.
    /// </param>
    /// <param name="verdictScript">Qué veredicto emite en una sesión de verificación.</param>
    /// <param name="modelsScript">Qué modelos dice ofrecer. Puede lanzar, para ese camino.</param>
    /// <param name="fixScript">
    /// Cómo cierra un arreglo asistido. Sin script no arregla: <see cref="FixAsync"/> lanza lo que
    /// lanza el contrato, que es la verdad sobre una casa que no sabe hacerlo.
    /// </param>
    /// <param name="modelName">El modelo que la sesión registra.</param>
    public FakeProvider(
        Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>>? auditScript = null,
        Func<AuditUnitRequest, IEnumerable<VerdictArgs>>? reconcileScript = null,
        Func<VerifyTarget, string>? verdictScript = null,
        Func<IReadOnlyList<AgentModel>>? modelsScript = null,
        Func<FixRequest, FixDoneArgs?>? fixScript = null,
        string? modelName = null)
    {
        _auditScript = auditScript ?? (_ => Array.Empty<SubmitFindingArgs>());
        _reconcileScript = reconcileScript;
        _verdictScript = verdictScript ?? (_ => "confirmado");
        _modelsScript = modelsScript;
        _fixScript = fixScript;
        ModelName = modelName ?? "modelo-de-pruebas";
    }

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public string ProviderName => "Casa de pruebas";

    /// <inheritdoc/>
    public string? ModelName { get; }

    public event Action<string>? TextStreamed;

    public event Action<UsageSample>? UsageReported;

    public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<AgentReadiness> CheckAsync(CancellationToken ct)
        => Task.FromResult(new AgentReadiness(true, "Casa de pruebas lista."));

    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
        => Task.FromResult(_modelsScript is null
            ? (IReadOnlyList<AgentModel>)new[] { new AgentModel(ModelName!, "Modelo de pruebas") }
            : _modelsScript());

    /// <summary>
    /// Una pasada de auditoría: reconcilia lo que ya había, reporta lo suyo en UNA llamada
    /// —el contrato de agrupación que se le pide al auditor de verdad—, declara consumo y cierra
    /// la unidad. Es el circuito completo que el coordinador espera de cualquier casa.
    /// </summary>
    public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TextStreamed?.Invoke($"[casa-de-pruebas] auditando {request.UnitPath}\n");

        VerdictArgs[] verdicts = (_reconcileScript is not null
                ? _reconcileScript(request)
                : request.Existing.Select(e => new VerdictArgs(e.FindingId, "presente", "sigue en el código")))
            .ToArray();
        if (verdicts.Length > 0)
        {
            toolbox.ReportVerdicts(verdicts);
        }

        SubmitFindingArgs[] batch = _auditScript(request).ToArray();
        if (batch.Length > 0)
        {
            toolbox.SubmitFindings(batch);
        }

        UsageReported?.Invoke(new UsageSample(
            Math.Max(1, request.UnitContent.Length / 4),
            Math.Max(1, request.UnitContent.Length / 12),
            null,
            ModelName));

        toolbox.UnitDone(request.UnitPath, "revisión completa (casa de pruebas)");
        return Task.CompletedTask;
    }

    public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
    {
        foreach (VerifyTarget target in request.Targets)
        {
            ct.ThrowIfCancellationRequested();
            toolbox.SubmitVerdict(target.FindingUlid, _verdictScript(target), "evidencia (casa de pruebas)");
        }

        UsageReported?.Invoke(new UsageSample(request.Targets.Count * 50L, request.Targets.Count * 10L, null, ModelName));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Un arreglo asistido mínimo: narra y cierra con lo que diga el guion. Sin guion no se
    /// implementa —cae en el valor por defecto del contrato, que lanza—, porque una casa que no
    /// sabe arreglar tiene que decirlo y no fingir un cierre vacío.
    /// </summary>
    public Task FixAsync(FixRequest request, FixConversation conversation, CancellationToken ct)
    {
        FixDoneArgs? done = _fixScript?.Invoke(request);
        if (done is null)
        {
            throw new NotSupportedException($"{nameof(FakeProvider)} no implementa el arreglo asistido.");
        }

        TextStreamed?.Invoke($"[casa-de-pruebas] arreglando en {request.CloneRoot}\n");
        conversation.Toolbox.FixDone(done);
        UsageReported?.Invoke(new UsageSample(100, 40, null, ModelName));
        return Task.CompletedTask;
    }
}
