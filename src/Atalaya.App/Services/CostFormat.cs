using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>En qué unidad se ENSEÑA un coste (F29 §2). Es presentación: el hub no la conoce.</summary>
public enum CostCurrency
{
    /// <summary>AI credits, la unidad en la que factura GitHub. Un decimal.</summary>
    Credits,

    /// <summary>Dólares, a 0,01 $ por credit. Dos decimales, con el símbolo detrás.</summary>
    Usd,
}

/// <summary>
/// El ÚNICO sitio donde un coste se convierte en texto (F15; se llamaba <c>CreditText</c> hasta
/// F29, cuando los credits dejaron de ser la única unidad en la que se enseña).
/// <para>
/// Mismo papel que <see cref="PercentText"/> y por el mismo motivo: un número que se formatea en
/// cinco sitios acaba diciendo cinco cosas distintas, y el redondeo es donde nacen las mentiras
/// pequeñas. La regla que trajo BUGFIX-REDONDEO se aplica igual aquí: <b>el redondeo nunca puede
/// escribir un cero cuando hubo gasto</b>. «0,0 credits» tras auditar una unidad es la misma falta
/// que el «0 %» de cobertura con trabajo hecho — dice que no costó nada, y costó.
/// </para>
/// <para>
/// <b>Y desde F29 es también el único sitio que sabe en qué DIVISA se enseña</b> (§2). La unidad
/// del hub sigue siendo el credit —los tokens y los credits se guardan como siempre—; los dólares
/// son una lente de lectura de esta máquina. Por eso la divisa vive aquí y no en cada vista: si el
/// pie, el azulejo y la lista de informes tuvieran que acordarse de convertir, a la primera que se
/// olvidara habría dos monedas en la misma pantalla.
/// </para>
/// </summary>
public static class CostFormat
{
    /// <summary>
    /// <b>La divisa activa de esta máquina</b> (F29 §2). La pone el arranque leyendo los ajustes y
    /// la cambia la fila de Ajustes → Tarifas. Es estática por lo mismo que <see cref="AppCulture"/>
    /// y que el tema: la lee todo lo que escribe un coste, y pasarla de mano en mano por doce
    /// firmas acabaría con una que no la recibe.
    /// <para>
    /// <b>Los informes NO la miran</b>: escriben las dos cifras siempre (<see cref="Both"/>). Un
    /// informe se lee dentro de años y no puede depender de una preferencia de una máquina.
    /// </para>
    /// </summary>
    public static CostCurrency Currency { get; set; } = CostCurrency.Credits;

    /// <summary>
    /// Cómo se llama la unidad activa. En un sitio, para que no se escriba de dos maneras — ni en
    /// C# ni en XAML, donde hay un test que lo recorre.
    /// </summary>
    public static string Unit => Currency == CostCurrency.Usd ? UsdSymbol : "credits";

    /// <summary>El símbolo del dólar, detrás de la cifra, como ya lo escribía Métricas.</summary>
    public const string UsdSymbol = "$";

    /// <summary>
    /// <b>La unidad de la TABLA DE TARIFAS</b>, que no es la divisa de presentación: los precios
    /// publicados de GitHub están en dólares por millón de tokens y ahí seguirán aunque el coste se
    /// enseñe en credits. Vive aquí para que el XAML no escriba un «$» a mano.
    /// </summary>
    public const string RateColumnUnit = "$ por millón";

    /// <summary>Lo mínimo que se puede escribir en credits, con su decimal.</summary>
    private const decimal SmallestShown = 0.1m;

    /// <summary>Y lo mínimo en dólares, con los suyos. Un céntimo.</summary>
    private const decimal SmallestUsd = 0.01m;

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
    /// La unidad LARGA de la divisa activa: «AI credits», que es como factura GitHub (F15, D-789
    /// revisado), o el símbolo del dólar. Es la que acompaña a una cifra suelta en una tarjeta.
    /// </summary>
    public static string BillingUnit => Currency == CostCurrency.Usd ? UsdSymbol : "AI credits";

    /// <summary>
    /// El importe sin unidad, en la divisa activa («68,2» en credits, «0,68» en dólares).
    /// <para>
    /// El argumento son SIEMPRE credits: es lo que el hub guarda y lo único que
    /// <see cref="CreditCalculator"/> produce. La conversión ocurre aquí y solo aquí, con
    /// <see cref="CreditCalculator.UsdPerCredit"/>, que es donde D-786 dejó escrito lo que vale un
    /// credit — el día que cambie, cambia ahí.
    /// </para>
    /// </summary>
    public static string Number(decimal? credits)
        => Currency == CostCurrency.Usd ? UsdNumber(credits) : CreditNumber(credits);

