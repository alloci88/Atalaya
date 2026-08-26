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

    /// <summary>
    /// La URL del remoto <c>origin</c> de un clon, o null si la carpeta no es un repo, no tiene
    /// <c>origin</c>, o no se puede abrir (F5.8 §1).
    /// <para>
    /// Es la EVIDENCIA con la que se decide si un clon local es de verdad el de una aplicación:
    /// que la ruta exista no prueba nada — una carpeta puede haberse reutilizado para otro repo.
    /// </para>
    /// </summary>
    public static string? OriginUrl(string? clonePath)
    {
        if (!IsRepo(clonePath))
        {
            return null;
        }

        try
        {
            using var repo = new Repository(clonePath);
            string? url = repo.Network.Remotes["origin"]?.Url;
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True si la carpeta es un repositorio git utilizable.</summary>
    public static bool IsRepo(string? path)
        => !string.IsNullOrWhiteSpace(path) && Repository.IsValid(path);
}
