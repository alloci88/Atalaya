namespace Atalaya.Domain.Model;

/// <summary>
/// Cómo cuenta un proveedor sus tokens de entrada (F15). <b>No es un detalle: invertirlo desvía
/// todos los costes.</b>
/// </summary>
public enum TokenAccounting
{
    /// <summary>
    /// La entrada INCLUYE lo servido de caché y lo escrito en ella — es el total del prompt.
    /// Los tokens que se facturan a tarifa de entrada son, por tanto, <c>In − caché</c>.
    /// </summary>
    InputIncludesCache,

    /// <summary>
    /// La entrada EXCLUYE la caché: lo cacheado se informa aparte y no está dentro de <c>In</c>.
    /// Los tokens de entrada se facturan tal cual.
    /// </summary>
    InputExcludesCache,
}

/// <summary>Por qué una sesión no tiene coste calculable. Se DICE; no se rellena con un cero.</summary>
public enum CostUnavailable
{
    /// <summary>Hay coste.</summary>
    None = 0,

    /// <summary>La sesión no registró con qué modelo se hizo (histórico anterior al registro).</summary>
    ModelUnknown,

    /// <summary>El modelo está registrado pero no tiene tarifa en la tabla de la organización.</summary>
    RateMissing,

    /// <summary>La sesión no guardó tokens (muy antigua, o proveedor que no los dio).</summary>
    TokensMissing,

    /// <summary>
    /// Este proveedor <b>no factura a la organización</b>, así que su consumo no se tarifa
    /// (F16-RETOQUE §1). No es un dato que falte ni una tarifa por configurar: es que no hay
    /// factura que calcular. Los tokens y las llamadas siguen registrándose.
    /// </summary>
    NotBilled,
}

/// <summary>
/// El coste de algo, con su procedencia. Nunca es solo un número: o hay credits, o hay un motivo.
/// </summary>
/// <param name="Credits">Los AI credits. Null cuando no se puede calcular.</param>
/// <param name="Why">Por qué no se puede, cuando no se puede.</param>
/// <param name="BillableInputTokens">Los tokens de entrada que SÍ se facturan a tarifa plena.</param>
public sealed record CostResult(
    decimal? Credits,
    CostUnavailable Why = CostUnavailable.None,
    long BillableInputTokens = 0,
    long CachedInputTokens = 0,
    long CacheWriteTokens = 0,
    long OutputTokens = 0,
    string? Model = null)
{
    /// <summary>Se ha podido calcular.</summary>
    public bool HasValue => Credits is not null;

    /// <summary>Los dólares detrás de los credits. 1 credit = 0,01 $.</summary>
    public decimal? Usd => Credits / 100m;

    public static CostResult Unavailable(CostUnavailable why, string? model = null)
        => new(null, why, Model: model);
}

