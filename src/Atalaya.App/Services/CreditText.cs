using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// El ÚNICO sitio donde un coste se convierte en texto (F15).
/// <para>
/// Mismo papel que <see cref="PercentText"/> y por el mismo motivo: un número que se formatea en
/// cinco sitios acaba diciendo cinco cosas distintas, y el redondeo es donde nacen las mentiras
/// pequeñas. La regla que trajo BUGFIX-REDONDEO se aplica igual aquí: <b>el redondeo nunca puede
/// escribir un cero cuando hubo gasto</b>. «0,0 credits» tras auditar una unidad es la misma falta
/// que el «0 %» de cobertura con trabajo hecho — dice que no costó nada, y costó.
/// </para>
/// </summary>
public static class CreditText
{
    /// <summary>Cómo se llama la unidad. En un sitio, para que no se escriba de dos maneras.</summary>
    public const string Unit = "credits";

    /// <summary>Lo mínimo que se puede escribir con un decimal.</summary>
    private const decimal SmallestShown = 0.1m;

    /// <summary>Lo que se lee donde no hay nada que decir.</summary>
    public const string Unknown = "—";

    /// <summary>
    /// Cómo se dice el coste de una casa que <b>no factura a la organización</b> (F16-RETOQUE §1).
    /// <para>
    /// No es un hueco ni un «no se sabe»: es la respuesta completa. Claude Code corre contra la
    /// suscripción personal de quien lo usa, así que la pregunta «¿cuánto ha costado esto?» tiene
    /// contestación exacta y no hace falta ninguna tabla de precios para darla. Vive en una
    /// constante porque la dicen el pie, el informe, la lista de informes y el diálogo de
    /// lanzamiento, y una frase escrita cuatro veces acaba diciendo cuatro cosas.
    /// </para>
    /// </summary>
    public const string SubscriptionCost = "incluido en tu suscripción de Claude";

    /// <summary>
    /// Lo mismo, en una palabra, para una celda de tabla. <b>Va siempre con la frase larga en el
    /// tooltip</b>: no es una segunda versión de la verdad, es la misma abreviada donde no cabe.
    /// </summary>
    public const string SubscriptionCostShort = "suscripción";

    /// <summary>
    /// La unidad en la que factura GitHub. Es la de TODO lo que esta clase valora, porque desde
    /// F16-RETOQUE lo único que se tarifa es lo que factura (F15, D-789 revisado).
    /// </summary>
    public const string BillingUnit = "AI credits";

    /// <summary>
    /// Los credits, sin unidad («68,2»). Un decimal, que es la precisión con la que el panel de
    /// GitHub enseña sus cifras y suficiente para decidir.
    /// </summary>
    public static string Number(decimal? credits)
    {
        if (credits is not { } value)
        {
            return Unknown;
        }

        if (value <= 0m)
        {
            return 0m.ToString("0.0", AppCulture.Display);
        }

        // Hubo gasto, pero tan poco que un decimal lo redondearía a cero. Se dice el límite en vez
        // de escribir un 0,0 que borraría el consumo.
        if (value < SmallestShown)
        {
            return "< " + SmallestShown.ToString("0.0", AppCulture.Display);
        }

        decimal rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded <= 0m
            ? "< " + SmallestShown.ToString("0.0", AppCulture.Display)
            : rounded.ToString("0.0", AppCulture.Display);
    }

    /// <summary>Los credits con su unidad: «68,2 credits».</summary>
    public static string Of(decimal? credits)
        => credits is null ? Unknown : $"{Number(credits)} {Unit}";

