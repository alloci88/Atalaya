using Atalaya.Domain.Hashing;

namespace Atalaya.Domain.Model;

/// <summary>
/// An ephemeral claim on a unit (§2), stored as <c>claims/{unitHash}.json</c> and deleted
/// on release. Has a TTL; the owning session heartbeats it every TTL/2 so long sessions
/// keep it alive while a dead session releases it in ≤ TTL.
/// </summary>
public sealed class Claim
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The unit's repo-relative path.</summary>
    public required string Unit { get; set; }

    public required string Module { get; set; }

    public required string By { get; set; }

    public required string Machine { get; set; }

    /// <summary>Last heartbeat/creation time. Renewed by the owning session.</summary>
    public DateTimeOffset Utc { get; set; }

    public int TtlMinutes { get; set; } = 30;

    /// <summary>Stable identity (§2): SHA-256 of the normalized unit path.</summary>
    public string UnitHash => HashUtil.UnitHash(Unit);

    public DateTimeOffset ExpiresAt => Utc.AddMinutes(TtlMinutes);

    /// <summary>When the owning session should next heartbeat (TTL/2).</summary>
    public DateTimeOffset HeartbeatDueAt => Utc.AddMinutes(TtlMinutes / 2.0);

    /// <summary>Expired claims are ignorable and deletable by anyone (§2).</summary>
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;
}
