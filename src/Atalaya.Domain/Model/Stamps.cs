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
    /// <summary>Implicit: a lotes/integral re-audit covered its units and did not re-report it.</summary>
    Implicita,

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
