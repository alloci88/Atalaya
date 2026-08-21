using Atalaya.Domain.Model;

namespace Atalaya.Inventory;

/// <summary>Detects the stack of a clone by its manifests (§4).</summary>
public static class StackDetector
{
    /// <summary>
    /// Returns the most specific stack whose manifests are present. TypeScript wins over
    /// JavaScript when a <c>tsconfig.json</c> exists; C# wins on <c>.sln</c>/<c>.csproj</c>.
    /// </summary>
    public static TechStack Detect(string root)
    {
        if (!Directory.Exists(root))
        {
            return TechStack.Unknown;
        }

        bool Has(string pattern) => EnumerateShallow(root, pattern).Any();

        if (Has("*.sln") || Has("*.csproj"))
        {
            return TechStack.DotNet;
        }

        if (Has("go.mod"))
        {
            return TechStack.Go;
        }

        if (Has("Cargo.toml"))
        {
            return TechStack.Rust;
        }

        if (Has("pom.xml") || Has("build.gradle") || Has("build.gradle.kts"))
        {
            return TechStack.Java;
        }

        if (Has("pyproject.toml") || Has("setup.py") || Has("requirements.txt") || Has("setup.cfg"))
        {
            return TechStack.Python;
        }

        if (Has("package.json"))
        {
            return Has("tsconfig.json") ? TechStack.TypeScript : TechStack.JavaScript;
        }

        if (Has("CMakeLists.txt") || Has("*.vcxproj"))
        {
            return TechStack.CCpp;
        }

        return TechStack.Unknown;
    }

    // Manifests can live several levels deep (e.g. src/Project/Project.csproj). Walk the tree
    // pruning heavy/irrelevant directories so detection is thorough without scanning junk.
    private static IEnumerable<string> EnumerateShallow(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (string f in files)
            {
                yield return f;
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (string sub in subdirs)
            {
                string name = Path.GetFileName(sub);
                if (name is "node_modules" or ".git" or "bin" or "obj" or "target"
                    or "vendor" or "dist" or "out" or ".venv" or "packages")
                {
                    continue;
                }

                pending.Push(sub);
            }
        }
    }
}
