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
}

/// <summary>
/// <b>En qué se reparte el coste, por concepto</b> (F20 §1). Los tokens engañan cuando las tarifas
/// difieren doce veces entre sí: con Opus, escribir en caché cuesta 6,25 $/M y leerla 0,50 $/M, así
/// que 126.904 tokens escritos pesan trece veces más que 119.583 leídos aunque el número se
/// parezca. Hasta F20 esto había que calcularlo a mano para poder decidir dónde apretar.
/// <para>
/// El orden es FIJO —escritura, salida, lectura, fresca— y no por tamaño: así dos informes se
/// comparan de un vistazo. Cuál manda se ve en el porcentaje.
/// </para>
/// </summary>
public sealed record CostSplit(decimal CacheWrite, decimal Output, decimal Cached, decimal Fresh)
{
    public decimal Total => CacheWrite + Output + Cached + Fresh;

    /// <summary>Los cuatro conceptos con su nombre, en el orden de la casa. Sin los que son cero.</summary>
    public IReadOnlyList<(string Concepto, decimal Usd)> Items
        => new[]
            {
                ("escritura de caché", CacheWrite),
                ("salida", Output),
                ("lectura de caché", Cached),
                ("entrada fresca", Fresh),
            }
            .Where(x => x.Item2 > 0m)
            .ToList();

    /// <summary>Qué fracción del total es ese concepto. 0 cuando no hay total que repartir.</summary>
    public double ShareOf(decimal usd) => Total <= 0m ? 0 : (double)(usd / Total);
}

