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
    private readonly Func<IChatEndpoint>? _chat;
    private readonly Func<int?>? _maxTurns;

    /// <param name="endpoint">A qué se habla. Se lee en CADA uso, por lo de BUGFIX-AJUSTES.</param>
    /// <param name="key">
    /// La clave, del almacén de secretos. Es una función y no un valor: así esta clase nunca la
    /// retiene, y lo que no se retiene no se vuelca en un log ni en un volcado de memoria.
    /// </param>
    /// <param name="chat">
    /// <b>Con qué se habla</b>: el transporte, del que aquí solo se conoce <see cref="IChatEndpoint"/>.
    /// Es una función por lo mismo que lo es la configuración —se resuelve en cada uso, no al
    /// construir— y entra por parámetro para que el bucle se pueda probar con un doble guionizado
    /// en memoria: esta capa no sabe de HTTP, y probarla por HTTP sería probar la de al lado.
    /// </param>
    /// <param name="maxTurns">
    /// El techo de vueltas del bucle, cuando quien construye esto sabe leerlo de los ajustes
    /// (<c>Thresholds.MaxCallsPerPass</c>). Null —o desactivado— cae en
    /// <see cref="AuditLoopLimits.DefaultMaxTurns"/>: el bucle es nuestro y no puede girar sin fin.
    /// </param>
    public OpenAiCompatibleProvider(
        Func<OpenAiEndpoint> endpoint,
        Func<string?> key,
        ILogger? logger = null,
        Func<IChatEndpoint>? chat = null,
        Func<int?>? maxTurns = null)
    {
        _endpoint = endpoint;
        _key = key;
        _logger = logger ?? NullLogger.Instance;
        _chat = chat;
        _maxTurns = maxTurns;
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

    /// <summary>
    /// <b>Una pasada sobre una unidad</b> (PROV-3 §4): el prompt entero en un mensaje de usuario,
    /// las siete herramientas del catálogo compartido ofrecidas como <c>functions</c>, y vuelta a
    /// preguntar hasta que <c>unit_done</c> cierre o el techo lo pare.
    /// <para>
    /// <b>El prompt viaja entero y sin partir.</b> <see cref="AuditUnitRequest.StablePrefix"/>
    /// existe para que una casa que sepa marcar un prefijo cacheable lo marque; en este dialecto no
    /// se marca nada — la caché de prompt la decide el endpoint y lo único que se puede hacer con
    /// ella es LEERLA en <c>prompt_tokens_details.cached_tokens</c>. Mandar el prompt entero es
    /// además lo que garantiza que el prefijo se repita byte a byte entre pasadas, que es de lo que
    /// vive esa caché.
    /// </para>
    /// </summary>
    public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        => ChatToolLoop.RunAsync(
            Chat(),
            AuditorFunctions.ForAudit(toolbox),
            request.Prompt,
            AuditLoopLimits.MaxTurns(_maxTurns?.Invoke()),
            RaiseText,
            Report,
            RaiseToolStream,
            RaiseCutSkipped,
            ct);

    /// <summary>
    /// <b>Una sesión de verificación</b>: el mismo bucle con la única herramienta de verificar.
    /// Aquí <b>no hay terminal</b> —cerrar una unidad no significa nada— así que la conversación
    /// acaba cuando el modelo deja de llamar herramientas, y el techo sigue estando por si no lo
    /// hace. Que se llegue al techo se escribe en el registro y no por <c>CutSkipped</c>: ese
    /// evento cuenta pasadas de auditoría sin cortar, y una verificación no es ninguna.
    /// </summary>
    public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
        => ChatToolLoop.RunAsync(
            Chat(),
            AuditorFunctions.ForVerify(toolbox),
            request.Prompt,
            AuditLoopLimits.MaxTurns(_maxTurns?.Invoke()),
            RaiseText,
            Report,
            RaiseToolStream,
            why => _logger.LogWarning("openai-compatible: verificación sin cerrar — {Why}", why),
            ct);

    /// <summary>
    /// <b>El consumo de UNA llamada</b> (PROV-3 §6), traducido del dialecto al contrato común:
    /// <c>prompt_tokens</c> es la entrada —que aquí <b>incluye</b> lo cacheado, y por eso este
    /// proveedor declara <see cref="TokenAccounting.InputIncludesCache"/>—, <c>completion_tokens</c>
    /// la salida y <c>prompt_tokens_details.cached_tokens</c> la caché LEÍDA.
    /// <para>
    /// <b>La caché escrita va a cero, y eso aquí es la verdad y no un hueco.</b>
    /// <c>chat/completions</c> no tiene ese concepto: el endpoint gestiona la caché del prompt por
    /// su cuenta y no la cobra aparte, así que no hay un número que falte — hay un concepto que no
    /// existe. Inventarle un valor desviaría el coste; dejarlo como «no se sabe» sería declarar un
    /// hueco que nadie puede rellenar nunca.
    /// </para>
    /// <para>
    /// <b>El importe no se informa</b> (<c>Cost = null</c>): este dialecto no lo manda, y el que
    /// vale es el que la aplicación deriva de los tokens con la tarifa de <c>model-rates.json</c>
    /// para <c>openai-compatible</c> + modelo. La siembra no trae ninguna para esta casa —cada
    /// endpoint tiene sus precios y no se pueden adivinar—, así que hasta que alguien escriba la
    /// suya el agregado sale <b>parcial</b> (D-787), que es lo correcto.
    /// </para>
    /// <para>
    /// <b>Y se publica UNA muestra por llamada, la mande el endpoint o no.</b> En este dialecto
    /// <c>usage</c> es opcional al hacer streaming; si no llegara y no se publicara nada, el
    /// contador de llamadas de la sesión —y con él el techo del coordinador— se quedaría a cero
    /// para siempre. Una llamada sin tokens es «el endpoint no lo dijo»; una llamada que no se
    /// cuenta es una llamada que no ocurrió, y ocurrió.
    /// </para>
    /// </summary>
    private void Report(ChatUsage? usage)
        => RaiseUsage(new UsageSample(
            usage?.PromptTokens ?? 0,
            usage?.CompletionTokens ?? 0,
            Cost: null,
            Model: ModelName,
            CacheReadTokens: usage?.CachedPromptTokens ?? 0,
            CacheWriteTokens: 0,
            Calls: 1));

    /// <summary>
    /// El transporte. Se resuelve en cada uso —como la configuración— y si no se ha cableado se
    /// dice exactamente qué falta: lo que no está escrito es el HTTP, no el bucle.
    /// </summary>
    private IChatEndpoint Chat()
        => _chat?.Invoke()
           ?? throw new NotImplementedException(
               "PROV-3: este proveedor se construyó sin transporte. El bucle de herramientas está "
               + "escrito y espera un IChatEndpoint por el parámetro `chat` del constructor.");
}
