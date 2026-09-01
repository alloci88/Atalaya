using Atalaya.App.Services;
using Atalaya.Domain.Model;

namespace Atalaya.App.Tests;

/// <summary>
/// La tarifa de los tests, elegida para que la aritmética se lea de un vistazo (F15).
/// <para>
/// <b>Por qué existe.</b> Desde F15 el coste ya no se guarda: se DERIVA de los tokens con la
/// tarifa del modelo de cada sesión. Los tests que agregan costes —los cubos de la gráfica, el
/// tile del periodo, los colores del panel— no van de precios: van de que la suma reparta bien.
/// Obligarles a razonar con tarifas reales de seis cifras decimales sería cambiar lo que prueban
/// por ruido.
/// </para>
/// <para>
/// <b>La tarifa.</b> Solo la salida cuesta, y cuesta 10 $/millón. Con eso:
/// <c>1.000 tokens de salida = 0,01 $ = 1 credit</c>, así que <b>credits = tokens de salida / 1000</b>
/// y un test que quiera «una sesión de 12,25 credits» escribe 12.250 tokens de salida. La entrada
/// y la caché van a cero <i>a propósito</i>: así el reparto entre cacheado y no cacheado —que tiene
/// sus propios tests en <c>CreditCalculatorTests</c>— no contamina estas cuentas.
/// </para>
/// </summary>
public static class TestRates
{
    /// <summary>El modelo que usan las sesiones de prueba. Existe en la tabla de abajo y en ningún sitio más.</summary>
    public const string Model = "modelo-de-prueba";

    /// <summary>Cuántos tokens de salida hacen falta para gastar UN credit con esta tarifa.</summary>
    public const int OutputTokensPerCredit = 1_000;

    /// <summary>
    /// El modelo de las sesiones LEGADAS de los tests, que traen su propio nombre escrito en el
    /// JSON. Lleva la misma tarifa: lo que se prueba con él es que un histórico se recalcula, no
    /// cuánto vale un modelo.
    /// </summary>
    public const string LegacyModel = "gpt-5";

    /// <summary>La tabla: dos modelos con la misma tarifa, y solo la salida cuesta.</summary>
    public static ModelRateTable Table() => new()
    {
        Source = "Tarifa de test: 10 $/M de salida, todo lo demás a cero.",
        Rates =
        {
            new ModelRate(Model, 0m, 10.00m, 0m),
            new ModelRate(LegacyModel, 0m, 10.00m, 0m),
        },
    };

    /// <summary>Los tokens de salida que producen exactamente ese coste en credits.</summary>
    public static long OutputFor(decimal credits) => (long)(credits * OutputTokensPerCredit);

    /// <summary>Deja la tabla escrita en el hub, que es de donde la leen los servicios.</summary>
    public static void Seed(HubContext hub) => hub.Store.WriteModelRates(Table());

    /// <summary>
    /// Prepara una sesión para que su coste derivado sea exactamente <paramref name="credits"/>:
    /// le pone el modelo de prueba y los tokens que corresponden.
    /// </summary>
    public static void CostAs(AuditSession session, decimal? credits, long inputTokens = 0)
    {
        session.Model ??= Model;

        // credits == null significa «esta sesión no tiene coste medido», y desde F15 eso quiere
        // decir exactamente una cosa: NO GUARDÓ TOKENS. Escribirle tokens con coste cero sería otra
        // situación —costó cero— y la diferencia entre «no se sabe» y «cero» es justo la que estos
        // tests vigilan.
        if (credits is not { } c)
        {
            return;
        }

        session.Usage.Add(inputTokens, OutputFor(c), 0, 0, null);
    }
}
