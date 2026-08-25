using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// An anchored detection/confirmation event (§2 <c>firstDetected</c>/<c>lastConfirmed</c>):
/// when, in which mode, against which commit, by whom.
/// </summary>
/// <param name="UnitContentHash">
/// SHA-256 del contenido de la unidad tal y como el auditor la vio (F5.1b). Es la segunda capa de
/// la guarda de evidencia de cambio: el commit puede haber avanzado sin que ESTA unidad cambiara,
/// y entonces un veredicto «arreglado» sigue siendo imposible. Null en sellos anteriores a F5.1b.
/// </param>
/// <param name="Model">
/// Modelo que hizo la observación, cuando se conoce (F5.1b). Sirve para nombrar a quién discrepa
/// cuando dos modelos se contradicen sobre el mismo hallazgo. Null si no se registró.
/// </param>
public sealed record DetectionStamp(
    DateTimeOffset Utc,
    AuditMode Mode,
    string Commit,
    string By,
    string? UnitContentHash = null,
    string? Model = null);

/// <summary>
/// Una discrepancia de criterio (F5.1b): el auditor sostiene que el hallazgo NUNCA fue un defecto.
/// <para>
/// No es una resolución y no debe archivarse como tal. Hasta F5.1b el vocabulario del auditor era
/// {presente, arreglado, no-verificable}, así que para expresar desacuerdo la única casilla
/// disponible era «arreglado» — y un desacuerdo acababa cerrando el hallazgo en silencio. Esta
/// entrada guarda quién discrepó, cuándo y por qué, y se ACUMULA: tres modelos distintos
/// discrepando del mismo hallazgo es una señal muy fuerte para la persona que decide.
/// </para>
/// </summary>
/// <param name="Model">El modelo que discrepó; null si la sesión no lo registró.</param>
/// <param name="By">El usuario en cuya sesión se produjo la discrepancia.</param>
/// <param name="Justification">El razonamiento del auditor. Obligatorio: sin él no hay disputa.</param>
public sealed record DisputeEntry(
    DateTimeOffset Utc,
    string? Model,
    string By,
    string Justification);

/// <summary>How a finding reached <see cref="FindingStatus.Resuelto"/> (§5.7 — four vías).</summary>
public enum ResolutionVia
{
    /// <summary>
    /// LEGADO — no se emite desde F4. La resolución implícita ("cubierta por la sesión y no
    /// re-reportada") está eliminada: nada se resuelve por omisión. Se conserva el valor SOLO
    /// para poder leer hallazgos resueltos por sesiones anteriores sin romper.
    /// </summary>
    Implicita,

    /// <summary>
    /// El auditor declaró el hallazgo <c>arreglado</c> en <c>report_verdicts</c> durante una
    /// sesión de auditoría, con evidencia (F4). Es la vía normal de resolución.
    /// </summary>
    Auditor,

    /// <summary>A verify session returned verdict <see cref="Verdict.Resuelto"/>.</summary>
    Verify,

    /// <summary>Manual governance action with mandatory justification.</summary>
    Manual,
}

/// <summary>Resolution anchor (§5.7): every resolution pins a commit and an author.</summary>
public sealed record ResolutionStamp(
    DateTimeOffset Utc,
    ResolutionVia Via,
    AuditMode Mode,
    string Commit,
    string By,
    string? Justification);
