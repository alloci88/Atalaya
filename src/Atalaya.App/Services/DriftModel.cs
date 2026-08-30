using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Qué le ha pasado a una unidad AUDITADA desde que se auditó (F9 §1).
/// <para>
/// <b>Es una dimensión aparte del estado de auditoría, no un valor más.</b> Una unidad puede estar
/// «auditada» Y «cambiada» a la vez: lo primero dice que alguien la miró, lo segundo que el código
/// que miró ya no es el que hay. Meter la deriva en <see cref="Domain.UnitState"/> habría obligado
/// a elegir cuál de las dos cosas se cuenta, y las dos hacen falta.
/// </para>
/// </summary>
public enum DriftState
{
    /// <summary>El código no ha cambiado desde su auditoría. Nada que hacer.</summary>
    SinCambios,

    /// <summary>Cambió, y al menos un cambio es ajeno a los arreglos de la propia aplicación.</summary>
    Modificada,

    /// <summary>
    /// Lo único que la ha tocado son arreglos de la propia aplicación (F9 §2). No es deriva: es
    /// trabajo pendiente de comprobar, y se comprueba verificando, no re-auditando.
    /// </summary>
    ArregladaPendienteDeVerificar,

    /// <summary>La unidad ya no está en HEAD. Se informa aparte; nunca se resuelve nada solo.</summary>
    Borrada,

    /// <summary>
    /// No se puede saber: el commit de la auditoría no está en este clon, el historial se reescribió,
    /// o el clon va por detrás de la máquina que auditó. Ni «sin cambios» ni «cambiada» — las dos
    /// serían inventadas.
    /// </summary>
    HistorialNoDisponible,
}

/// <summary>La deriva de UNA unidad (F9 §1).</summary>
/// <param name="Path">La ruta de la unidad en el inventario vigente.</param>
/// <param name="Commits">
/// Cuántos commits AJENOS la tocaron. Los arreglos propios reconocidos no se cuentan aquí: van en
/// <paramref name="OwnFixes"/>, porque suman una acción distinta.
/// </param>
/// <param name="LastChangeUtc">Fecha del committer del último commit que la tocó.</param>
/// <param name="OwnFixes">
/// Arreglos de la propia aplicación reconocidos desde la auditoría. A partir de
/// <see cref="DriftRules.MaxOwnFixesBeforeReaudit"/> la unidad pasa a «cambiada» aunque todos sean
/// propios.
/// </param>
/// <param name="RenamedFrom">De dónde venía, cuando el cambio fue un renombrado o un movimiento.</param>
/// <param name="Note">Por qué no se sabe, cuando no se sabe. Vacío en los casos normales.</param>
public sealed record UnitDrift(
    string Path,
    DriftState State,
    int Commits = 0,
    DateTimeOffset? LastChangeUtc = null,
    int OwnFixes = 0,
    string? RenamedFrom = null,
    string? Note = null)
{
    /// <summary>Entra en «Seleccionar cambiadas»: solo lo que se re-audita.</summary>
    public bool IsChanged => State == DriftState.Modificada;

    /// <summary>Lo que se lee en la fila del inventario. El color nunca es el único canal.</summary>
    public string Label => State switch
    {
        DriftState.Modificada => Commits == 1
            ? "Cambiada desde la auditoría (1 commit)"
            : $"Cambiada desde la auditoría ({Commits} commits)",
        DriftState.ArregladaPendienteDeVerificar => "Arreglada — pendiente de verificar",
        DriftState.Borrada => "Ya no existe en el repositorio",
        DriftState.HistorialNoDisponible => "Historial no disponible",
        _ => string.Empty,
    };

    /// <summary>El detalle: la fecha, de dónde venía si se movió, o por qué no se sabe.</summary>
    public string Tooltip
    {
        get
        {
            string when = LastChangeUtc is { } utc
                ? $" · último el {utc.ToLocalTime().ToString("d MMM yyyy", AppCulture.Display)}"
                : string.Empty;
            string from = RenamedFrom is { Length: > 0 } ? $" · venía de {RenamedFrom}" : string.Empty;

            return State switch
            {
                DriftState.SinCambios => "El código no ha cambiado desde que se auditó.",
                DriftState.Modificada => Label + when + from
                    + (OwnFixes > 0
                        ? $" · incluye {OwnFixes} arreglo(s) de Atalaya"
                        : string.Empty)
                    + ". Candidata a re-auditar.",
                DriftState.ArregladaPendienteDeVerificar =>
                    $"Lo único que la ha tocado son {OwnFixes} arreglo(s) hechos desde Atalaya{when}. "
                    + "No se re-audita: se verifica, que es el instrumento que detectó el hallazgo.",
                DriftState.Borrada => "El fichero ya no está en HEAD. Sus hallazgos activos, si los "
                    + "hay, salen en «unidades que ya no existen».",
                _ => Note ?? "No se ha podido comparar con el historial de este clon.",
            };
        }
    }
}

