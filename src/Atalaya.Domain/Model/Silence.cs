using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// A silence (§2), keyed by fingerprint, with optional expiry. Silencing is always a
/// human action with author and reason. An expired silence is non-existent for filtering.
/// </summary>
public sealed class Silence
{
    public int SchemaVersion { get; set; } = 1;

    public required string Fingerprint { get; set; }

    public SilenceReason Reason { get; set; }

    public string? Notes { get; set; }

    public required string By { get; set; }

    public DateTimeOffset Utc { get; set; }

    /// <summary>Null = never expires.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>The findings this silence was created against (for provenance).</summary>
    public List<Ulid> FindingUlids { get; set; } = new();

    /// <summary>A silence is live (suppresses) until its expiry, if any.</summary>
    public bool IsLiveAt(DateTimeOffset now) => ExpiresUtc is null || ExpiresUtc.Value > now;

    /// <summary>Expired-but-present: the UI lists these as "caducado, revisar" (§2).</summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresUtc is not null && ExpiresUtc.Value <= now;
}