/// <summary>
/// Convierte tokens en <b>AI credits</b>, que es la unidad en la que GitHub factura Copilot desde
/// el <b>1 de junio de 2026</b> (F15).
/// <para>
/// <b>Qué cambió y por qué importa.</b> Hasta esa fecha se facturaba por «premium requests»:
/// llamadas × un multiplicador del modelo. Atalaya calculaba eso —lo que llamaba «unidades SDK»— y
/// era correcto entonces. Ahora los credits se consumen <b>por tokens</b> (entrada, salida y caché)
/// a las tarifas de API publicadas de cada modelo, así que la decisión original quedó del revés:
/// los tokens ya no son un detalle que absorbe la caché, <b>son la factura</b>. El sistema de
/// multiplicadores está retirado y no queda ni como opción.
/// </para>
/// <para>
/// <b>1 credit = 0,01 $.</b>
/// </para>
/// <para>
/// <b>La semántica de los tokens NO es la misma en los dos proveedores</b>, y se verificó
/// empíricamente antes de escribir esta fórmula porque contar la caché dos veces —o ninguna—
/// desviaría todos los costes:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Copilot incluye la caché en la entrada.</b> En una sesión real del hub: In 538.468,
/// CacheRead 368.618, CacheWrite 169.826 — y 538.468 − 368.618 = 169.850 ≈ CacheWrite. La entrada
/// es el prompt entero, del que una parte vino de caché.
/// </item>
/// <item>
/// <b>Claude Code la excluye.</b> En una sesión real: <c>input_tokens</c> 6 con
/// <c>cache_read_input_tokens</c> 19.990. Un 6 no puede contener a 19.990. Y la comprobación
/// definitiva: aplicando esta fórmula a los tokens que informó el CLI se reproduce <b>exactamente</b>
/// el coste que el propio CLI calculó (0,001075 $ en Haiku 4.5; 0,051106 $ en Sonnet 5, éste con la
/// tarifa de caché de una hora).
/// </item>
/// </list>
/// <para>
/// <b>Y desde F16-RETOQUE esa segunda casa ya no pasa por aquí.</b> El consumo de Claude Code va
/// contra la suscripción personal de quien lo usa y <b>no factura a la organización</b>, así que no
/// se tarifa: <see cref="IsBilled"/> lo para en la puerta y el resultado es
/// <see cref="CostUnavailable.NotBilled"/>. La medida de arriba no se borra —costó comprobarla y
/// explica por qué <see cref="AccountingOf"/> dice lo que dice—, pero ya no se usa para poner un
/// número delante de nadie. Lo que factura, y lo único que esta clase valora, es Copilot.
/// </para>
/// </summary>
public static class CreditCalculator
{
    /// <summary>Lo que vale un credit, en dólares. Publicado por GitHub.</summary>
    public const decimal UsdPerCredit = 0.01m;

    /// <summary>
    /// Cómo cuenta cada casa. Es un mapa y no una propiedad del proveedor porque esto se aplica
    /// sobre sesiones <b>ya guardadas</b> —Métricas relee meses de historia— y el proveedor que las
    /// escribió puede no estar registrado hoy, o no existir ya en esta versión.
    /// </summary>
    /// <summary>
    /// ¿El consumo de esta casa <b>factura a la organización</b>? (F16-RETOQUE §1).
    /// <para>
    /// <b>La decisión de producto.</b> Claude Code corre contra la <b>suscripción personal</b> de
    /// quien lo usa: nadie le pasa una factura a la organización por esos tokens. Tarifarlo exigía
    /// mantener a mano una copia de la lista de precios de Anthropic — un dato que cambia sin
    /// avisar y que, en cuanto se quedara viejo, dejaría de ser ruido para pasar a ser
    /// desinformación. Así que no se tarifa: se cuentan las llamadas y los tokens, que son hechos
    /// medidos, y el coste se dice como lo que es.
    /// </para>
    /// <para>
    /// <b>Vive aquí y no en la vista</b>, y ése es el punto: es el mismo embudo por el que pasan el
    /// pie, el informe, la lista de informes y las cuatro cifras de Métricas. Puesto en cualquier
    /// otro sitio habría que acordarse de preguntarlo N veces, y a la primera que se olvidara
    /// saldría un «tarifa no configurada» por una tarifa que no debe existir.
    /// </para>
    /// <para>
    /// Un proveedor vacío es Copilot —lo único que había antes de F14— y sí factura. Uno que esta
    /// versión no conozca se supone facturable: es la suposición conservadora, porque hace que su
    /// gasto se vea en vez de desaparecer del panel sin decir nada.
    /// </para>
    /// </summary>
    public static bool IsBilled(string? providerId)
        => !string.Equals(providerId?.Trim(), "claude-code", StringComparison.OrdinalIgnoreCase);