/// <summary>
/// Un hallazgo activo que se quedó sin código (F9 §4): la unidad que lo contenía ya no está en HEAD.
/// </summary>
/// <param name="DeletedCommit">
/// El commit que borró el fichero, cuando se ha podido determinar. Es la EVIDENCIA de la
/// resolución: sin él la acción sigue siendo posible, pero se registra diciendo que no se localizó.
/// </param>
public sealed record OrphanFinding(
    string UnitPath,
    string FindingId,
    string? Alias,
    string Title,
    Domain.Severity Severity,
    string? DeletedCommit,
    DateTimeOffset? DeletedUtc);

/// <summary>La deriva de una aplicación entera, con los avisos que condicionan su lectura (F9 §1.1).</summary>
public sealed record AppDrift(
    string Slug,
    IReadOnlyList<UnitDrift> Units,
    IReadOnlyList<OrphanFinding> Orphans)
{
    /// <summary>Nada que decir: no hay clon, no es un repo, o la app no existe.</summary>
    public static AppDrift Unavailable(string slug, string problem) => new(
        slug, Array.Empty<UnitDrift>(), Array.Empty<OrphanFinding>()) { Problem = problem };

    public static AppDrift Empty(string slug) => new(
        slug, Array.Empty<UnitDrift>(), Array.Empty<OrphanFinding>());

    /// <summary>Por qué no hay nada que enseñar. Null cuando sí lo hay.</summary>
    public string? Problem { get; init; }

    /// <summary>Contra qué rama se ha medido. El panel lo DICE: no es lo mismo medir contra otra.</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>La rama es la por defecto del repositorio. Si no, el panel lo matiza (F9 §1.1).</summary>
    public bool BranchIsDefault { get; init; } = true;

    /// <summary>HEAD del clon: la foto contra la que se ha comparado todo.</summary>
    public string Head { get; init; } = string.Empty;

    /// <summary>Cambios sin commitear que este análisis NO ve (F9 §1.1).</summary>
    public int UncommittedFiles { get; init; }

    /// <summary>HEAD local va por detrás de su remoto: la foto puede estar incompleta.</summary>
    public bool BehindRemote { get; init; }

    /// <summary>Cuánto tardó el cálculo. Se enseña en el tooltip; sirve para no volar a ciegas.</summary>
    public TimeSpan Elapsed { get; init; }

    public int Changed => Units.Count(u => u.State == DriftState.Modificada);

    public int FixedPendingVerify => Units.Count(u => u.State == DriftState.ArregladaPendienteDeVerificar);

    public int HistoryUnavailable => Units.Count(u => u.State == DriftState.HistorialNoDisponible);

    public int Deleted => Units.Count(u => u.State == DriftState.Borrada);

    /// <summary>
    /// Las cambiadas, más toqueteada primero (F9 §3). El desempate por ruta hace el orden estable:
    /// dos listas iguales tienen que salir iguales, o el usuario cree que algo se ha movido.
    /// </summary>
    public IReadOnlyList<UnitDrift> ChangedUnits => Units
        .Where(u => u.IsChanged)
        .OrderByDescending(u => u.Commits)
        .ThenBy(u => u.Path, StringComparer.Ordinal)
        .ToList();

    /// <summary>Los avisos honestos que acompañan al número, en el orden en que importan.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var list = new List<string>();
            if (!BranchIsDefault && Branch.Length > 0)
            {
                list.Add($"La deriva se mide contra «{Branch}», que no es la rama por defecto del "
                    + "repositorio: es legítimo, pero lo que veas es la deriva de esa rama.");
            }

            if (BehindRemote)
            {
                list.Add("Tu clon va por detrás de su remoto: haz pull para ver la deriva real.");
            }

            if (UncommittedFiles > 0)
            {
                list.Add($"Hay {UncommittedFiles} fichero(s) sin commitear que este análisis no ve: "
                    + "la deriva se mide entre commits.");
            }

            if (HistoryUnavailable > 0)
            {
                list.Add($"{HistoryUnavailable} unidad(es) sin historial comparable: mira su detalle "
                    + "en la lista.");
            }

            return list;
        }
    }
}

/// <summary>Las reglas de la deriva que son una decisión y no un cálculo (F9 §2).</summary>
public static class DriftRules
{
    /// <summary>
    /// Arreglos propios acumulados sobre la misma unidad antes de que pase a «cambiada» aunque
    /// ninguno sea ajeno (F9 §2).
    /// <para>
    /// <b>Tres, y por qué.</b> Un arreglo pendiente de verificar es trabajo a medio cerrar y la
    /// respuesta es verificarlo. Pero tres arreglos encadenados sobre la misma unidad sin que nadie
    /// la haya vuelto a mirar entera ya no son tres retoques: son una unidad que se está
    /// reescribiendo a trozos, y el riesgo deja de ser el del hallazgo que se arreglaba. El número
    /// exacto no sale de una medida —no hay datos todavía—: sale de que uno sería no dejar arreglar
    /// nada y diez sería no mirar nunca. Se sube o se baja cuando el uso diga cuál de las dos
    /// molesta.
    /// </para>
    /// </summary>
    public const int MaxOwnFixesBeforeReaudit = 3;
}