/// <summary>
/// El coste de algo, con su procedencia. Nunca es solo un número: o hay importe, o hay un motivo.
/// </summary>
/// <param name="Usd">
/// El importe, <b>en dólares</b> (PROV-2 §3). Null cuando no se puede calcular.
/// <para>
/// Hasta PROV-2 esto eran AI credits. Dejó de valer en cuanto dos casas pueden gastar en el mismo
/// periodo: los credits son la moneda de UNA de ellas, y una suma de unidades distintas no es un
/// número. El dólar es la unidad de TODAS las tarifas publicadas, así que es la única en la que el
/// dominio puede sumar; quien tenga moneda propia convierte al enseñarla, con la equivalencia que
/// declara su <c>ProviderBilling</c> y no una constante del dominio.
/// </para>
/// </param>
/// <param name="Why">Por qué no se puede, cuando no se puede.</param>
/// <param name="BillableInputTokens">Los tokens de entrada que SÍ se facturan a tarifa plena.</param>
public sealed record CostResult(
    decimal? Usd,
    CostUnavailable Why = CostUnavailable.None,
    long BillableInputTokens = 0,
    long CachedInputTokens = 0,
    long CacheWriteTokens = 0,
    long OutputTokens = 0,
    string? Model = null,
    CostSplit? Split = null)
{
    /// <summary>Se ha podido calcular.</summary>
    public bool HasValue => Usd is not null;

    /// <summary>
    /// <b>El número es una estimación, no una medida</b> (F29 §1). Solo lo es cuando nadie supo con
    /// qué modelo corrió la sesión y una persona eligió con qué tarifa valorarla; se enseña con un
    /// asterisco y su explicación allá donde se enseñe el coste, y <b>la marca no se quita nunca</b>:
    /// asignar una tarifa no convierte en medido lo que no se midió.
    /// </summary>
    public CostReconciliation? EstimatedWith { get; init; }

    public bool IsEstimate => EstimatedWith is not null;

    /// <summary>
    /// <b>Lo que dice la casa que escribió esto cuando su modelo no lleva tarifa</b> (PROV-2 §3):
    /// «incluido en tu suscripción de Claude». Null cuando no declara ninguna, que es lo normal.
    /// <para>
    /// Viaja DENTRO del resultado, por lo mismo que viaja <see cref="Why"/>: el pie, el informe, la
    /// lista de informes y las cuatro cifras de Métricas pasan por el mismo embudo, y a la primera
    /// que se olvidara de preguntar saldría un «tarifa no configurada» donde no falta ninguna
    /// tarifa. Es lo que F16-RETOQUE §1 consiguió con un <c>if</c> que nombraba una casa, y que
    /// PROV-2 conserva sin el <c>if</c>: ahora lo declara quien responde por la frase.
    /// </para>
    /// </summary>
    public string? NoRateNote { get; init; }

    /// <summary>
    /// <b>No es un hueco: es que no lleva precio</b> (PROV-2 §3). Su casa lo declara, así que no
    /// marca el agregado como parcial, no pide reconciliación y se cuenta aparte. Una casa que no
    /// lo declara y no tiene tarifa sí es un hueco, como cualquiera — y el día que alguien escriba
    /// una tarifa para ésta, se tarifa como cualquiera.
    /// </summary>
    public bool IsUnpriced => Usd is null && NoRateNote is { Length: > 0 };

    public static CostResult Unavailable(
        CostUnavailable why, string? model = null, string? noRateNote = null)
        => new(null, why, Model: model) { NoRateNote = noRateNote };
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
/// <b>Y desde PROV-2 la unidad de aquí es el DÓLAR</b> (§3). Era el AI credit, la moneda con la
/// que GitHub factura Copilot, y funcionó mientras hubo una sola casa que gastara. Con dos, sumar
/// credits es sumar unidades distintas; el dólar es la unidad de todas las tarifas publicadas, así
/// que es la única en la que este cálculo puede devolver un total. La equivalencia «1 credit =
/// 0,01 $» no desapareció: se mudó a donde vive quien la usa, la <c>ProviderBilling</c> de Copilot,
/// y se aplica al ENSEÑAR la cifra, no al calcularla.
/// </para>
/// <para>
/// <b>Y ya no hay ninguna casa parada en la puerta.</b> Hasta PROV-2 un <c>IsBilled</c> comparaba
/// el proveedor con una cadena literal y devolvía «no facturable» antes de mirar tokens ni tarifas.
/// Lo que decide ahora si un consumo se tarifa es lo único que de verdad lo decide: <b>que exista
/// tarifa para su proveedor y su modelo</b>. Una casa que corre contra la suscripción de quien la
/// usa no lleva tarifa sembrada, así que sigue sin número — pero por no tener precio, no por
/// llamarse como se llama, y el día que alguien le escriba una tarifa se tarifa como cualquiera.
/// Lo que esa casa tenga que decir en su lugar lo declara ella, en
/// <see cref="ProviderCostTraits.NoRateNote"/>, y viaja dentro del <see cref="CostResult"/>.
/// </para>
/// </summary>
public static class CostCalculator
{
    /// <summary>
    /// El coste de un consumo de tokens con la tarifa de SU modelo.
    /// <para>
    /// <b>El modelo se lee del registro, jamás se asume.</b> Si la sesión no lo guardó, o su modelo
    /// no tiene tarifa, el resultado es «no aplicable» con su motivo — nunca la tarifa de otro
    /// modelo parecido, que es la clase de aproximación que convierte un panel en una invención.
    /// </para>
    /// </summary>
    /// <param name="traits">
    /// Lo que la casa que escribió esto declara: con qué identificador se busca su tarifa, cómo
    /// cuenta sus tokens de entrada y qué se lee cuando no lleva precio (PROV-2 §3). Null es el
    /// histórico: el proveedor tal cual venga, la entrada con la caché dentro y ninguna frase.
    /// </param>
    public static CostResult Calculate(
        string? model,
        string? provider,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        ModelRateTable? rates,
        ProviderCostTraits? traits = null)
    {
        ProviderCostTraits casa = traits ?? ProviderCostTraits.Historical(provider);
        string? note = casa.NoRateNote;

        // El identificador con el que se busca la tarifa sale de la casa y no del campo escrito:
        // una sesión anterior a F14 no guardó ninguno, y la tabla sí nombra a la que la escribió.
        string? rateProvider = casa.ProviderId ?? provider;

        if (inputTokens <= 0 && outputTokens <= 0 && cacheReadTokens <= 0 && cacheWriteTokens <= 0)
        {
            return CostResult.Unavailable(CostUnavailable.TokensMissing, model, note);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return CostResult.Unavailable(CostUnavailable.ModelUnknown, model, note);
        }

        ModelRate? rate = rates?.Find(model, rateProvider);
        if (rate is null)
        {
            return CostResult.Unavailable(CostUnavailable.RateMissing, model, note);
        }

        // ¿Se cobra la escritura de caché aparte? Si sí, esos tokens salen del montón de entrada y
        // van a su propia tarifa. Si no —null—, son entrada normal y se quedan donde están.
        bool writeBilledApart = rate.CacheWritePerMillion is not null;

        long billableInput = casa.Accounting == TokenAccounting.InputIncludesCache
            ? inputTokens - cacheReadTokens - (writeBilledApart ? cacheWriteTokens : 0)
            : inputTokens;

        // Blindaje: si la suma de cachés supera a la entrada, el supuesto de semántica no encaja
        // con estos datos. Antes que emitir un coste NEGATIVO —que se propagaría a los agregados
        // sin que nadie lo notara— se reparte lo que se puede y se cobra cero por lo que no.
        billableInput = Math.Max(0, billableInput);

        // F20 §1 — cada concepto se valora por separado y el total es su suma. UNA sola
        // aritmética: el reparto no es una segunda cuenta que pueda discrepar del total, es el
        // total desglosado. Sumarlos tiene que dar exactamente lo que devuelve esta función.
        decimal fresh = PerMillion(billableInput, rate.InputPerMillion);
        decimal cached = PerMillion(cacheReadTokens, rate.CachedInputPerMillion);
        decimal written = PerMillion(cacheWriteTokens, rate.CacheWritePerMillion ?? 0m);
        decimal output = PerMillion(outputTokens, rate.OutputPerMillion);

        decimal usd = fresh + cached + written + output;

        return new CostResult(
            usd,
            CostUnavailable.None,
            billableInput,
            cacheReadTokens,
            cacheWriteTokens,
            outputTokens,
            model,
            new CostSplit(written, output, cached, fresh));
    }

    /// <summary>
    /// El coste de una sesión entera, con el modelo y el proveedor que ella misma registró — y, si
    /// su hueco se reconcilió alguna vez, por el camino que aquella reconciliación dejó escrito
    /// (F29 §1).
    /// <para>
    /// <b>El orden importa y es éste</b>: primero la fórmula de siempre. Una sesión cuyo modelo
    /// tiene tarifa se valora con ella y la reconciliación no pinta nada — ni siquiera si alguien
    /// le asignó otra en su día. La reconciliación solo contesta donde la fórmula dice «no puedo»,
    /// que es justamente para lo que existe.
    /// </para>
    /// </summary>
    public static CostResult Calculate(
        AuditSession session,
        ModelRateTable? rates,
        CostReconciliation? reconciled = null,
        ProviderCostTraits? traits = null)
    {
        CostResult direct = Calculate(
            session.Model,
            session.Provider,
            session.Usage.InputTokens,
            session.Usage.OutputTokens,
            session.Usage.CacheReadTokens,
            session.Usage.CacheWriteTokens,
            rates,
            traits);

        if (direct.HasValue
            || reconciled is null
            || direct.Why is not (CostUnavailable.RateMissing or CostUnavailable.ModelUnknown))
        {
            return direct;
        }

        return reconciled.How switch
        {
            CostResolution.PorLlamada => ByCall(session, rates, direct, traits),
            CostResolution.TarifaAsignada => Assigned(session, rates, reconciled, direct, traits),

            // La tarifa se añadió a la tabla y luego alguien la quitó: se vuelve a lo que hay, que
            // es «tarifa no configurada». Una reconciliación no puede fabricar un precio.
            _ => direct,
        };
    }

    /// <summary>
    /// <b>El coste llamada a llamada</b> (F29 §0). Copilot enruta por llamada cuando se le deja
    /// elegir, y cada respuesta trae el modelo que de verdad contestó: sumando el coste de cada una
    /// con SU tarifa sale un coste medido, sin aproximar nada. Es la misma fórmula de arriba
    /// aplicada N veces, no una segunda aritmética.
    /// <para>
    /// Si una sola llamada no se puede valorar, no hay coste: media sesión valorada se leería como
    /// la sesión entera, que es la mentira que D-787 fue a impedir.
    /// </para>
    /// </summary>
    private static CostResult ByCall(
        AuditSession session, ModelRateTable? rates, CostResult direct, ProviderCostTraits? traits)
    {
        IReadOnlyList<CallSample> calls = CostReconciler.CallsOf(session);
        if (!CostReconciler.CanCostByCall(session, rates, traits))
        {
            return direct;
        }

        decimal usd = 0m;
        decimal write = 0m, output = 0m, cached = 0m, fresh = 0m;
        long billable = 0;
        foreach (CallSample call in calls)
        {
            CostResult one = Calculate(
                call.Model, session.Provider,
                call.InputTokens, call.OutputTokens, call.CacheReadTokens, call.CacheWriteTokens,
                rates, traits);

            // Una llamada sin tokens no cuesta: se salta, no invalida la sesión.
            if (!one.HasValue)
            {
                continue;
            }

            usd += one.Usd ?? 0m;
            billable += one.BillableInputTokens;
            if (one.Split is { } s)
            {
                write += s.CacheWrite;
                output += s.Output;
                cached += s.Cached;
                fresh += s.Fresh;
            }
        }

        return new CostResult(
            usd,
            CostUnavailable.None,
            billable,
            session.Usage.CacheReadTokens,
            session.Usage.CacheWriteTokens,
            session.Usage.OutputTokens,
            session.Model,
            new CostSplit(write, output, cached, fresh));
    }

    /// <summary>
    /// <b>El coste con la tarifa que alguien eligió</b> (F29 §1): la fórmula de siempre sobre los
    /// tokens de la sesión, con el modelo asignado. Sale marcado como estimación y así viaja.
    /// </summary>
    private static CostResult Assigned(
        AuditSession session,
        ModelRateTable? rates,
        CostReconciliation reconciled,
        CostResult direct,
        ProviderCostTraits? traits)
    {
        CostResult estimated = Calculate(
            reconciled.AssignedModel,
            session.Provider,
            session.Usage.InputTokens,
            session.Usage.OutputTokens,
            session.Usage.CacheReadTokens,
            session.Usage.CacheWriteTokens,
            rates,
            traits);

        return estimated.HasValue
            ? estimated with { Model = session.Model, EstimatedWith = reconciled }
            : direct;
    }

    private static decimal PerMillion(long tokens, decimal pricePerMillion)
        => tokens <= 0 ? 0m : tokens / 1_000_000m * pricePerMillion;
}
