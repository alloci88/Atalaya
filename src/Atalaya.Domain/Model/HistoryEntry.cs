namespace Atalaya.Domain.Model;

/// <summary>The kinds of events recorded in a finding's <c>history</c> (§2).</summary>
public enum FindingEvent
{
    Detected,
    Confirmed,
    Resolved,
    Reopened,
    Assigned,
    SeverityChanged,
    Silenced,
    Unsilenced,
    Commented,
    /// <summary>LEGADO — no se emite desde F4. Se conserva para leer historiales anteriores.</summary>
    Recurrence,
    /// <summary>An assisted-fix branch was proposed (§5.7, H9).</summary>
    FixProposed,

    /// <summary>
    /// El auditor sostuvo que esto nunca fue un defecto (F5.1b). NO cierra el hallazgo: lo deja
    /// marcado como disputado a la espera de que lo decida una persona.
    /// </summary>
    Disputed,

    /// <summary>Una persona cerró la disputa por gobernanza (F5.1b).</summary>
    DisputeCleared,
}

/// <summary>One immutable entry in a finding's audit trail.</summary>
public sealed record HistoryEntry(DateTimeOffset Utc, FindingEvent Event, string By, string? Detail);
