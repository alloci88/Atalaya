using Atalaya.Agents;

namespace Atalaya.App.Services;

/// <summary>
/// <b>En qué unidad se ENSEÑA un importe</b> (PROV-2 §3; era <c>CostCurrency</c> a secas en
/// F29 §2).
/// <para>
/// El dominio cuenta en dólares y solo en dólares: es la unidad de todas las tarifas publicadas y
/// la única en la que se puede sumar el gasto de dos casas. Pero <b>Copilot sigue reportando AI
/// credits</b>, y quien cuadra la factura con el panel de GitHub quiere leer credits. Esta lente
/// es el sitio —el único— donde ese importe se pasa a la moneda de una casa.
/// </para>
/// <para>
/// <b>La equivalencia no vive aquí</b>: viene de la <c>ProviderBilling</c> de quien la usa. El día
/// que un credit deje de valer 0,01 $, cambia donde vive esa casa y no en el formateador.
/// </para>
/// <para>
/// <b>Y la preferencia de máquina está subordinada al gasto</b>. «Créditos» solo se puede enseñar
/// cuando todo el gasto que se está mirando es de UNA casa que tiene moneda propia; en cuanto hay
/// mezcla, la única cifra honesta es la suma en dólares. Quien sabe qué casas gastaron es la
/// consulta, no el formateador, así que la lente se construye donde se agrega y viaja hasta aquí.
/// </para>
/// </summary>
/// <param name="Unit">La unidad corta («credits»). Null: dólares.</param>
/// <param name="UnitLong">La unidad larga («AI credits»), para una cifra suelta en una tarjeta.</param>
/// <param name="UsdPerUnit">Lo que vale una de esas unidades, en dólares.</param>
public sealed record CostLens(string? Unit = null, string? UnitLong = null, decimal UsdPerUnit = 1m)
{
    /// <summary>Dólares: la unidad del dominio, y la de cualquier mezcla.</summary>
    public static readonly CostLens Dollars = new();

    /// <summary>¿Se enseña en la moneda de una casa, y no en dólares?</summary>
    public bool IsOwnUnit => !string.IsNullOrWhiteSpace(Unit) && UsdPerUnit > 0m;

    /// <summary>Lo que se escribe detrás de la cifra: «credits», o «$».</summary>
    public string Symbol => IsOwnUnit ? Unit! : CostFormat.UsdSymbol;

    /// <summary>La forma larga, para una cifra suelta: «AI credits», o «$».</summary>
    public string LongSymbol => IsOwnUnit ? (UnitLong ?? Unit!) : CostFormat.UsdSymbol;

    /// <summary>Ese importe en dólares, escrito en la unidad de esta lente.</summary>
    public decimal Amount(decimal usd) => IsOwnUnit ? usd / UsdPerUnit : usd;

    /// <summary>
    /// <b>La lente que toca</b>: la moneda de esa casa si la tiene y si esta máquina la prefiere;
    /// dólares en cuanto falta una de las dos cosas.
    /// </summary>
    /// <param name="billing">
    /// Cómo factura la casa cuyo gasto se está enseñando, o null cuando hay más de una — que es
    /// exactamente el caso en el que no hay moneda propia que valga.
    /// </param>
    /// <param name="preference">Lo que esta máquina tiene elegido en Ajustes → Tarifas.</param>
    public static CostLens For(ProviderBilling? billing, CostCurrency preference)
        => preference == CostCurrency.Usd || billing is not { HasOwnUnit: true }
            ? Dollars
            : new CostLens(billing.Unit, billing.UnitLong, billing.UsdPerUnit);

    /// <summary>
    /// <b>La lente de lo que se GUARDA</b> (F29 §2): la moneda de esa casa si la tiene, mire lo
    /// que mire esta máquina. La usan los informes, que se leen dentro de años en otro puesto y
    /// con otra preferencia puesta, así que no pueden depender de la de quien los generó.
    /// </summary>
    public static CostLens Recorded(ProviderBilling? billing)
        => For(billing, CostCurrency.Credits);

    /// <summary>
    /// <b>La nota de una línea que explica por qué esto va en dólares</b> (PROV-2 §3). Sale cuando
    /// la máquina pidió la moneda de una casa y el gasto del periodo no deja dársela: hay más de
    /// una casa gastando. Vacía en cualquier otro caso — un aviso que sale siempre no se lee.
    /// </summary>
    public const string MixedNote =
        "En dólares: en este periodo ha gastado más de un proveedor, y sus monedas no se suman.";
}
