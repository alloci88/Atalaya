using Atalaya.Agents;
using Atalaya.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.OpenAI;

/// <summary>
/// <b>El primer proveedor por API</b> (PROV-3): cualquier endpoint que hable el dialecto de
/// `chat/completions`.
/// <para>
/// <b>Por qué uno y no siete.</b> Ese dialecto lo hablan OpenAI, Azure OpenAI, Mistral, Groq,
/// OpenRouter, Ollama, LM Studio y vLLM. Un proveedor con URL base, modelo y clave los cubre a
/// todos, incluidos los locales —que son gratis, y por tanto la primera forma de probar Atalaya
/// sin factura—. Por eso el identificador no nombra a ninguna casa: nombra al dialecto.
/// </para>
/// <para>
/// <b>Y es el que no depende de un login de máquina</b>, que es lo que hace posible auditar sin
/// nadie delante y en CI.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleProvider
    : IAssistedFixProvider, INarratingAuditor, ICuttingAuditor
{
    /// <summary>
    /// El identificador que se escribe en sesiones, hallazgos e informes. Constante y no un literal
    /// repartido, como en las otras dos casas.
    /// </summary>
    public const string Id = "openai-compatible";

    private readonly Func<OpenAiEndpoint> _endpoint;
    private readonly Func<string?> _key;
    private readonly ILogger _logger;

    /// <param name="endpoint">A qué se habla. Se lee en CADA uso, por lo de BUGFIX-AJUSTES.</param>
    /// <param name="key">
    /// La clave, del almacén de secretos. Es una función y no un valor: así esta clase nunca la
    /// retiene, y lo que no se retiene no se vuelca en un log ni en un volcado de memoria.
    /// </param>
    public OpenAiCompatibleProvider(
        Func<OpenAiEndpoint> endpoint,
        Func<string?> key,
        ILogger? logger = null)
    {
        _endpoint = endpoint;
        _key = key;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public string ProviderName => "Endpoint OpenAI-compatible";

    /// <summary>
    /// <b>Opcional, como Claude Code</b>: a quien no lo configure no se le pide nada.
    /// </summary>
    public bool IsOptional => true;

    /// <summary>
    /// <b>Presente siempre</b>, y es deliberado. «Presente» es «está en esta máquina», y aquí no
    /// hay nada que instalar: es HTTP. Si además se exigiera estar configurado, el desplegable de
    /// Ajustes no lo ofrecería hasta configurarlo y no habría dónde configurarlo — la pescadilla.
    /// Que falte la URL o la clave lo dice <see cref="CheckAsync"/>, que es de donde cuelga
    /// «no listo».
    /// </summary>
    public bool IsPresent => true;

    /// <summary>
    /// <b>La entrada INCLUYE lo cacheado</b>: en este dialecto `prompt_tokens` es el prompt entero
    /// y `prompt_tokens_details.cached_tokens` dice qué parte de él vino de caché.
    /// </summary>
    public TokenAccounting Accounting => TokenAccounting.InputIncludesCache;

    /// <summary>
    /// Factura en dólares y no declara frase de «sin tarifa»: si no hay tarifa escrita para este
    /// proveedor y su modelo, es un hueco de verdad y el agregado sale parcial (D-787). Cada
    /// endpoint tiene sus precios y la siembra no puede adivinarlos; con Ollama la tarifa es 0 y la
    /// escribe quien lo use.
    /// </summary>
    public ProviderBilling Billing { get; } = ProviderBilling.Default;

    /// <inheritdoc/>
    public string? ModelName
    {
        get
        {
            string model = _endpoint().Model;
            return string.IsNullOrWhiteSpace(model) ? null : model;
        }
    }

    public event Action<string>? TextStreamed;

    public event Action<UsageSample>? UsageReported;

    /// <inheritdoc/>
    public event Action<ToolStream>? ToolStreamed;

    /// <inheritdoc/>
    public event Action<string>? CutSkipped;

    /// <summary>Lo que hay configurado ahora mismo. Para quien lo necesite sin duplicar la lectura.</summary>
    internal OpenAiEndpoint Endpoint => _endpoint();

    /// <summary>La clave de ahora, o null. Interna: no sale de este ensamblado.</summary>
    internal string? Key => _key();

    /// <summary>Por dónde escribe. Interna por lo mismo.</summary>
    internal ILogger Log => _logger;

    /// <summary>Levanta los eventos desde el resto del ensamblado, sin exponerlos fuera.</summary>
    internal void RaiseText(string text) => TextStreamed?.Invoke(text);

    internal void RaiseUsage(UsageSample sample) => UsageReported?.Invoke(sample);

    internal void RaiseToolStream(ToolStream stream) => ToolStreamed?.Invoke(stream);

    internal void RaiseCutSkipped(string why) => CutSkipped?.Invoke(why);

    /// <inheritdoc/>
    public async Task<bool> EnsureReadyAsync(CancellationToken ct)
        => (await CheckAsync(ct).ConfigureAwait(false)).Ready;

    /// <summary>
    /// <b>¿Se puede auditar con esto?</b> En el andamio contesta lo único que se puede contestar sin
    /// tocar la red: si falta configuración, lo dice; si no falta, se declara desconocido en vez de
    /// afirmar que conecta. Quien lo complete llamará al endpoint (PROV-3 §2).
    /// </summary>
    public Task<AgentReadiness> CheckAsync(CancellationToken ct)
    {
        OpenAiEndpoint endpoint = _endpoint();

        if (OpenAiEndpoint.WhyNot(endpoint.BaseUrl) is { } mal)
        {
            return Task.FromResult(new AgentReadiness(false, mal, AgentProblem.NotAuthenticated));
        }

        if (string.IsNullOrWhiteSpace(endpoint.Model))
        {
            return Task.FromResult(new AgentReadiness(
                false,
                "Falta el modelo. Escríbelo en Ajustes → Proveedor y modelo: cada endpoint tiene los "
                + "suyos y no hay una lista común que ofrecer.",
                AgentProblem.ModelUnavailable));
        }

        if (string.IsNullOrWhiteSpace(_key()))
        {
            return Task.FromResult(new AgentReadiness(
                false,
                "Falta la clave de API. Se guarda cifrada en esta máquina, nunca en el hub.",
                AgentProblem.NotAuthenticated));
        }

        return Task.FromResult(new AgentReadiness(true, "Configurado."));
    }

    /// <summary>
    /// <b>Sin lista de modelos, y a propósito.</b> El modelo es texto libre porque cada endpoint
    /// tiene los suyos: `GET /models` lo sirven unos sí y otros no, y lo que devuelven no se parece.
    /// Devolver vacío es lo honesto — y `ModelResolver` ya sabe no reescribir nada ante una lista
    /// vacía (D-711).
    /// </summary>
    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

    /// <inheritdoc/>
    public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        => throw new NotImplementedException(
            "PROV-3: el bucle de herramientas de la auditoría todavía no está escrito.");

    /// <inheritdoc/>
    public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
        => throw new NotImplementedException(
            "PROV-3: la verificación todavía no está escrita.");
}
