using LibGit2Sharp;

namespace Atalaya.App.Services;

/// <summary>Tiny read-only helpers over a local clone (§5.2 freshness, anchoring commits).</summary>
public static class GitInfo
{
    /// <summary>Short HEAD commit of a clone, or "unknown" if it is not a repo.</summary>
    public static string HeadSha(string? clonePath)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || !Repository.IsValid(clonePath))
        {
            return "unknown";
        }

        try
        {
            using var repo = new Repository(clonePath);
            return repo.Head.Tip?.Sha[..Math.Min(7, repo.Head.Tip.Sha.Length)] ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
