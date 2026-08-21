using Atalaya.Domain;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Ingests a submitted finding into a hub app: applies §2 dedupe + the confidence machine
/// and persists the result. Shared by inventory scans (large-unit findings) and live audit
/// sessions (H5). The agent never writes state — this does (mejora 1).
/// </summary>
public sealed class FindingIngestionService
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;
    private readonly IngestionEngine _engine;

    public FindingIngestionService(HubContext hub, IUlidFactory ulids)
    {
        _hub = hub;
        _ulids = ulids;
        _engine = new IngestionEngine(ulids);
    }

    /// <summary>
    /// Ingests one finding. Returns the outcome so callers can update session counters. Persists
    /// the affected finding (new/recurrence/reconfirmation); suppressed detections write nothing.
    /// </summary>
    public IngestionOutcome Ingest(string slug, SubmittedFinding submitted, AuditMode mode, DetectionStamp stamp)
    {
        string fingerprint = Fingerprint.Compute(
            submitted.RuleId, submitted.PrimaryPath, submitted.Symbol, submitted.Title);

        var existing = _hub.Store.FindByFingerprint(slug, fingerprint);
        Silence? silence = _hub.Store.TryReadSilence(slug, fingerprint);
        Silence? live = silence is not null && silence.IsLiveAt(stamp.Utc) ? silence : null;

        IngestionOutcome outcome = _engine.Ingest(submitted, mode, stamp, existing, live);

        if (outcome.Finding is not null)
        {
            _hub.Store.WriteFinding(slug, outcome.Finding);
        }

        return outcome;
    }
}
