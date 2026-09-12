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
/// <b>La tarifa.</b> Solo la salida cuesta, y cuesta 1.000 $/millón. Con eso:
/// <c>1.000 tokens de salida = 1,00 $</c>, así que <b>el importe = tokens de salida / 1000</b> y un
/// test que quiera «una sesión de 12,25 $» escribe 12.250 tokens de salida. La entrada y la caché
/// van a cero <i>a propósito</i>: así el reparto entre cacheado y no cacheado —que tiene sus
/// propios tests en <c>CostCalculatorTests</c>— no contamina estas cuentas.
/// </para>
/// <para>
/// <b>La cifra redonda es el DÓLAR desde PROV-2 §3</b>, que es la unidad del dominio; antes era el
/// credit, con la misma aritmética y otra escala. Se cambió el precio por millón —de 10 a 1.000—
/// en vez de dividir entre cien todas las esperas de todos los tests: lo que estos tests miden es
/// que la suma reparta bien, no cuánto vale un modelo, y una tabla de esperas reescrita a mano es
/// una tabla donde se cuela un error.
/// </para>
/// <para>
/// <b>Y su proveedor va en blanco, a propósito.</b> Es la tarifa GENÉRICA —la que casa con
/// cualquier casa y pierde frente a una específica (D-786)—, que es exactamente lo que hay escrito
/// en los hubs que ya existen y lo que el cálculo tiene que seguir sabiendo leer. Las sesiones de
/// estos tests no escriben proveedor, igual que las anteriores a F14.
/// </para>
/// </summary>
public static class TestRates
{
    /// <summary>El modelo que usan las sesiones de prueba. Existe en la tabla de abajo y en ningún sitio más.</summary>
    public const string Model = "modelo-de-prueba";

    /// <summary>Cuántos tokens de salida hacen falta para gastar UN dólar con esta tarifa.</summary>
    public const int OutputTokensPerUsd = 1_000;

    /// <summary>
    /// El modelo de las sesiones LEGADAS de los tests, que traen su propio nombre escrito en el
    /// JSON. Lleva la misma tarifa: lo que se prueba con él es que un histórico se recalcula, no
    /// cuánto vale un modelo.
    /// </summary>
    public const string LegacyModel = "gpt-5";

    /// <summary>La tabla: dos modelos con la misma tarifa, y solo la salida cuesta.</summary>
    public static ModelRateTable Table() => new()
    {
        Source = "Tarifa de test: 1.000 $/M de salida, todo lo demás a cero.",
        Rates =
        {
            new ModelRate(Model, string.Empty, 0m, 1000.00m, 0m),
            new ModelRate(LegacyModel, string.Empty, 0m, 1000.00m, 0m),
        },
    };

    /// <summary>Los tokens de salida que producen exactamente ese importe, en dólares.</summary>
    public static long OutputFor(decimal usd) => (long)(usd * OutputTokensPerUsd);

    /// <summary>
    /// <b>Y los que se ENSEÑAN como ese número de AI credits</b>, para los tests que comprueban
    /// un texto: el importe es dólares, pero lo que se lee en la pantalla de quien tiene Copilot
    /// son sus credits a 0,01 $. Existe para que esos tests conserven su cifra esperada palabra
    /// por palabra a través de PROV-2 §3 — si una sola se moviera, sería que algo cambió de veras.
    /// </summary>
    public static long OutputForCredits(decimal credits) => OutputFor(credits * UsdPerCredit);

    /// <inheritdoc cref="OutputForCredits"/>
    public static void CostAsCredits(AuditSession session, decimal? credits, long inputTokens = 0)
        => CostAs(session, credits * UsdPerCredit, inputTokens);

    /// <summary>Lo que vale un AI credit, tal y como lo declara la casa que los usa.</summary>
    public static decimal UsdPerCredit => TestProviders.Copilot.Billing.UsdPerUnit;

    /// <summary>
    /// La misma tabla, pero con el proveedor de fábrica escrito en cada fila: es como queda un hub
    /// después de la migración de PROV-2 §3, y es lo que hace que una tarifa de una casa no le
    /// ponga precio al consumo de otra.
    /// </summary>
    public static ModelRateTable OfFactory()
    {
        ModelRateTable table = Table();
        table.AdoptProvider(Atalaya.Copilot.RealCopilotAgent.Id);
        return table;
    }

    /// <summary>Deja la tabla escrita en el hub, que es de donde la leen los servicios.</summary>
    public static void Seed(HubContext hub) => hub.Store.WriteModelRates(Table());

    /// <summary>
    /// Prepara una sesión para que su coste derivado sea exactamente <paramref name="usd"/>:
    /// le pone el modelo de prueba y los tokens que corresponden.
    /// </summary>
    public static void CostAs(AuditSession session, decimal? usd, long inputTokens = 0)
    {
        session.Model ??= Model;

        // usd == null significa «esta sesión no tiene coste medido», y desde F15 eso quiere decir
        // exactamente una cosa: NO GUARDÓ TOKENS. Escribirle tokens con coste cero sería otra
        // situación —costó cero— y la diferencia entre «no se sabe» y «cero» es justo la que estos
        // tests vigilan.
        if (usd is not { } c)
        {
            return;
        }

        session.Usage.Add(inputTokens, OutputFor(c), 0, 0, null);
    }
}