    /// <summary>
    /// Los credits, sin unidad («68,2»). Un decimal, que es la precisión con la que el panel de
    /// GitHub enseña sus cifras y suficiente para decidir.
    /// </summary>
    public static string CreditNumber(decimal? credits)
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

    /// <summary>
    /// Los dólares, sin símbolo («1,85»). Dos decimales — y la misma regla del redondeo: si hubo
    /// gasto y no llega al céntimo se dice «&lt; 0,01», nunca un «0,00» que afirmaría que fue gratis.
    /// </summary>
    public static string UsdNumber(decimal? credits)
    {
        if (credits is not { } value)
        {
            return Unknown;
        }

        decimal usd = value * CreditCalculator.UsdPerCredit;
        if (usd <= 0m)
        {
            return 0m.ToString("0.00", AppCulture.Display);
        }

        if (usd < SmallestUsd)
        {
            return "< " + SmallestUsd.ToString("0.00", AppCulture.Display);
        }

        return Math.Round(usd, 2, MidpointRounding.AwayFromZero).ToString("0.00", AppCulture.Display);
    }

    /// <summary>
    /// <b>Una MARCA de un eje, en la divisa activa</b> (R6 §8). Es la misma conversión que
    /// <see cref="Number"/> y por el mismo camino —de aquí no sale ninguna cifra que no haya pasado
    /// por esta clase—, pero sin la regla del «no escribas un cero donde hubo gasto»: una marca de
    /// una rejilla no es un gasto, es dónde cae el 0, el 1,25 y el 2,50 de la escala, y un «&lt; 0,01»
    /// colgado del eje no dice nada de nada.
    /// </summary>
    public static string Tick(decimal credits)
        => Currency == CostCurrency.Usd
            ? (credits * CreditCalculator.UsdPerCredit).ToString("0.##", AppCulture.Display)
            : credits.ToString("0.#", AppCulture.Display);

    /// <summary>El importe con su unidad, en la divisa activa: «68,2 credits» o «0,68 $».</summary>
    public static string Of(decimal? credits)
        => credits is null ? Unknown : $"{Number(credits)} {Unit}";