    /// <summary>
    /// <b>El coste de una sesión, con UN solo criterio y consciente de la casa</b> (F16 §B,
    /// revisado en F16-RETOQUE §1).
    /// <para>
    /// Existía el mismo número contado de dos maneras distintas en la misma sesión: el pie decía
    /// «coste no informado por el SDK» —una frase acuñada para Copilot, que además nombra un SDK
    /// que en Claude Code no existe— y el informe de esa misma sesión decía «no calculable (tarifa
    /// no configurada)». Las dos hablaban del mismo hueco y ninguna era la del otro, así que quien
    /// leía las dos tenía que elegir a cuál creer.
    /// </para>
    /// <para>
    /// La verdad es una: desde F15 el coste se DERIVA de los tokens con la tarifa del modelo, así
    /// que cuando no hay número el motivo es uno de los de <see cref="CostUnavailable"/>. Y desde
    /// F16-RETOQUE hay uno que <b>no es un hueco</b>: una casa que no factura a la organización no
    /// tiene coste que calcular, y eso se dice entero —«incluido en tu suscripción de Claude»— sin
    /// el «no calculable» delante, que insinuaría que falta algo por configurar.
    /// </para>
    /// </summary>
    public static string OfSession(CostResult cost, string? providerId)
        => cost.Why switch
        {
            CostUnavailable.NotBilled => SubscriptionCost,
            CostUnavailable.None => WithUnit(cost.Credits, providerId),
            _ => $"coste no calculable ({Reason(cost.Why)})",
        };

    /// <summary>
    /// Los tokens de una sesión, por tipo: «2.786 entrada · 10.975 salida · caché 201.371 leída /
    /// 22.525 escrita».
    /// <para>
    /// <b>Existe por las sesiones que no se tarifan</b> (F16-RETOQUE §1). En Copilot el pie enseña
    /// credits y los tokens quedan en el informe; con una casa que no factura no hay número de
    /// coste que enseñar, así que lo que dice el peso de la sesión son las llamadas y estos
    /// tokens. Retirarlos sería quedarse sin ninguna magnitud, y son dato primario.
    /// </para>
    /// </summary>
    public static string Tokens(long input, long output, long cacheRead, long cacheWrite)
    {
        if (input <= 0 && output <= 0 && cacheRead <= 0 && cacheWrite <= 0)
        {
            return string.Empty;
        }

        string head = $"{N(input)} entrada · {N(output)} salida";
        return cacheRead > 0 || cacheWrite > 0
            ? $"{head} · caché {N(cacheRead)} leída / {N(cacheWrite)} escrita"
            : head;
    }

    /// <summary>
    /// Los mismos tokens en una sola cifra —«237.657 tokens»—, para una celda estrecha. El desglose
    /// completo va en su tooltip: aquí se resume, no se esconde.
    /// </summary>
    public static string TokensTotal(long input, long output, long cacheRead, long cacheWrite)
    {
        long total = Math.Max(0, input) + Math.Max(0, output)
            + Math.Max(0, cacheRead) + Math.Max(0, cacheWrite);
        return total == 0 ? string.Empty : $"{N(total)} tokens";
    }

    private static string N(long value) => value.ToString("N0", AppCulture.Display);

    /// <summary>
    /// <b>El pie de una sesión en vivo</b>: llamadas, y el coste — con los tokens en medio cuando
    /// la casa no factura (F16-RETOQUE §1).
    /// <para>
    /// Está aquí y no en cada view-model porque los dos pies —el de la auditoría y el del arreglo—
    /// tienen que decir exactamente lo mismo, y porque es el sitio donde se ve de un vistazo la
    /// regla entera: con factura, un número de credits; sin ella, las magnitudes que sí son
    /// hechos —llamadas y tokens— y la frase que dice quién paga. Ni «tarifa no configurada» ni
    /// «equivalente API» pueden salir de aquí para una casa no tarifada, porque el motivo que
    /// llega es <see cref="CostUnavailable.NotBilled"/> y no hay rama que los produzca.
    /// </para>
    /// </summary>
    public static string SessionFooter(
        int calls, long input, long output, long cacheRead, long cacheWrite,
        CostResult cost, string? providerId)
        => string.Join(" · ", UsageSegments(calls, input, output, cacheRead, cacheWrite, cost, providerId).Select(s => s.Full));

