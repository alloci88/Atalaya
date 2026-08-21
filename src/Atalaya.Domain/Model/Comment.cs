using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// A comment attached to a finding (§2), stored as <c>comments/{findingUlid}/{ulid}.json</c>.
/// The fix-prompt generator (§5.7) also records its output here with <see cref="Kind"/> = "fix-prompt".
/// </summary>
public sealed class Comment
{
    public int SchemaVersion { get; set; } = 1;

    public Ulid Id { get; init; }

    public Ulid FindingUlid { get; set; }

    public required string By { get; set; }

    public DateTimeOffset Utc { get; set; }

    public required string Body { get; set; }

    /// <summary>Optional discriminator, e.g. "fix-prompt". Null = plain human comment.</summary>
    public string? Kind { get; set; }
}
