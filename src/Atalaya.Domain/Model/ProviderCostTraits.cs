namespace Atalaya.Domain.Model;

/// <summary>
/// <b>Lo que el CÁLCULO del coste necesita saber de la casa que escribió una sesión</b>
/// (PROV-2 §3).
/// <para>
/// <b>Por qué no se le pregunta al proveedor.</b> El dominio no conoce a ninguno —ése es el punto
/// de <c>IAuditorProvider</c>—, y además esto se aplica sobre sesiones <b>ya guardadas</b>:
/// Métricas relee meses de historia escrita por casas que esta versión puede no traer. Así que
/// quien sí conoce el registro —la aplicación— resuelve estos tres datos y los deja viajar con la
/// tabla de tarifas, dentro de <see cref="CostLookup"/>.
/// </para>
/// <para>
/// <b>El valor por defecto es el histórico</b>, no el de nadie: el proveedor tal y como esté
/// escrito, la entrada con la caché dentro —que es la forma con la que se escribió todo lo que
/// hay en el hub antes de que hubiera dos casas— y ninguna frase de «sin tarifa».
/// </para>
/// </summary>
/// <param name="ProviderId">
/// Con qué identificador se busca la tarifa. <b>No es siempre el que la sesión escribió</b>: las
/// anteriores a F14 no escribieron ninguno porque no había otra casa, y la tabla sí nombra a la
/// que las escribió. Quien reclama ese histórico lo declara en el contrato (revisa D-780), y aquí
/// llega ya resuelto. Null deja pasar el que traiga la sesión.
/// </param>
/// <param name="Accounting">
/// Cómo cuenta esa casa sus tokens de entrada (revisa D-785). Invertirlo desvía todos los costes,
/// así que lo declara quien lo sabe en vez de adivinarlo un <c>switch</c> por nombre.
/// </param>
/// <param name="NoRateNote">
/// Qué se lee cuando su modelo no tiene tarifa: «incluido en tu suscripción de Claude». Null —lo
/// normal— significa que un modelo sin tarifa es un hueco por configurar, como siempre.
/// </param>
public sealed record ProviderCostTraits(
    string? ProviderId = null,
    TokenAccounting Accounting = TokenAccounting.InputIncludesCache,
    string? NoRateNote = null)
{
    /// <summary>Lo que se supone de una casa de la que no se sabe nada: la forma del histórico.</summary>
    public static ProviderCostTraits Historical(string? providerId) => new(providerId);

    /// <summary>
    /// Cómo se resuelven estos rasgos a partir de lo que la sesión escribió. Es una función y no
    /// una tabla porque la contesta el registro de proveedores, que vive fuera del dominio.
    /// </summary>
    public static readonly Func<string?, ProviderCostTraits> Default = Historical;
}
