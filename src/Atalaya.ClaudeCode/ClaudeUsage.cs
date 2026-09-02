namespace Atalaya.ClaudeCode;

/// <summary>
/// Cómo se registra lo que gasta Claude Code (F14, reducido en F16-RETOQUE §1).
/// <para>
/// <b>El hecho, primero.</b> El CLI informa tokens de verdad y también un <c>total_cost_usd</c>.
/// Ese número es real, pero es lo que habrían costado esos tokens <b>a tarifa de lista de la
/// API</b> — el propio CLI lo etiqueta <c>"costBasis": "list"</c>. Una suscripción de Claude no
/// factura por llamada: el usuario paga su cuota mensual y esa cifra no le llega en ninguna
/// factura.
/// </para>
/// <para>
/// <b>Qué se hace con él, y qué no.</b> Se guarda con su unidad puesta —que no es «dólares», es
/// «dólares de tarifa de lista»— y acaba en el informe como una línea informativa que dice
/// exactamente eso. <b>Nada más.</b> No es el coste de la sesión, no se enseña en el pie y no entra
/// en ninguna métrica: desde F16-RETOQUE el consumo de esta casa no se tarifa en absoluto, porque
/// va contra la suscripción personal de quien la usa y no le llega a la organización.
/// </para>
/// <para>
/// <b>Lo que se retiró y por qué.</b> Aquí vivían una salvedad —«tarifa de lista, no lo que factura
/// tu suscripción»— y una frase de estimación previa al lanzamiento. Las dos existían para colocar
/// un número junto a una explicación de por qué ese número no era lo que parecía. Sin número no
/// hace falta explicarlo: la regla se dice entera en una línea y no hay nada que desmentir.
/// </para>
/// </summary>
public static class ClaudeUsage
{
    /// <summary>
    /// La unidad con la que se etiqueta el coste que informa el CLI. Es la cadena que acaba escrita
    /// en <c>AuditSession.Usage.Currency</c> y la que el informe pone junto a la cifra declarada,
    /// así que dice lo que es sin que haga falta una nota al pie.
    /// </summary>
    public const string ListPriceUnit = "USD (tarifa de lista)";
}
