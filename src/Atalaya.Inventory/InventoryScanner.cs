using Atalaya.Domain;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;

namespace Atalaya.Inventory;

/// <summary>The result of scanning a clone (§4).</summary>
/// <param name="Stack">Detected (or configured) stack.</param>
/// <param name="Inventory">The full cycle inventory with per-unit state.</param>
/// <param name="LargeUnitFindings">Auto-generated refactor findings for oversized units.</param>
/// <param name="Modules">Distinct module names discovered.</param>
public sealed record ScanOutput(
    TechStack Stack,
    InventoryCycle Inventory,
    IReadOnlyList<SubmittedFinding> LargeUnitFindings,
    IReadOnlyList<string> Modules);

/// <summary>
/// Scans a local clone into an inventory (§4): detects the stack, enumerates modules and
/// units applying exclusions, computes LOC and content hash, marks oversized units "grande"
/// and emits a stable refactor finding for each. The "too big" call is the app's, via the
/// configurable threshold — never the agent's (mejora 7).
/// </summary>
public sealed class InventoryScanner
{
    /// <summary>The stable ruleId used for auto "unit too large" findings.</summary>
    public const string LargeUnitRuleId = "mejoras.mantenibilidad.unidad-grande";

    /// <summary>Un título constante para que el auditor lo reconozca entre ciclos (§4, F4).</summary>
    private const string LargeUnitTitle = "Unidad demasiado grande para auditar como una sola unidad";

    public ScanOutput Scan(string root, AppConfig config, int cycleN)
    {
        root = Path.GetFullPath(root);
        TechStack stack = config.Stack != TechStack.Unknown ? config.Stack : StackDetector.Detect(root);
        StackProfile profile = StackProfile.For(stack);

        var matcher = new ExclusionMatcher(DefaultExclusions.Patterns.Concat(config.Exclusions));
        List<string> files = WalkFiles(root, matcher).ToList();

        var moduleIndex = new ModuleIndex(files, profile);

        var inventory = new InventoryCycle { CycleN = cycleN };
        var largeFindings = new List<SubmittedFinding>();
        var modules = new HashSet<string>(StringComparer.Ordinal);

        foreach (string rel in files)
        {
            string fileName = Path.GetFileName(rel);
            if (!profile.IsSourceFile(fileName))
            {
                continue;
            }

            string abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes = File.ReadAllBytes(abs);
            int loc = CountLines(bytes);
            int chars = bytes.Length;
            string module = moduleIndex.ModuleFor(rel);
            modules.Add(module);

            bool isLarge = loc > config.Thresholds.LargeUnitLoc || chars > config.Thresholds.LargeUnitChars;

            inventory.Units.Add(new InventoryUnit
            {
                Path = rel,
                Module = module,
                Loc = loc,
                ContentHash = HashUtil.Sha256Hex(bytes),
                State = isLarge ? UnitState.Grande : UnitState.Pendiente,
            });

            if (isLarge)
            {
                largeFindings.Add(BuildLargeUnitFinding(rel, loc, config.Thresholds));
            }
        }

        inventory.Units.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ScanOutput(stack, inventory, largeFindings, modules.OrderBy(m => m, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Builds the auto refactor finding for an oversized unit. Title is constant and LOC lives
    /// only in the description, so the title the auditor reconciles against is stable
    /// across cycles even as the file grows.
    /// </summary>
    public static SubmittedFinding BuildLargeUnitFinding(string path, int loc, Thresholds thresholds)
        => new(
            RuleId: LargeUnitRuleId,
            Pillar: Pillar.Mejoras,
            Tag: FindingTag.Checklist,
            Severity: Severity.Media,
            Title: LargeUnitTitle,
            Description: $"La unidad '{path}' tiene {loc} LOC, por encima del umbral de "
                + $"{thresholds.LargeUnitLoc} LOC / {thresholds.LargeUnitChars} caracteres.",
            Impact: "Las unidades muy grandes no caben en una tanda de auditoría y concentran riesgo.",
            Recommendation: "Divide la unidad en piezas cohesivas y con responsabilidad única.",
            Locations: new[] { new Location(path, 1) },
            Symbol: null);

    private static IEnumerable<string> WalkFiles(string root, ExclusionMatcher matcher)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();

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
                if (!matcher.IsExcludedDirectoryName(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (string f in files)
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (!matcher.IsExcluded(rel))
                {
                    yield return rel;
                }
            }
        }
    }

    /// <summary>
    /// El LOC de un contenido, tal y como lo cuenta el escáner (D-009). Público porque la
    /// re-medición de F5.16 tiene que contar EXACTAMENTE igual: si el instrumento que crea el
    /// hallazgo y el que lo resuelve discreparan, una unidad podría salir de «Grandes» y quedarse
    /// con su hallazgo activo.
    /// </summary>
    public static int CountLinesOf(byte[] bytes) => CountLines(bytes);

    private static int CountLines(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return 0;
        }

        int lines = 1;
        foreach (byte b in bytes)
        {
            if (b == (byte)'\n')
            {
                lines++;
            }
        }

        // A trailing newline shouldn't inflate the count by one empty line.
        if (bytes[^1] == (byte)'\n')
        {
            lines--;
        }

        return lines;
    }

    /// <summary>Maps each unit to its nearest enclosing module (by manifest ancestry).</summary>
    private sealed class ModuleIndex
    {
        // (directory-with-trailing-slash, moduleName) sorted by descending length.
        private readonly List<(string Dir, string Name)> _manifests = new();

        public ModuleIndex(IEnumerable<string> files, StackProfile profile)
        {
            foreach (string rel in files)
            {
                string fileName = Path.GetFileName(rel);
                if (!profile.IsManifest(fileName))
                {
                    continue;
                }

                string dirRel = Dir(rel);
                string name = profile.Stack == TechStack.DotNet
                    ? Path.GetFileNameWithoutExtension(fileName)
                    : ModuleNameFromDir(dirRel);
                _manifests.Add((dirRel, name));
            }

            _manifests.Sort((a, b) => b.Dir.Length.CompareTo(a.Dir.Length));
        }

        public string ModuleFor(string rel)
        {
            string dirRel = Dir(rel);
            foreach ((string dir, string name) in _manifests)
            {
                if (dir.Length == 0 || dirRel == dir.TrimEnd('/') || dirRel.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            // No manifest ancestor — fall back to the top-level directory (or root).
            int slash = rel.IndexOf('/');
            return slash > 0 ? rel[..slash] : "(root)";
        }

        private static string Dir(string rel)
        {
            int slash = rel.LastIndexOf('/');
            return slash < 0 ? string.Empty : rel[..(slash + 1)];
        }

        private static string ModuleNameFromDir(string dirRel)
        {
            string trimmed = dirRel.TrimEnd('/');
            if (trimmed.Length == 0)
            {
                return "(root)";
            }

            int slash = trimmed.LastIndexOf('/');
            return slash < 0 ? trimmed : trimmed[(slash + 1)..];
        }
    }
}
