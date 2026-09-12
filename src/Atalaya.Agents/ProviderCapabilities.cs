using Atalaya.Domain.Model;

namespace Atalaya.Agents;

/// <summary>
/// <b>Cómo factura una casa y en qué se enseña lo que gasta</b> (PROV-2 §3).
/// <para>
/// El dominio cuenta en <b>dólares</b>: es la unidad de las tarifas publicadas, la única que se
/// puede sumar cuando en el mismo periodo han gastado dos proveedores. Lo que cada casa tenga por
/// costumbre facturar —los AI credits de GitHub— es una lente de <b>presentación</b> suya, y la
/// equivalencia vigente la declara ella, no el dominio: el día que cambie, cambia donde vive el
/// proveedor que la usa.
/// </para>
/// <para>
/// <see cref="NoRateNote"/> es lo que se dice cuando el modelo de esta casa <b>no tiene tarifa</b>
/// en la tabla de la organización. No es un hueco por configurar en todas partes: hay casas que
/// corren contra la suscripción personal de quien las usa y para las que «tarifa no configurada»
/// sería una falsa deuda. Quien lo declara responde por la frase.
/// </para>
/// </summary>
/// <param name="Unit">La unidad corta de presentación («credits»). Null: dólares.</param>
/// <param name="UnitLong">La unidad larga («AI credits»), para una cifra suelta en una tarjeta.</param>
/// <param name="UsdPerUnit">Lo que vale una unidad de ésas, en dólares. Un credit, 0,01 $.</param>
/// <param name="NoRateNote">Qué se lee cuando su modelo no tiene tarifa. Null: «tarifa no configurada».</param>
public sealed record ProviderBilling(
    string? Unit = null,
    string? UnitLong = null,
    decimal UsdPerUnit = 1m,
    string? NoRateNote = null)
{
    /// <summary>Factura en dólares y no tiene nada que declarar: lo que se supone de una casa nueva.</summary>
    public static readonly ProviderBilling Default = new();

    /// <summary>¿Tiene unidad propia de presentación, distinta del dólar?</summary>
    public bool HasOwnUnit => !string.IsNullOrWhiteSpace(Unit) && UsdPerUnit > 0m;

    /// <summary>Ese importe en dólares, escrito en la unidad de esta casa.</summary>
    public decimal InOwnUnit(decimal usd) => HasOwnUnit ? usd / UsdPerUnit : usd;
}

/// <summary>
/// <b>El corte en <c>unit_done</c>, declarado</b> (PROV-2 §2, D-880). Capacidad opcional, como
/// <see cref="IThreadedAuditor"/> o <see cref="INarratingAuditor"/>: el coordinador escucha a
/// quien la declare y no pregunta por el tipo concreto de nadie.
/// <para>
/// Una pasada que no se pudo cortar cuesta una llamada de cortesía más, y eso no puede quedar
/// como una cifra sin causa (N-2): por aquí sube el motivo.
/// </para>
/// </summary>
public interface ICuttingAuditor
{
    /// <summary>Cada pasada que NO se pudo cortar, con su motivo.</summary>
    event Action<string>? CutSkipped;
}
