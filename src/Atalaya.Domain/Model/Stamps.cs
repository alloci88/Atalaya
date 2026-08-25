using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// An anchored detection/confirmation event (§2 <c>firstDetected</c>/<c>lastConfirmed</c>):
/// when, in which mode, against which commit, by whom.
/// </summary>
public sealed record DetectionStamp(DateTimeOffset Utc, AuditMode Mode, string Commit, string By);

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
