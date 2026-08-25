namespace Atalaya.App.Services;

/// <summary>
/// Recuerda qué grupos de V3 ha plegado o desplegado el usuario A MANO, mientras la aplicación
/// viva (F5.4 §5).
/// <para>
/// <b>Por qué no vive en el view-model.</b> V3 es transitoria en el contenedor: navegar al detalle
/// y volver construye un view-model nuevo, así que una memoria interna se perdía en cada ida y
/// vuelta — justo el gesto que hace el usuario al revisar hallazgos uno por uno.
/// </para>
/// <para>
/// <b>Por qué no se persiste.</b> Es una preferencia de la sesión, no un ajuste: guardar en disco
/// qué clases tenía plegadas anteayer resucitaría un estado que ya no significa nada, porque los
/// grupos dependen del filtro que hubiera puesto entonces.
/// </para>
/// </summary>
public sealed class GroupExpansionMemory
{
    /// <summary>
    /// Hasta este número de grupos, lo que el usuario NO ha decidido se abre. Por encima, se
    /// pliega: una lista de treinta unidades abierta de par en par no se lee, y plegada es el
    /// resumen ejecutivo (nombre de clase + chips).
    /// </summary>
    public const int SmallListGroups = 5;

    private readonly Dictionary<string, bool> _decided = new(StringComparer.Ordinal);

    /// <summary>La decisión del usuario sobre ese grupo, o <c>null</c> si nunca lo tocó.</summary>
    public bool? Remembered(string key) => _decided.TryGetValue(key, out bool expanded) ? expanded : null;

    /// <summary>Guarda una decisión EXPLÍCITA. Lo automático nunca se guarda: dejaría de ser automático.</summary>
    public void Remember(string key, bool expanded) => _decided[key] = expanded;

    public void Forget() => _decided.Clear();

    /// <summary>Cuántos grupos tienen una decisión del usuario. Para los tests.</summary>
    public int Count => _decided.Count;
}
