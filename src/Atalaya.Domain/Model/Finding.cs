using Atalaya.Domain.Ids;
using Atalaya.Domain.Rules;

namespace Atalaya.Domain.Model;

/// <summary>
/// A finding — the central entity (§2). Identity is the <see cref="Id"/> (ULID) plus the
/// <see cref="Fingerprint"/>; <see cref="DisplayId"/> is a mutable presentation alias only.
/// Findings are never deleted; they move between <see cref="FindingStatus"/> values.
/// State transitions go through the methods below so the confidence machine and the
/// audit trail are always enforced together.
/// </summary>
public sealed class Finding
{
    public int SchemaVersion { get; set; } = 1;

    public Ulid Id { get; init; }

    /// <summary>Human-readable alias (BUG-0042). Assigned only after a successful push (§2).</summary>
    public string? DisplayId { get; set; }

    public required string Fingerprint { get; set; }

    public required string RuleId { get; set; }

    public Pillar Pillar { get; set; }

    public FindingTag Tag { get; set; }

    public Severity Severity { get; set; }

    public Confidence Confidence { get; set; }

    public FindingStatus Status { get; set; } = FindingStatus.Activo;

    public required string Title { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Impact { get; set; } = string.Empty;

    public string Recommendation { get; set; } = string.Empty;

    public List<Location> Locations { get; set; } = new();

    public AuditMode Origin { get; set; }

    public required DetectionStamp FirstDetected { get; set; }

    public required DetectionStamp LastConfirmed { get; set; }

    public int TimesConfirmed { get; set; } = 1;

    public ResolutionStamp? Resolved { get; set; }

    /// <summary>Set when a location could not be re-anchored or verify said non-verifiable (§5.4).</summary>
    public bool NeedsReview { get; set; }

    /// <summary>If this finding is a recurrence of a resolved fingerprint, the old ULID (§2).</summary>
    public Ulid? RecurrenceOf { get; set; }

    public string? Assignee { get; set; }

    /// <summary>Previous <see cref="DisplayId"/> values, if an alias ever changed (§2).</summary>
    public List<string> AliasHistory { get; set; } = new();

    public List<HistoryEntry> History { get; set; } = new();

    /// <summary>Builds a brand-new finding, assigning confidence from the origin mode.</summary>
    public static Finding CreateNew(
        Ulid id,
        string fingerprint,
        Ingestion.SubmittedFinding submitted,
        AuditMode origin,
        DetectionStamp stamp,
        Ulid? recurrenceOf = null)
    {
        var finding = new Finding
        {
            Id = id,
            Fingerprint = fingerprint,
            RuleId = submitted.RuleId,
            Pillar = submitted.Pillar,
            Tag = submitted.Tag,
            Severity = submitted.Severity,
            Confidence = ConfidenceMachine.ForNew(origin),
            Status = FindingStatus.Activo,
            Title = submitted.Title,
            Description = submitted.Description,
            Impact = submitted.Impact,
            Recommendation = submitted.Recommendation,
            Locations = submitted.Locations.ToList(),
            Origin = origin,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            TimesConfirmed = 1,
            RecurrenceOf = recurrenceOf,
        };

        finding.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Detected, stamp.By,
            recurrenceOf is null ? $"detected via {origin}" : $"recurrence of {recurrenceOf}"));
        if (recurrenceOf is not null)
        {
            finding.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Recurrence, stamp.By,
                $"reappeared after resolution of {recurrenceOf}"));
        }

        return finding;
    }

    /// <summary>
    /// Reconfirms this finding: applies the confidence machine, refreshes
    /// <see cref="LastConfirmed"/>, increments <see cref="TimesConfirmed"/> and logs it.
    /// </summary>
    public void Confirm(AuditMode mode, DetectionStamp stamp, bool cycleClose = false)
    {
        Confidence before = Confidence;
        Confidence = ConfidenceMachine.OnReconfirm(Confidence, mode, cycleClose);
        LastConfirmed = stamp;
        TimesConfirmed++;

        string detail = before == Confidence
            ? $"confirmed via {mode}"
            : $"confirmed via {mode}, confidence {before}→{Confidence}";
        History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Confirmed, stamp.By, detail));
    }

    /// <summary>Moves the finding to <see cref="FindingStatus.Resuelto"/> with an anchor (§5.7).</summary>
    public void Resolve(ResolutionStamp stamp)
    {
        Status = FindingStatus.Resuelto;
        Resolved = stamp;
        History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Resolved, stamp.By,
            $"resolved via {stamp.Via}" + (stamp.Justification is null ? "" : $": {stamp.Justification}")));
    }

    /// <summary>Reopens a resolved finding (governance).</summary>
    public void Reopen(DateTimeOffset utc, string by, string? detail)
    {
        Status = FindingStatus.Activo;
        Resolved = null;
        History.Add(new HistoryEntry(utc, FindingEvent.Reopened, by, detail));
    }

    public void ChangeSeverity(Severity newSeverity, DateTimeOffset utc, string by)
    {
        if (newSeverity == Severity)
        {
            return;
        }

        Severity old = Severity;
        Severity = newSeverity;
        History.Add(new HistoryEntry(utc, FindingEvent.SeverityChanged, by, $"{old}→{newSeverity}"));
    }

    public void Assign(string? assignee, DateTimeOffset utc, string by)
    {
        Assignee = assignee;
        History.Add(new HistoryEntry(utc, FindingEvent.Assigned, by,
            assignee is null ? "unassigned" : $"assigned to {assignee}"));
    }

    /// <summary>Marks the finding silenced (a human action; the silence entity is separate).</summary>
    public void MarkSilenced(DateTimeOffset utc, string by, string? detail)
    {
        Status = FindingStatus.Silenciado;
        History.Add(new HistoryEntry(utc, FindingEvent.Silenced, by, detail));
    }

    /// <summary>Lifts silence, returning to active (a human action or silence expiry re-detection).</summary>
    public void Unsilence(DateTimeOffset utc, string by, string? detail)
    {
        if (Status == FindingStatus.Silenciado)
        {
            Status = FindingStatus.Activo;
        }

        History.Add(new HistoryEntry(utc, FindingEvent.Unsilenced, by, detail));
    }

    /// <summary>Assigns the presentation alias (post-push), preserving any prior alias (§2).</summary>
    public void AssignDisplayId(string displayId)
    {
        if (!string.IsNullOrEmpty(DisplayId) && DisplayId != displayId)
        {
            AliasHistory.Add(DisplayId);
        }

        DisplayId = displayId;
    }
}