    /// <summary>
    /// Los tres trozos del consumo, con sus formas y su prioridad (F17-RETOQUE): llamadas, que no
    /// ceden nunca; coste, que se abrevia; y tokens, que se abrevian antes y hasta desaparecer
    /// detrás de «tokens: ver informe». Es el ÚNICO sitio donde se decide qué tokens se enseñan,
    /// para que no vuelva a haber dos bloques diciendo lo mismo — el pie los repetía: el segmento
    /// de F16-RETOQUE los daba enteros y el bloque anterior, «tokens X in / Y out», seguía detrás.
    /// <para>
    /// El orden es llamadas → coste → tokens en las dos casas. Con factura, el coste son los credits
    /// y los tokens van detrás; sin ella, los tokens son el hecho primario que queda y la frase del
    /// coste dice quién paga. En ninguna de las dos los tokens aparecen dos veces.
    /// </para>
    /// </summary>
    public static IReadOnlyList<FooterSegment> UsageSegments(
        int calls, long input, long output, long cacheRead, long cacheWrite,
        CostResult cost, string? providerId)
    {
        var segments = new List<FooterSegment>
        {
            FooterSegment.Of($"{calls} llamadas"),
            new(new[] { CostLong(cost, providerId), CostShort(cost) }, Priority: 1, Bold: true),
        };

        // Los tokens, en dos formas: el desglose y el total («330.124 tokens»). El total ES la
        // forma abreviada con acceso al detalle —el tooltip lleva el desglose y el informe también—;
        // una frase como «tokens: ver informe» mide MÁS que el total con su número, así que nunca
        // sería la forma que cabe cuando el total no cabe. Agotado el total, el trozo se retira.
        string tokens = Tokens(input, output, cacheRead, cacheWrite);
        if (tokens.Length > 0)
        {
            segments.Add(new FooterSegment(
                new[] { tokens, TokensTotal(input, output, cacheRead, cacheWrite) }, Priority: 2, Opacity: 0.7));
        }

        return segments;
    }

    private static string CostLong(CostResult cost, string? providerId)
        => cost.Why == CostUnavailable.NotBilled ? $"coste: {SubscriptionCost}" : OfSession(cost, providerId);

    /// <summary>La forma corta del coste: el número con su unidad, o dos palabras cuando no hay número.</summary>
    private static string CostShort(CostResult cost)
        => cost.Why == CostUnavailable.NotBilled
            ? $"coste: {SubscriptionCostShort}"
            : cost.HasValue ? $"{Number(cost.Credits)} {Unit}" : "coste: —";

    /// <summary>
    /// El número con la unidad de la casa que lo factura: «68,2 AI credits». Ya no hay una segunda
    /// forma —el «equivalente API» de D-789— porque ya no hay un segundo coste: lo que no factura
    /// no se tarifa y no llega hasta aquí (F16-RETOQUE §1).
    /// </summary>
    public static string WithUnit(decimal? credits, string? providerId)
        => $"{Number(credits)} {BillingUnit}";

    /// <summary>
    /// El coste con su motivo cuando no lo hay. Es la forma que se enseña en las vistas: un número,
    /// o una frase que dice por qué no hay número — jamás un cero de relleno.
    /// </summary>
    public static string Of(CostResult cost) => cost.HasValue ? Of(cost.Credits) : Reason(cost.Why);

    /// <summary>Por qué no hay coste, en una línea.</summary>
    public static string Reason(CostUnavailable why) => why switch
    {
        CostUnavailable.ModelUnknown => "modelo no registrado",
        CostUnavailable.RateMissing => "tarifa no configurada",
        CostUnavailable.TokensMissing => "sin tokens registrados",
        CostUnavailable.NotBilled => SubscriptionCost,
        _ => Unknown,
    };

    /// <summary>
    /// El equivalente en dólares, para el tooltip: 1 credit = 0,01 $. La conversión a euros NO se
    /// hace — no hay tipo de cambio configurado, e inventarse uno sería fabricar una precisión que
    /// no tenemos (N-2).
    /// </summary>
    public static string Dollars(decimal? credits)
        => credits is not { } value
            ? Unknown
            : (value * CreditCalculator.UsdPerCredit).ToString("0.00", AppCulture.Display) + " $";

    /// <summary>
    /// Qué hay que saber del número que se enseña. Con una sola naturaleza de coste —la factura de
    /// la organización— la salvedad es una sola: qué es un credit.
    /// </summary>
    public static string Caveat =>
        "AI credits: lo que GitHub factura por estos tokens. 1 credit = 0,01 $.";
}
