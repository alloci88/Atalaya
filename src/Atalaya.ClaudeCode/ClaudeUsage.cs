namespace Atalaya.ClaudeCode;

/// <summary>
/// Cómo se registra lo que gasta Claude Code, y por qué NO se llama «coste» a secas (F14).
/// <para>
/// <b>El hecho, primero.</b> El CLI informa tokens de verdad y también un <c>total_cost_usd</c>.
/// Ese número es real, pero es lo que habrían costado esos tokens <b>a tarifa de lista de la
/// API</b> — el propio CLI lo etiqueta <c>"costBasis": "list"</c>. Una suscripción de Claude no
/// factura por llamada: el usuario paga su cuota mensual y esa cifra no le llega en ninguna
/// factura.
/// </para>
/// <para>
/// <b>Qué se hace con él, y qué no.</b> Se guarda, porque es un dato medido y tirarlo sería perder
/// la única forma de comparar el peso de dos auditorías. Pero se guarda <b>con su unidad puesta</b>,
/// y la unidad no es «dólares»: es «dólares de tarifa de lista». Copilot cuenta peticiones premium
/// con multiplicador. Sumar los dos números daría una cifra que no significa nada y que además
/// parecería dinero. Por eso:
/// </para>
/// <list type="bullet">
/// <item>Cada muestra viaja con <c>CostUnit</c>, y la sesión guarda la unidad con la que se midió.</item>
/// <item>Métricas enseña el coste <b>por proveedor</b>, nunca un total mezclado.</item>
/// <item>La estimación previa a un lanzamiento con Claude Code no promete dinero: dice llamadas.</item>
/// </list>
/// <para>
/// No se inventa ninguna conversión entre proveedores, ni ahora ni cuando alguien la pida: no
/// existe un tipo de cambio entre «peticiones premium» y «dólares de lista», y publicarlo sería
/// fabricar una precisión que no tenemos (N-2).
/// </para>
/// </summary>
public static class ClaudeUsage
{
    /// <summary>
    /// La unidad con la que se etiqueta el coste que informa Claude Code. Es la cadena que acaba
    /// escrita en <c>AuditSession.Usage.Currency</c> y la que se enseña en Métricas y en los
    /// informes, así que dice lo que es sin que haga falta una nota al pie.
    /// </summary>
    public const string ListPriceUnit = "USD (tarifa de lista)";

    /// <summary>
    /// La advertencia que acompaña a esa cifra donde se enseñe. Corta y sin rodeos: el número es
    /// real, lo que no es real es que sea una factura.
    /// </summary>
    public const string ListPriceCaveat =
        "Tarifa de lista de la API, no lo que factura tu suscripción: Claude Code no cobra por "
        + "llamada. Sirve para comparar el peso de dos auditorías, no para cuadrar gastos.";

    /// <summary>
    /// Lo que se dice antes de lanzar con Claude Code, donde Copilot diría un coste estimado. Sin
    /// tarifa por llamada no hay dinero que prometer, así que se promete lo que sí se sabe: el
    /// tamaño de la tanda.
    /// </summary>
    public static string LaunchEstimate(int units)
        => $"~{units} {(units == 1 ? "llamada estimada" : "llamadas estimadas")} · "
        + "coste según tu suscripción";
}
