using System.Text.RegularExpressions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Model;

namespace Atalaya.Domain.Ingestion;

/// <summary>
/// The exact payload the agent delivers through the <c>submit_finding(s)</c> tool (§6.2).
/// It deliberately does NOT carry id, confidence or status — those are the app's job.
/// </summary>
public sealed partial record SubmittedFinding(
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
    /// <summary>The primary file of the finding — the first location.</summary>
    public string PrimaryPath => Locations.Count > 0
        ? Locations[0].Path
        : throw new InvalidOperationException("A submitted finding must have at least one location.");

    /// <summary>
    /// Clave de la ÚNICA salvaguarda de deduplicación que queda (F4): título normalizado +
    /// ubicación primaria. Sirve exclusivamente para rechazar un submit repetido dentro de la
    /// MISMA sesión; nunca se compara contra el histórico — la identidad entre sesiones la
    /// decide el auditor por ULID vía <c>report_verdicts</c>.
    /// </summary>
    public string SessionDuplicateKey => string.Join(
        '\u001f',
        Whitespace().Replace((Title ?? string.Empty).ToLowerInvariant(), " ").Trim(),
        CodeAnchor.NormalizePath(PrimaryPath),
        Locations.Count > 0 ? Locations[0].Line : 0);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
