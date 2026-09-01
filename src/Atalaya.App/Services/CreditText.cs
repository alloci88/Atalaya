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
    /// Cómo se etiqueta el coste de un proveedor (F15). No es cosmética: el de Copilot es una
    /// <b>factura de verdad</b> —lo que la organización paga— y el de Claude Code con suscripción
    /// es un <b>equivalente</b>, porque esa suscripción no cobra por tokens. Presentarlos con la
    /// misma palabra sería decir que uno cuesta lo que no cuesta.
    /// </summary>
    public static string LabelFor(string? providerId)
        => IsSubscription(providerId) ? "equivalente API" : "AI credits";

    /// <summary>La salvedad del equivalente, donde haga falta explicarlo.</summary>
    public static string CaveatFor(string? providerId)
        => IsSubscription(providerId)
            ? "Equivalente API: lo que habrían costado estos tokens pagando la API. Tu suscripción "
              + "no factura por tokens, así que no es un cobro — sirve para comparar el peso de dos "
              + "auditorías."
            : "AI credits: lo que GitHub factura por estos tokens. 1 credit = 0,01 $.";

    /// <summary>
    /// ¿El coste de esta casa es un equivalente y no una factura? Se pregunta por el proveedor y no
    /// por la unidad guardada, porque la unidad es una consecuencia de esto y no al revés.
    /// </summary>
    public static bool IsSubscription(string? providerId)
        => string.Equals(providerId, "claude-code", StringComparison.OrdinalIgnoreCase);
}
