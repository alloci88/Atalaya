namespace Atalaya.Storage;

/// <summary>
/// Builds the on-disk paths of the audit-hub layout (§2). All state is small JSON files
/// with unique names so two users almost never write the same file.
/// </summary>
public sealed class HubPaths
{
    public HubPaths(string root) => Root = Path.GetFullPath(root);

    /// <summary>The hub working directory (a git clone).</summary>
    public string Root { get; }

    public string HubJson => Path.Combine(Root, "hub.json");

    public string AppsDir => Path.Combine(Root, "apps");

    public string AppDir(string slug) => Path.Combine(AppsDir, slug);

    public string AppJson(string slug) => Path.Combine(AppDir(slug), "app.json");

    public string InventoryDir(string slug) => Path.Combine(AppDir(slug), "inventory");

    public string InventoryFile(string slug, int cycleN) => Path.Combine(InventoryDir(slug), $"cycle{cycleN}.json");

    public string FindingsDir(string slug) => Path.Combine(AppDir(slug), "findings");

    public string FindingFile(string slug, string ulid) => Path.Combine(FindingsDir(slug), $"{ulid}.json");

    public string SilencesDir(string slug) => Path.Combine(AppDir(slug), "silences");

    /// <summary>F4: un silencio se nombra por el ULID del hallazgo que silencia.</summary>
    public string SilenceFile(string slug, string findingUlid) => Path.Combine(SilencesDir(slug), $"{findingUlid}.json");

    public string ClaimsDir(string slug) => Path.Combine(AppDir(slug), "claims");

    public string ClaimFile(string slug, string unitHash) => Path.Combine(ClaimsDir(slug), $"{HashToFileName(unitHash)}.json");

    public string SessionsDir(string slug) => Path.Combine(AppDir(slug), "sessions");

    public string SessionFile(string slug, string ulid) => Path.Combine(SessionsDir(slug), $"{ulid}.json");

    public string CommentsDir(string slug) => Path.Combine(AppDir(slug), "comments");

    public string CommentThreadDir(string slug, string findingUlid) => Path.Combine(CommentsDir(slug), findingUlid);

    public string CommentFile(string slug, string findingUlid, string commentUlid)
        => Path.Combine(CommentThreadDir(slug, findingUlid), $"{commentUlid}.json");

    public string ReportsDir(string slug) => Path.Combine(AppDir(slug), "reports");

    public string ReportFile(string slug, string sessionUlid) => Path.Combine(ReportsDir(slug), $"{sessionUlid}.md");

    /// <summary>Strips the <c>sha256:</c> prefix to get a filesystem-safe name.</summary>
    public static string HashToFileName(string hashWithPrefix)
    {
        int colon = hashWithPrefix.IndexOf(':');
        return colon >= 0 ? hashWithPrefix[(colon + 1)..] : hashWithPrefix;
    }

    /// <summary>The repo-relative path of a file (forward slashes), for git operations.</summary>
    public string RelativeOf(string absolutePath)
        => Path.GetRelativePath(Root, absolutePath).Replace('\\', '/');
}
