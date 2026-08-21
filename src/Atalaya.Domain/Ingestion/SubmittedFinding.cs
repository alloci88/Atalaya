using Atalaya.Domain.Model;

namespace Atalaya.Domain.Ingestion;

/// <summary>
/// The exact payload the agent delivers through the <c>submit_finding</c> tool (§6.2).
/// It deliberately does NOT carry id, confidence or status — those are the app's job.
/// </summary>
public sealed record SubmittedFinding(
    string RuleId,
    Pillar Pillar,
    FindingTag Tag,
    Severity Severity,
    string Title,
    string Description,
    string Impact,
    string Recommendation,
    IReadOnlyList<Location> Locations,
    string? Symbol)
{
    /// <summary>The primary file of the finding — the first location. Drives the fingerprint.</summary>
    public string PrimaryPath => Locations.Count > 0
        ? Locations[0].Path
        : throw new InvalidOperationException("A submitted finding must have at least one location.");
}
