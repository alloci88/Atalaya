using System.Text.Json.Serialization;
using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// <b>Ids de modelo que no nombran un modelo</b> (F29 §0).
/// <para>
/// Copilot deja elegir <c>auto</c>: el enrutador decide por llamada. Lo que la sesión registra
/// entonces es lo que se PIDIÓ, no con qué se contestó — y <c>auto</c> no puede tener tarifa,
/// porque no hay ningún precio publicado para «lo que el enrutador decida». Tratarlo como un
/// modelo sin tarifa manda al usuario a añadir un precio que no debe existir, que es exactamente
/// lo que hacía el aviso ámbar de Ajustes: «auto (copilot) · 1 sesión».
/// </para>
/// <para>
/// No es una lista de modelos —eso es lo que el guarda de F5.15 prohíbe—: es la lista de las
/// palabras que ocupan el sitio de un modelo sin serlo, y ninguna de ellas caduca.
/// </para>
/// </summary>
public static class ModelIds
{
    /// <summary>El enrutador de Copilot: «elige tú». No es un modelo y no lleva tarifa.</summary>
    public const string Auto = "auto";

    /// <summary>¿Esta cadena ocupa el sitio de un modelo sin nombrar ninguno?</summary>
    public static bool IsPlaceholder(string? model)
        => string.Equals(model?.Trim(), Auto, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Cómo se resolvió el hueco de coste de una sesión (F29 §1).</summary>
public enum CostResolution
{
    /// <summary>
    /// Con el modelo REAL de cada llamada, que el proveedor devolvió y la sesión guardó. El coste
    /// es la suma de los costes de sus llamadas, cada una con la tarifa de su modelo: es un coste
    /// <b>medido</b>, no una estimación, y por eso no lleva marca.
    /// </summary>
    PorLlamada,

    /// <summary>
    /// El modelo de la sesión ya tiene tarifa: alguien la añadió a la tabla del hub. No hay nada
    /// que elegir — el coste sale de la fórmula de siempre en cuanto la tarifa existe.
    /// </summary>
    TarifaAnadida,

    /// <summary>
    /// Nadie sabe con qué modelo corrió, así que una persona ELIGIÓ con qué tarifa valorarla. El
    /// coste que sale de aquí es <b>estimado</b>, lleva su marca donde se enseñe y no deja de
    /// llevarla nunca.
    /// </summary>
    TarifaAsignada,
}

/// <summary>
/// <b>Cómo se cerró el hueco de coste de UNA sesión</b> (F29 §1).
/// <para>
/// <b>Carpeta propia, y la sesión no se toca</b>: <c>apps/{slug}/cost-reconciliations/{ulid}.json</c>.
/// Una sesión es el registro inmutable de lo que pasó aquel día y esto es una decisión posterior
/// sobre cómo valorarla — el mismo argumento por el que los arreglos de F9 §2 viven en
/// <c>fixes/</c> y no dentro de la sesión que los produjo. Un fichero por sesión, merge-friendly
/// como todo lo demás.
/// </para>
/// <para>
/// <b>Aquí NO hay ningún coste escrito, y es a propósito.</b> D-788 dice que los tokens son el
/// hecho y los credits un derivado que se recalcula en cada lectura; guardar el número que salió
/// aquel día crearía la segunda verdad que D-788 fue a eliminar, y una tarifa corregida mañana ya
/// no lo alcanzaría. Lo que se guarda es lo que FALTABA para poder calcularlo: con qué modelo, por
/// decisión de quién y cuándo.
/// </para>
/// </summary>
public sealed class CostReconciliation
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>La sesión que se reconcilia. Es también el nombre del fichero.</summary>
    public Ulid SessionId { get; set; }

    public required string AppSlug { get; set; }

    public CostResolution How { get; set; }

    /// <summary>Quién lo decidió. Sale de la identidad del hub, igual que el autor de una sesión.</summary>
    public required string By { get; set; }

    /// <summary>Cuándo. Es la fecha que el informe enseña como «calculado a posteriori».</summary>
    public DateOnly On { get; set; }

    /// <summary>
    /// La tarifa que se eligió para valorarla, en <see cref="CostResolution.TarifaAsignada"/>.
    /// Null en las otras dos, donde no eligió nadie.
    /// </summary>
    public string? AssignedModel { get; set; }

    /// <summary>
    /// Los modelos que de verdad contestaron, en <see cref="CostResolution.PorLlamada"/>. Se
    /// guardan porque son la respuesta a «¿y qué fue "auto" en realidad?», y esa pregunta se hace
    /// mirando el informe de dentro de un año, no la sesión.
    /// </summary>
    public List<string> Models { get; set; } = new();

    /// <summary>
    /// <b>Un coste estimado nunca se confunde con uno medido</b> (F29 §1). Solo la tarifa asignada
    /// a mano estima; las otras dos formas calculan con la tarifa que corresponde.
    /// </summary>
    [JsonIgnore]
    public bool IsEstimate => How == CostResolution.TarifaAsignada;
}
