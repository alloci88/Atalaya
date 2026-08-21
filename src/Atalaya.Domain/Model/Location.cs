using System.Diagnostics.CodeAnalysis;

namespace Atalaya.Domain.Model;

/// <summary>
/// A <c>fichero:línea</c> location of a finding, with a hash of the anchored snippet
/// so verify can re-anchor when lines move (§5.4). The snippet itself is not stored.
/// </summary>
public sealed class Location
{
    public required string Path { get; set; }

    public int Line { get; set; }

    /// <summary>Hash of the anchored code snippet (<see cref="Fingerprinting.Fingerprint.ComputeSnippetHash"/>).</summary>
    public string? SnippetHash { get; set; }

    public Location() { }

    [SetsRequiredMembers]
    public Location(string path, int line, string? snippetHash = null)
    {
        Path = path;
        Line = line;
        SnippetHash = snippetHash;
    }
}
