namespace Atalaya.Domain.Model;

/// <summary>
/// Lo que cuesta un millón de tokens de UN modelo, en dólares (F15).
/// <para>
/// <b>Es configuración compartida y no código.</b> Las tarifas cambian, traen promocionales con
/// fecha de caducidad y aparecen modelos nuevos entre release y release. Quemarlas en el binario
/// significaría que corregir un precio exige publicar una versión, y mientras tanto toda la
/// organización mide mal. Viven en el hub, versionadas en git — <b>el commit ES la atribución</b>
/// de quién cambió qué tarifa y cuándo, así que no hace falta inventarse un campo «modificado por»
/// (misma regla que D-770).
/// </para>
/// </summary>
/// <param name="Model">
/// El identificador del modelo tal y como lo registra la sesión. Es la clave, y por eso se compara
/// sin distinguir mayúsculas: lo que el proveedor escribe en el registro es lo que hay que casar.
/// </param>
/// <param name="Provider">
/// <b>A qué proveedor aplica esta tarifa</b> (F15; obligatorio desde PROV-2 §3).
/// <para>
/// Existe porque <b>el mismo modelo puede costar distinto según quién factura</b>. El caso real:
/// Claude Code usa caché de una hora, que Anthropic cobra al doble de la entrada, mientras que la
/// tabla de GitHub publica la de cinco minutos (1,25×). Sin este campo habría que elegir una de las
/// dos y equivocarse con la otra.
/// </para>
/// <para>
/// <b>Dejó de ser opcional.</b> Mientras hubo una sola casa que facturara, «a cualquiera» y «a la
/// de siempre» eran lo mismo y el campo se podía dejar en blanco; con el coste tarifado por
/// proveedor+modelo, una tarifa sin proveedor es una que no se sabe a quién cobra. Por eso va en
/// la posición obligatoria del constructor: escribir una sin decirlo ya no compila. Lo que quede
/// escrito en blanco en un hub anterior sigue casando con todo el mundo —nadie pierde su tabla—,
/// pierde frente a una específica, y se adopta con <see cref="ModelRateTable.AdoptProvider"/>.
/// </para>
/// </param>
/// <param name="CacheWritePerMillion">
/// Lo que cuesta ESCRIBIR en caché, cuando el proveedor lo cobra aparte. <b>Null significa «no se
/// cobra por separado»</b> —los modelos de OpenAI en la tabla de GitHub—, y entonces esos tokens
/// son entrada normal. No es lo mismo que cero: cero sería «escribir en caché es gratis», que es
/// una afirmación distinta y que nadie ha hecho.
/// </param>
/// <param name="EffectiveFrom">
/// Desde cuándo rige. Se anota para poder leer una tabla dentro de un año y saber si la cifra que
/// hay escrita es la que estaba vigente cuando se auditó.
/// </param>
/// <param name="Note">
/// Lo que haga falta recordar: «promocional hasta el 31/12/2026», «contexto largo», la fuente.
/// </param>
public sealed record ModelRate(
    string Model,
    string Provider,
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CachedInputPerMillion,
    decimal? CacheWritePerMillion = null,
    DateOnly? EffectiveFrom = null,
    string? Note = null)
{
    /// <summary>¿Es esta la tarifa de ese modelo y ese proveedor?</summary>
    public bool Matches(string? model, string? provider)
        => model is { Length: > 0 }
           && string.Equals(Model, model, StringComparison.OrdinalIgnoreCase)
           && (!IsProviderSpecific
               || string.Equals(Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>Una tarifa atada a un proveedor concreto gana a la genérica.</summary>
    public bool IsProviderSpecific => !string.IsNullOrWhiteSpace(Provider);
}

/// <summary>
/// La tabla de tarifas de la organización (F15), en <c>hub/model-rates.json</c>.
/// <para>
/// Va en la RAÍZ del hub y no en cada <c>app.json</c> porque un precio no es una propiedad de la
/// aplicación auditada: es del contrato de la organización con su proveedor. Tenerla por aplicación
/// obligaría a corregir el mismo número N veces y garantizaría que alguna copia se quedara vieja.
/// </para>
/// </summary>
public sealed class ModelRateTable
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>De dónde salieron estas cifras. Se escribe para poder volver a comprobarlas.</summary>
    public string? Source { get; set; }

    /// <summary>Cuándo se sembraron o revisaron por última vez.</summary>
    public DateOnly? ReviewedOn { get; set; }

    public List<ModelRate> Rates { get; set; } = new();

    /// <summary>
    /// La tarifa de un modelo, o null si no está configurada. Prefiere la que nombra al proveedor:
    /// si alguien ha escrito una tarifa específica para esta casa, es porque la genérica no vale.
    /// </summary>
    public ModelRate? Find(string? model, string? provider)
    {
        ModelRate? generic = null;

        foreach (ModelRate rate in Rates)
        {
            if (!rate.Matches(model, provider))
            {
                continue;
            }

            if (rate.IsProviderSpecific)
            {
                return rate;
            }

            generic ??= rate;
        }

        return generic;
    }

    /// <summary>
    /// ¿Hay ALGUNA tarifa escrita que le sirva a esa casa? No es lo mismo que tener la de un
    /// modelo concreto: contesta a «¿se tarifa a esta casa, o es que nadie le ha puesto precio
    /// nunca?», que es lo que separa un hueco por configurar de una casa que declara que no lleva
    /// precio (PROV-2 §3).
    /// </summary>
    public bool HasAnyFor(string? provider)
        => Rates.Any(r => !r.IsProviderSpecific
                          || string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <b>Le pone proveedor a lo que no lo tenga</b> (PROV-2 §3). Devuelve cuántas filas ha
    /// cambiado; 0 significa que no hay nada que migrar y que no hace falta escribir el fichero.
    /// <para>
    /// Las 31 tarifas que hay escritas en los hubs de la gente se sembraron sin proveedor, cuando
    /// «a cualquiera» y «a la casa de siempre» eran lo mismo. Con la columna obligatoria hay que
    /// decir de quién son, y la respuesta no se inventa: <b>son la lista de precios publicada de
    /// la casa de fábrica</b>, que es la que le factura a la organización. Por eso el
    /// identificador entra por parámetro y no lo elige el dominio, que no conoce a ninguna casa.
    /// </para>
    /// <para>
    /// <b>No toca ninguna tarifa que ya nombre a alguien</b> y no cambia ningún precio: una tarifa
    /// genérica ya casaba con la casa de fábrica —era la única que facturaba—, así que ponerle su
    /// nombre no mueve ni una cifra de las que nadie tiene ya delante.
    /// </para>
    /// </summary>
    public int AdoptProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return 0;
        }

        int changed = 0;
        for (int i = 0; i < Rates.Count; i++)
        {
            if (Rates[i].IsProviderSpecific)
            {
                continue;
            }

            Rates[i] = Rates[i] with { Provider = providerId };
            changed++;
        }

        return changed;
    }
}