    public static TokenAccounting AccountingOf(string? providerId) => providerId?.ToLowerInvariant() switch
    {
        // Sin proveedor escrito es Copilot: es lo único que había antes de F14.
        null or "" or "copilot" => TokenAccounting.InputIncludesCache,
        "claude-code" => TokenAccounting.InputExcludesCache,

        // Uno que no conocemos: se asume la forma de Copilot, que es la del histórico. Y de todos
        // modos el reparto se blinda abajo, así que un supuesto equivocado no produce negativos.
        _ => TokenAccounting.InputIncludesCache,
    };

    /// <summary>
    /// El coste de un consumo de tokens con la tarifa de SU modelo.
    /// <para>
    /// <b>El modelo se lee del registro, jamás se asume.</b> Si la sesión no lo guardó, o su modelo
    /// no tiene tarifa, el resultado es «no aplicable» con su motivo — nunca la tarifa de otro
    /// modelo parecido, que es la clase de aproximación que convierte un panel en una invención.
    /// </para>
    /// </summary>
    public static CostResult Calculate(
        string? model,
        string? provider,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        ModelRateTable? rates)
    {
        // Lo PRIMERO, antes que mirar tokens o tarifas: si esta casa no factura a la organización,
        // no hay nada que tarifar y no puede haber ningún motivo de los otros tres. Preguntarlo
        // aquí —y no en cada vista— es lo que garantiza que a un proveedor no tarifado no le pueda
        // ladrar jamás un «tarifa no configurada» (F16-RETOQUE §1).
        if (!IsBilled(provider))
        {
            return CostResult.Unavailable(CostUnavailable.NotBilled, model);
        }

        if (inputTokens <= 0 && outputTokens <= 0 && cacheReadTokens <= 0 && cacheWriteTokens <= 0)
        {
            return CostResult.Unavailable(CostUnavailable.TokensMissing, model);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return CostResult.Unavailable(CostUnavailable.ModelUnknown, model);
        }

        ModelRate? rate = rates?.Find(model, provider);
        if (rate is null)
        {
            return CostResult.Unavailable(CostUnavailable.RateMissing, model);
        }

        // ¿Se cobra la escritura de caché aparte? Si sí, esos tokens salen del montón de entrada y
        // van a su propia tarifa. Si no —null—, son entrada normal y se quedan donde están.
        bool writeBilledApart = rate.CacheWritePerMillion is not null;

        long billableInput = AccountingOf(provider) == TokenAccounting.InputIncludesCache
            ? inputTokens - cacheReadTokens - (writeBilledApart ? cacheWriteTokens : 0)
            : inputTokens;

        // Blindaje: si la suma de cachés supera a la entrada, el supuesto de semántica no encaja
        // con estos datos. Antes que emitir un coste NEGATIVO —que se propagaría a los agregados
        // sin que nadie lo notara— se reparte lo que se puede y se cobra cero por lo que no.
        billableInput = Math.Max(0, billableInput);

        decimal usd =
            PerMillion(billableInput, rate.InputPerMillion)
            + PerMillion(cacheReadTokens, rate.CachedInputPerMillion)
            + PerMillion(cacheWriteTokens, rate.CacheWritePerMillion ?? 0m)
            + PerMillion(outputTokens, rate.OutputPerMillion);

        return new CostResult(
            usd / UsdPerCredit,
            CostUnavailable.None,
            billableInput,
            cacheReadTokens,
            cacheWriteTokens,
            outputTokens,
            model);
    }

    /// <summary>El coste de una sesión entera, con el modelo y el proveedor que ella misma registró.</summary>
    public static CostResult Calculate(AuditSession session, ModelRateTable? rates)
        => Calculate(
            session.Model,
            session.Provider,
            session.Usage.InputTokens,
            session.Usage.OutputTokens,
            session.Usage.CacheReadTokens,
            session.Usage.CacheWriteTokens,
            rates);

    private static decimal PerMillion(long tokens, decimal pricePerMillion)
        => tokens <= 0 ? 0m : tokens / 1_000_000m * pricePerMillion;
}
