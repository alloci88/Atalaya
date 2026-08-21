using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.Domain.Ingestion;

/// <summary>The kind of outcome ingesting a submitted finding produced (§2 dedupe rules).</summary>
public enum IngestionKind
{
    /// <summary>No prior finding for this fingerprint — a brand-new finding was created.</summary>
    New,

    /// <summary>An active (or silence-expired) finding existed — it was reconfirmed.</summary>
    Reconfirmed,

    /// <summary>Only a resolved finding existed — a NEW finding was created with recurrenceOf.</summary>
    Recurrence,

    /// <summary>A live silence covered the fingerprint — the detection is recorded but no finding surfaces.</summary>
    SuppressedBySilence,
}

/// <summary>Result of ingesting one submitted finding.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Finding">The affected finding (new/reconfirmed/recurrence); null when suppressed.</param>
/// <param name="Fingerprint">The computed fingerprint (always present).</param>
/// <param name="SuppressingSilence">The live silence, when <see cref="IngestionKind.SuppressedBySilence"/>.</param>
public sealed record IngestionOutcome(
    IngestionKind Kind,
    Finding? Finding,
    string Fingerprint,
    Silence? SuppressingSilence);

/// <summary>
/// Ingests a submitted finding into the current app state, applying §2's dedupe rules
/// and the confidence machine. Pure: it takes the relevant existing state as inputs and
/// mutates the matched finding (or returns a new one), so it is directly unit-testable.
/// </summary>
public sealed class IngestionEngine
{
    private readonly IUlidFactory _ulids;

    public IngestionEngine(IUlidFactory ulids) => _ulids = ulids;

    /// <summary>
    /// Ingests <paramref name="submitted"/> detected in <paramref name="mode"/>.
    /// </summary>
    /// <param name="existingSameFingerprint">
    /// All findings already known for this fingerprint (any status). Usually 0 or 1.
    /// </param>
    /// <param name="liveSilence">A non-expired silence for this fingerprint, or null.</param>
    public IngestionOutcome Ingest(
        SubmittedFinding submitted,
        AuditMode mode,
        DetectionStamp stamp,
        IReadOnlyCollection<Finding> existingSameFingerprint,
        Silence? liveSilence)
    {
        string fingerprint = Fingerprint.Compute(
            submitted.RuleId, submitted.PrimaryPath, submitted.Symbol, submitted.Title);

        // 1. A live silence suppresses the finding entirely (only the detection is counted).
        if (liveSilence is not null)
        {
            return new IngestionOutcome(IngestionKind.SuppressedBySilence, null, fingerprint, liveSilence);
        }

        // Defend against being handed findings for a different fingerprint.
        var candidates = existingSameFingerprint
            .Where(f => f.Fingerprint == fingerprint)
            .ToList();

        // 2. An active finding → reconfirmation.
        Finding? active = candidates.FirstOrDefault(f => f.Status == FindingStatus.Activo);
        if (active is not null)
        {
            active.Confirm(mode, stamp);
            return new IngestionOutcome(IngestionKind.Reconfirmed, active, fingerprint, null);
        }

        // 3. A silenced finding whose silence has expired (liveSilence is null) → it reappears.
        Finding? silenced = candidates.FirstOrDefault(f => f.Status == FindingStatus.Silenciado);
        if (silenced is not null)
        {
            silenced.Unsilence(stamp.Utc, stamp.By, "silence expired; re-detected");
            silenced.Confirm(mode, stamp);
            return new IngestionOutcome(IngestionKind.Reconfirmed, silenced, fingerprint, null);
        }

        // 4. Only resolved findings → this is a recurrence: a NEW finding pointing at the old one.
        Finding? resolved = candidates
            .Where(f => f.Status == FindingStatus.Resuelto)
            .OrderByDescending(f => f.Resolved?.Utc ?? f.LastConfirmed.Utc)
            .FirstOrDefault();
        if (resolved is not null)
        {
            Finding recurrence = Finding.CreateNew(
                _ulids.NewUlid(), fingerprint, submitted, mode, stamp, recurrenceOf: resolved.Id);
            return new IngestionOutcome(IngestionKind.Recurrence, recurrence, fingerprint, null);
        }

        // 5. Nothing prior → brand-new finding.
        Finding created = Finding.CreateNew(_ulids.NewUlid(), fingerprint, submitted, mode, stamp);
        return new IngestionOutcome(IngestionKind.New, created, fingerprint, null);
    }
}
