namespace Atalaya.Inventory;

/// <summary>
/// Default exclusion patterns (§4): tests, generated code, build/dependency dirs,
/// migrations. All are editable per app (merged with <c>AppConfig.Exclusions</c>).
/// </summary>
public static class DefaultExclusions
{
    public static IReadOnlyList<string> Patterns { get; } = new[]
    {
        // build / dependency / tooling directories (matched as a path segment)
        "bin", "obj", "node_modules", "vendor", "dist", "target", ".venv", "venv",
        ".git", "packages", ".vs", "out", "__pycache__", ".idea", "coverage",
        // test directories
        "test", "tests", "__tests__", "spec", "specs", "testdata", "fixtures",
        // migrations
        "migrations",
        // generated / minified files (matched as filename globs)
        "*.Designer.cs", "*.g.cs", "*.g.i.cs", "*.generated.cs", "*.d.ts", "*.min.js",
        // test files (matched as filename globs)
        "*Tests.cs", "*Test.cs", "*_test.go", "*.spec.ts", "*.spec.js",
        "*.test.ts", "*.test.js", "test_*.py", "*_spec.rb",
    };
}

/// <summary>
/// Matches paths against exclusion patterns. Two pattern shapes:
/// a bare token (e.g. <c>bin</c>) excludes any file whose path has that directory segment;
/// a glob with <c>*</c> (e.g. <c>*.Designer.cs</c>, <c>test_*.py</c>) matches the filename.
/// Case-insensitive (Windows filesystems).
/// </summary>
public sealed class ExclusionMatcher
{
    private readonly HashSet<string> _segments = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, string Suffix)> _globs = new();
    private readonly HashSet<string> _exactFiles = new(StringComparer.OrdinalIgnoreCase);

    public ExclusionMatcher(IEnumerable<string> patterns)
    {
        foreach (string raw in patterns)
        {
            string p = raw.Trim();
            if (p.Length == 0)
            {
                continue;
            }

            if (p.Contains('*'))
            {
                int star = p.IndexOf('*');
                _globs.Add((p[..star], p[(star + 1)..]));
            }
            else if (p.Contains('.'))
            {
                _exactFiles.Add(p); // e.g. a specific filename
            }
            else
            {
                _segments.Add(p); // a directory-segment token
            }
        }
    }

    /// <summary>True if a directory of this name should be pruned from the walk (e.g. node_modules).</summary>
    public bool IsExcludedDirectoryName(string name) => _segments.Contains(name);

    /// <summary>True if the repo-relative path should be excluded from the inventory.</summary>
    public bool IsExcluded(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').Trim('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        // Any directory segment (all but the last) matches an excluded token.
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (_segments.Contains(segments[i]))
            {
                return true;
            }
        }

        string file = segments[^1];
        if (_segments.Contains(file) || _exactFiles.Contains(file))
        {
            return true;
        }

        foreach ((string prefix, string suffix) in _globs)
        {
            if (file.Length >= prefix.Length + suffix.Length
                && file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