    /// <summary>
    /// <b>Las dos cifras, para lo que se guarda</b> (F29 §2): «185,3 AI credits (1,85 $)».
    /// <para>
    /// La escriben los informes, y no miran la divisa activa: un informe se lee dentro de años, en
    /// otra máquina, y no puede depender de una preferencia de ésta. Registrar las dos cuesta seis
    /// caracteres y ahorra tener que saber a cuánto estaba el credit aquel día.
    /// </para>
    /// </summary>
    public static string Both(decimal? credits)
        => credits is null
            ? Unknown
            : $"{CreditNumber(credits)} AI credits ({UsdNumber(credits)} {UsdSymbol})";

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
    /// <b>El coste de una sesión tal y como lo registra un INFORME</b> (F29 §2): con las dos
    /// cifras, y sin mirar la divisa de esta máquina.
    /// <para>
    /// Un informe se lee dentro de años, en otro puesto y con otra preferencia puesta; si dijera
    /// solo lo que quien lo generó tenía elegido, haría falta saber a cuánto estaba el credit aquel
    /// día para poder leerlo. Y cuando el coste es una valoración y no una medida, lo dice
    /// (F29 §1): eso también tiene que quedar escrito.
    /// </para>
    /// </summary>
    public static string OfSessionForReport(CostResult cost, string? providerId)
        => cost.Why switch
        {
            CostUnavailable.NotBilled => SubscriptionCost,
            CostUnavailable.None => Both(cost.Credits)
                + (cost.EstimatedWith is { } r
                    ? $" — coste estimado con tarifa de {r.AssignedModel}, asignada por {r.By} "
                      + $"el {r.On.ToString("dd/MM/yyyy", AppCulture.Display)}"
                    : string.Empty),
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
    /// <param name="turns">
    /// Turnos de conversación de la sesión. Con ellos el desglose de caché se dice <b>por turno</b>,
    /// que es la unidad en la que ahora se paga: una pasada ya no es una petición con su prefijo
    /// dentro, es un turno de una conversación que el proveedor ya tiene cacheada, y lo que se
    /// escribe por turno es exactamente donde se ve si eso está funcionando. Cero lo omite —una
    /// sesión sin turnos no tiene por qué dividir nada.
    /// </param>
    public static string Tokens(
        long input, long output, long cacheRead, long cacheWrite, int turns = 0)
    {
        if (input <= 0 && output <= 0 && cacheRead <= 0 && cacheWrite <= 0)
        {
            return string.Empty;
        }

        string head = $"{N(input)} entrada · {N(output)} salida";
        if (cacheRead <= 0 && cacheWrite <= 0)
        {
            return head;
        }

        string cache = $"{head} · caché {N(cacheRead)} leída / {N(cacheWrite)} escrita";
        return turns > 0
            ? $"{cache} · {N(cacheWrite / turns)} escrita/turno"
            : cache;
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
    /// <b>En qué se reparte el coste, por concepto</b> (F20 §1): «escritura de caché 79,2 (60 %) ·
    /// salida 45,3 (35 %) · lectura de caché 6,0 (5 %)».
    /// <para>
    /// Los tokens no bastan para decidir dónde apretar cuando las tarifas difieren doce veces entre
    /// sí. Un desglose en tokens dice que se leyeron 119.583 y se escribieron 126.904 —números
    /// parecidos—; en credits dice que lo escrito cuesta trece veces lo leído, que es la frase con
    /// la que se decide algo.
    /// </para>
    /// <para>
    /// Vacía cuando no hay coste que repartir: sin factura no hay conceptos, y una fila de ceros
    /// sugeriría que se midió y salió cero.
    /// </para>
    /// </summary>
    public static string CostSplitLine(CostResult cost)
    {
        if (cost.Split is not { } split || split.Total <= 0m)
        {
            return string.Empty;
        }

        return string.Join(" · ", split.Items.Select(
            i => $"{i.Concepto} {Number(i.Credits)} ({PercentText.Of(split.ShareOf(i.Credits))})"));
    }

    /// <summary>El concepto que más pesa, para un pie que no tiene sitio para los cuatro.</summary>
    public static string CostSplitShort(CostResult cost)
    {
        if (cost.Split is not { } split || split.Total <= 0m || split.Items.Count == 0)
        {
            return string.Empty;
        }

        (string concepto, decimal credits) = split.Items.OrderByDescending(i => i.Credits).First();
        return $"{concepto} {PercentText.Of(split.ShareOf(credits))}";
    }

    /// <summary>
    /// <b>El resumen de adónde van los tokens, en una frase</b> (F18 §1): «código 2 % · 11
    /// llamadas/unidad». Es lo mismo que dice la línea de composición del informe, recortado a lo
    /// que cabe en un pie — y va en vivo porque es mientras la sesión corre cuando se nota que algo
    /// se ha disparado; en el informe se lee cuando ya está pagado.
    /// <para>
    /// Vacía cuando no hay composición con la que decirlo. Un «0 %» afirmaría que no viajó código.
    /// </para>
    /// </summary>
    public static string BudgetShort(PromptBudget? budget)
    {
        if (budget is not { HasComposition: true, Calls: > 0 })
        {
            return string.Empty;
        }

        string share = $"código {(budget.CodeShare * 100).ToString("0.#", AppCulture.Display)} %";
        return budget.Units > 0
            ? $"{share} · {budget.CallsPerUnit.ToString("0.#", AppCulture.Display)} llamadas/unidad"
            : share;
    }

    /// <summary>La forma mínima: solo la fracción de código, que es la cifra que decide.</summary>
    private static string BudgetTiny(PromptBudget? budget)
        => budget is { HasComposition: true, Calls: > 0 }
            ? $"código {(budget.CodeShare * 100).ToString("0.#", AppCulture.Display)} %"
            : string.Empty;

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
        CostResult cost, string? providerId, PromptBudget? budget = null, int turns = 0)
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
        // F23 §6 — el pie en vivo se rige por el mismo criterio que el cuerpo del informe: unidad,
        // tiempo y coste. Los tokens, el reparto y la composición son diagnóstico —lo que el
        // informe manda al anexo— y viven en el TOOLTIP: siguen a un gesto de distancia y dejan de
        // competir por una línea que se lee de reojo mientras la auditoría corre.
        string tokens = Tokens(input, output, cacheRead, cacheWrite, turns);
        if (tokens.Length > 0)
        {
            segments.Add(FooterSegment.Hidden(tokens));
        }

        // F20 §1 — el reparto del coste, detrás del coste y delante de los tokens en importancia:
        // es lo que dice DÓNDE apretar. Cede antes que el coste y después que los tokens, y su
        // forma mínima es el concepto que manda con su porcentaje.
        string reparto = CostSplitLine(cost);
        if (reparto.Length > 0)
        {
            segments.Add(FooterSegment.Hidden(reparto));
        }

        // F18 — la composición va la ÚLTIMA en el orden y la primera en ceder: es una lectura de los
        // tokens, no un hecho nuevo, y el informe la lleva entera. Cuando el sitio escasea se queda
        // en la fracción de código, que es la cifra que decide.
        string composicion = BudgetShort(budget);
        if (composicion.Length > 0)
        {
            segments.Add(FooterSegment.Hidden(composicion));
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
    /// El número con la unidad de la casa que lo factura: «68,2 AI credits» —o «0,68 $» con la
    /// divisa puesta en dólares (F29 §2)—. Ya no hay una segunda forma —el «equivalente API» de
    /// D-789— porque ya no hay un segundo coste: lo que no factura no se tarifa y no llega hasta
    /// aquí (F16-RETOQUE §1).
    /// </summary>
    public static string WithUnit(decimal? credits, string? providerId)
        => $"{Number(credits)} {BillingUnit}";

    /// <summary>
    /// <b>El asterisco de un coste estimado</b> (F29 §1). Va pegado a la cifra allá donde se
    /// enseñe, y lleva siempre <see cref="EstimateTooltip"/> detrás: un coste estimado nunca se
    /// confunde con uno medido, y la marca no se quita al reconciliar — se queda para siempre.
    /// </summary>
    public const string EstimateMark = "*";

    /// <summary>Con qué tarifa se estimó, quién la asignó y cuándo. La frase entera, en un sitio.</summary>
    public static string EstimateTooltip(CostReconciliation? reconciled)
        => reconciled is null
            ? string.Empty
            : $"Coste estimado con tarifa de {reconciled.AssignedModel}, asignada por "
              + $"{reconciled.By} el {reconciled.On.ToString("dd/MM/yyyy", AppCulture.Display)}. "
              + "La sesión no registró con qué modelo corrió, así que este número es una "
              + "valoración, no una medida.";

    /// <summary>La cifra con su asterisco cuando el coste es estimado, y tal cual cuando no.</summary>
    public static string Marked(string amount, bool estimated)
        => estimated ? amount + EstimateMark : amount;

    /// <inheritdoc cref="Marked(string, bool)"/>
    public static string Marked(string amount, CostResult cost) => Marked(amount, cost.IsEstimate);

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
        => credits is null ? Unknown : UsdNumber(credits) + " " + UsdSymbol;

    /// <summary>
    /// La MISMA cifra en la otra divisa, para el tooltip: quien mira dólares quiere ver los credits
    /// que factura GitHub, y quien mira credits, lo que cuestan. Es lo que hace que cambiar la
    /// preferencia no esconda nunca la otra mitad.
    /// </summary>
    public static string Equivalent(decimal? credits)
        => credits is null
            ? Unknown
            : Currency == CostCurrency.Usd
                ? $"{CreditNumber(credits)} AI credits"
                : Dollars(credits);

    /// <summary>
    /// Qué hay que saber del número que se enseña. Con una sola naturaleza de coste —la factura de
    /// la organización— la salvedad es una sola: qué es un credit, y a cuánto está.
    /// </summary>
    public static string Caveat =>
        Currency == CostCurrency.Usd
            ? "Dólares, a 0,01 $ por AI credit: lo que GitHub factura por estos tokens."
            : "AI credits: lo que GitHub factura por estos tokens. 1 credit = 0,01 $.";
}

/// <summary>
/// <b>La divisa, entre el ajuste y el formateador</b> (F29 §2). El fichero de ajustes guarda una
/// palabra —<c>credits</c> o <c>usd</c>— y no el nombre de un miembro de un <c>enum</c>: renombrar
/// un enum no puede cambiar lo que ya está escrito en la máquina de alguien.
/// </summary>
public static class CostCurrencies
{
    public const string Credits = "credits";

    public const string Usd = "usd";

    /// <summary>Lo que diga el ajuste; cualquier otra cosa es credits, que es lo de fábrica.</summary>
    public static CostCurrency Parse(string? saved)
        => string.Equals(saved?.Trim(), Usd, StringComparison.OrdinalIgnoreCase)
            ? CostCurrency.Usd
            : CostCurrency.Credits;

    /// <summary>Y de vuelta, para guardarla.</summary>
    public static string Save(CostCurrency currency)
        => currency == CostCurrency.Usd ? Usd : Credits;

    /// <summary>
    /// Cómo se llama cada opción en el desplegable. «AI credits» y «dólares (USD)» son las palabras
    /// del encargo, y dicen las dos cosas que hacen falta para elegir: cuál es la unidad de la
    /// factura y cuál la del presupuesto.
    /// </summary>
    public static string Label(CostCurrency currency)
        => currency == CostCurrency.Usd ? "Dólares (USD)" : "AI credits";
}
