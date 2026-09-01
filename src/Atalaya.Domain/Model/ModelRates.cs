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
/// A qué proveedor aplica esta tarifa, o null para «a cualquiera» (F15).
/// <para>
/// Existe porque <b>el mismo modelo puede costar distinto según quién factura</b>. El caso real:
/// Claude Code usa caché de una hora, que Anthropic cobra al doble de la entrada, mientras que la
/// tabla de GitHub publica la de cinco minutos (1,25×). Sin este campo habría que elegir una de las
/// dos y equivocarse con la otra.
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
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CachedInputPerMillion,
    decimal? CacheWritePerMillion = null,
    string? Provider = null,
    DateOnly? EffectiveFrom = null,
    string? Note = null)
{
    /// <summary>¿Es esta la tarifa de ese modelo y ese proveedor?</summary>
    public bool Matches(string? model, string? provider)
        => model is { Length: > 0 }
           && string.Equals(Model, model, StringComparison.OrdinalIgnoreCase)
           && (Provider is null
               || string.Equals(Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>Una tarifa atada a un proveedor concreto gana a la genérica.</summary>
    public bool IsProviderSpecific => Provider is { Length: > 0 };
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
}
