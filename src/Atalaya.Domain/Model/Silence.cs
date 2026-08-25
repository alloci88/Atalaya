using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// A silence (§2), con caducidad opcional. Silenciar es siempre una acción humana con autor y
/// motivo. Un silencio caducado es inexistente a efectos de filtrado.
/// <para>
/// F4: la clave pasa a ser el <b>ULID del hallazgo</b> (<see cref="FindingUlid"/>), no el
/// fingerprint. El hallazgo silenciado sigue existiendo con estado
/// <see cref="FindingStatus.Silenciado"/> y el auditor lo ve en la lista de existentes de su
/// unidad, así que puede declararlo "presente" sin que reaparezca.
/// </para>
/// </summary>
public sealed class Silence
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>El hallazgo silenciado. Es la clave: <c>silences/{ulid}.json</c>.</summary>
    public Ulid FindingUlid { get; set; }

    public SilenceReason Reason { get; set; }

    public string? Notes { get; set; }

    public required string By { get; set; }

    public DateTimeOffset Utc { get; set; }

    /// <summary>Null = never expires.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>A silence is live (suppresses) until its expiry, if any.</summary>
    public bool IsLiveAt(DateTimeOffset now) => ExpiresUtc is null || ExpiresUtc.Value > now;

    /// <summary>Expired-but-present: the UI lists these as "caducado, revisar" (§2).</summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresUtc is not null && ExpiresUtc.Value <= now;
}
