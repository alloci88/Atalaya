using Atalaya.Domain.Model;

namespace Atalaya.Inventory;

/// <summary>
/// Per-stack scanning profile: which files are units, which files mark a module boundary,
/// and how a module is named. Drives both detection and enumeration (§4).
/// </summary>
public sealed record StackProfile(
    TechStack Stack,
    IReadOnlyList<string> SourceExtensions,
    IReadOnlyList<string> ModuleManifests)
{
    private static readonly StackProfile[] All =
    {
        new(TechStack.DotNet, new[] { ".cs" }, new[] { "*.csproj" }),
        new(TechStack.TypeScript, new[] { ".ts", ".tsx" }, new[] { "package.json" }),
        new(TechStack.JavaScript, new[] { ".js", ".jsx", ".mjs", ".cjs" }, new[] { "package.json" }),
        new(TechStack.Python, new[] { ".py" }, new[] { "pyproject.toml", "setup.py", "setup.cfg" }),
        new(TechStack.Go, new[] { ".go" }, new[] { "go.mod" }),
        new(TechStack.Rust, new[] { ".rs" }, new[] { "Cargo.toml" }),
        new(TechStack.Java, new[] { ".java" }, new[] { "pom.xml", "build.gradle", "build.gradle.kts" }),
        new(TechStack.CCpp, new[] { ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".hxx" },
            new[] { "CMakeLists.txt" }),
    };

    public static StackProfile For(TechStack stack)
        => All.FirstOrDefault(p => p.Stack == stack) ?? All[0];

    public static IReadOnlyList<StackProfile> Known => All;

    public bool IsSourceFile(string fileName)
        => SourceExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public bool IsManifest(string fileName)
        => ModuleManifests.Any(m => m.StartsWith('*')
            ? fileName.EndsWith(m[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(fileName, m, StringComparison.OrdinalIgnoreCase));
}
