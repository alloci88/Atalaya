using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atalaya.App.Services;

/// <summary>Qué se compila: el proyecto de lo tocado, la solución entera, o nada.</summary>
public enum BuildTargetKind
{
    /// <summary>No hay nada compilable en el clon.</summary>
    None,

    /// <summary>El proyecto que contiene los ficheros tocados. Es el ámbito POR DEFECTO (H9.1).</summary>
    Project,

    /// <summary>La solución entera. Solo si el usuario lo pide, o si lo tocado no cae en un proyecto.</summary>
    Solution,
}

/// <summary>Lo que se le va a pasar a <c>dotnet</c>, y cómo se llama en castellano.</summary>
/// <param name="Kind">Proyecto, solución o nada.</param>
/// <param name="FullPath">Ruta absoluta del <c>.csproj</c> o del <c>.sln</c>.</param>
/// <param name="Relative">La misma ruta, relativa al clon: es lo que se enseña.</param>
public sealed record BuildTarget(BuildTargetKind Kind, string FullPath, string Relative)
{
    public static BuildTarget None { get; } = new(BuildTargetKind.None, string.Empty, string.Empty);

    /// <summary>«el proyecto Common/Common.csproj» — se lee dentro de una frase.</summary>
    public string Label => Kind switch
    {
        BuildTargetKind.Project => $"el proyecto {Relative}",
        BuildTargetKind.Solution => $"la solución {Relative}",
        _ => "nada",
    };
}

/// <summary>
/// El plan de una compilación: qué se compila, qué tests se pasan, qué queda fuera y por qué.
/// </summary>
/// <param name="Target">Proyecto o solución.</param>
/// <param name="TestProjects">
/// Proyectos de test que cubren el objetivo, en rutas relativas. Vacío no es un fallo: hay
/// proyectos sin tests, y decirlo es más honesto que ejecutar la solución entera «por si acaso».
/// </param>
/// <param name="ExcludedProjects">
/// Proyectos del clon que <c>dotnet build</c> no puede compilar (C++ y compañía). No se cuentan
/// como fallo: se nombran.
/// </param>
/// <param name="Note">Por qué el ámbito es este, cuando no es el obvio.</param>
public sealed record BuildPlan(
    BuildTarget Target,
    IReadOnlyList<string> TestProjects,
    IReadOnlyList<string> ExcludedProjects,
    string? Note)
{
    /// <summary>La solución entera pide línea base: ahí dentro hay código que no es del cambio.</summary>
    public bool WantsBaseline => Target.Kind != BuildTargetKind.None;
}

/// <summary>
/// Decide QUÉ se compila a partir de lo que el agente ha tocado (H9.1 §2).
/// <para>
/// <b>Por defecto, el proyecto de los ficheros tocados.</b> Antes se compilaba la solución entera,
/// y en una solución legacy que ya no compilaba —lo normal en el código que Atalaya audita— el
/// veredicto hablaba de todo menos del cambio: 18 errores, ninguno del arreglo. Un veredicto que
/// no se puede atribuir no es un veredicto.
/// </para>
/// <para>
/// La solución entera sigue disponible, pero es una decisión del USUARIO, no del agente: es él
/// quien sabe si su cambio puede haber roto a un vecino, y quien paga el tiempo de averiguarlo.
/// </para>
/// </summary>
public static class BuildScopeResolver
{
    /// <summary>Lo que <c>dotnet build</c> sabe compilar.</summary>
    public static readonly string[] DotnetProjectExtensions = { ".csproj", ".fsproj", ".vbproj" };

    /// <summary>
    /// Lo que NO sabe compilar, y que hay que nombrar en vez de contar como fallo. El caso que dio
    /// origen a esto es <c>.vcxproj</c>: <c>dotnet build</c> muere con MSB4019 buscando
    /// <c>Microsoft.Cpp.Default.props</c>, que solo trae el toolset de Visual Studio.
    /// </summary>
    public static readonly string[] ForeignProjectExtensions =
        { ".vcxproj", ".vcproj", ".njsproj", ".pyproj", ".sqlproj", ".wapproj", ".jsproj", ".wixproj" };

    /// <summary>Cuántos ficheros de proyecto se miran como mucho. Un clon enorme no bloquea la tool.</summary>
    public const int MaxProjectsScanned = 400;

    public static BuildPlan Resolve(
        string cloneRoot, IReadOnlyList<string> touchedFiles, bool fullSolution)
    {
        string? solution = BuildRunner.FindSolution(cloneRoot);
        IReadOnlyList<string> projects = AllProjects(cloneRoot);
        List<string> foreign = projects
            .Where(IsForeign)
            .Select(p => Relative(cloneRoot, p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (!fullSolution && touchedFiles.Count > 0)
        {
            List<string> owners = touchedFiles
                .Select(f => OwningProject(cloneRoot, f))
                .Where(p => p is not null)
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<string> buildable = owners.Where(p => !IsForeign(p)).ToList();

            if (buildable.Count == 1 && buildable.Count == owners.Count)
            {
                string project = buildable[0];
                return new BuildPlan(
                    new BuildTarget(BuildTargetKind.Project, project, Relative(cloneRoot, project)),
                    TestProjectsFor(cloneRoot, project, projects),
                    foreign,
                    Note: null);
            }

            if (owners.Count > 1 && solution is not null)
            {
                return Solution(cloneRoot, solution, projects, foreign,
                    $"el cambio toca {owners.Count} proyectos, así que se compila la solución");
            }

            if (owners.Count == 1 && IsForeign(owners[0]))
            {
                return new BuildPlan(
                    BuildTarget.None, Array.Empty<string>(), foreign,
                    $"{Relative(cloneRoot, owners[0])} requiere el toolset de Visual Studio; "
                    + "dotnet no puede compilarlo y Atalaya no lo intenta");
            }

            if (solution is not null)
            {
                return Solution(cloneRoot, solution, projects, foreign,
                    "los ficheros tocados no caen dentro de ningún proyecto, así que se compila la solución");
            }
        }

        if (solution is not null)
        {
            return Solution(cloneRoot, solution, projects, foreign, note: null);
        }

        // Sin solución todavía puede haber UN proyecto suelto: mejor eso que nada.
        string? single = projects.FirstOrDefault(p => !IsForeign(p));
        return single is not null && projects.Count(p => !IsForeign(p)) == 1
            ? new BuildPlan(
                new BuildTarget(BuildTargetKind.Project, single, Relative(cloneRoot, single)),
                TestProjectsFor(cloneRoot, single, projects),
                foreign,
                "no hay solución (.sln) en el clon; se compila el único proyecto que hay")
            : new BuildPlan(BuildTarget.None, Array.Empty<string>(), foreign, Note: null);
    }

    private static BuildPlan Solution(
        string cloneRoot, string solution, IReadOnlyList<string> projects,
        IReadOnlyList<string> foreign, string? note)
        => new(
            new BuildTarget(BuildTargetKind.Solution, solution, Relative(cloneRoot, solution)),
            Array.Empty<string>(),   // la solución ya arrastra sus propios tests
            foreign,
            note);

    /// <summary>
    /// El proyecto que contiene un fichero: el <c>.csproj</c> más cercano subiendo por el árbol.
    /// Es la misma regla que usa MSBuild para saber de quién es un fichero, y no necesita leer
    /// nada: la carpeta manda.
    /// </summary>
    public static string? OwningProject(string cloneRoot, string relativeFile)
    {
        string root = Path.GetFullPath(cloneRoot);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relativeFile.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return null;
        }

        var dir = new DirectoryInfo(Path.GetDirectoryName(full) ?? root);
        while (dir is not null && dir.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string? project = SafeEnumerate(dir.FullName)
                .Where(p => IsProjectFile(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (project is not null)
            {
                return project;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Los proyectos de test que cubren a este: el propio proyecto si lo es, y los que le apuntan
    /// con un <c>ProjectReference</c>. Es lo que convierte «compilo el proyecto tocado» en «paso
    /// los tests de lo que he tocado», que es lo único que el veredicto puede prometer.
    /// </summary>
    public static IReadOnlyList<string> TestProjectsFor(
        string cloneRoot, string project, IReadOnlyList<string> allProjects)
    {
        var found = new List<string>();
        if (IsTestProject(project))
        {
            found.Add(project);
        }

        string name = Path.GetFileName(project);
        foreach (string candidate in allProjects)
        {
            if (string.Equals(candidate, project, StringComparison.OrdinalIgnoreCase)
                || IsForeign(candidate)
                || !IsTestProject(candidate))
            {
                continue;
            }

            string text = SafeRead(candidate);
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(candidate);
            }
        }

        return found
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Un proyecto de test se declara: trae el SDK de tests o lo dice con todas las letras.</summary>
    public static bool IsTestProject(string projectPath)
    {
        string text = SafeRead(projectPath);
        return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsForeign(string path)
        => ForeignProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsProjectFile(string path)
        => DotnetProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
           || IsForeign(path);

    /// <summary>Todos los ficheros de proyecto del clon, en orden estable y con tope.</summary>
    public static IReadOnlyList<string> AllProjects(string cloneRoot)
    {
        if (string.IsNullOrWhiteSpace(cloneRoot) || !Directory.Exists(cloneRoot))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(cloneRoot, "*.*proj", SearchOption.AllDirectories)
                .Where(IsProjectFile)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(MaxProjectsScanned)
                .ToList();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.*proj", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    internal static string Relative(string root, string file)
    {
        try
        {
            return Path.GetRelativePath(root, file).Replace('\\', '/');
        }
        catch
        {
            return file;
        }
    }
}

/// <summary>Un error de compilación, ya separado de la línea que lo traía.</summary>
/// <param name="Code">El código de MSBuild o del compilador: <c>CS1002</c>, <c>MSB4019</c>.</param>
/// <param name="Line">La línea entera, tal y como la escribió dotnet. Es lo que se enseña.</param>
/// <param name="Signature">
/// La forma normalizada con la que se compara contra la línea base: sin rutas absolutas, sin
/// número de columna y en minúsculas. Dos ejecuciones del MISMO error tienen que dar la misma
/// firma, o el delta contaría como nuevo lo que ya estaba.
/// </param>
/// <param name="Foreign">Viene de un proyecto que dotnet no puede compilar: no cuenta como fallo.</param>
public sealed record BuildError(string Code, string Line, string Signature, bool Foreign);

/// <summary>
/// Saca los errores de la salida de <c>dotnet build</c> (H9.1 §2).
/// <para>
/// No se parsea para adivinar nada: se parsea para poder COMPARAR. Un error solo se puede
/// atribuir al cambio si se sabe que antes no estaba, y eso exige una firma estable.
/// </para>
/// </summary>
public static partial class BuildErrorParser
{
    /// <summary>Tope de errores que se conservan. Una solución rota de verdad no cabe en un informe.</summary>
    public const int MaxErrors = 200;

    private static readonly Regex ErrorLine = new(
        @"(?<code>(?:[A-Z]{2,8}\d{2,6}))\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<BuildError> Parse(string? output, string cloneRoot)
    {
        var found = new List<BuildError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string raw in (output ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || !line.Contains(" error ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match m = ErrorLine.Match(line);
            string code = m.Success ? m.Groups["code"].Value : "?";
            string signature = Signature(line, cloneRoot);
            if (!seen.Add(signature))
            {
                continue;   // dotnet repite el mismo error una vez por objetivo; cuenta una.
            }

            found.Add(new BuildError(code, line, signature, IsForeign(line, code)));
            if (found.Count >= MaxErrors)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// El error no es del código .NET: es de un proyecto que <c>dotnet build</c> no sabe abrir.
    /// <c>MSB4019</c> (el <c>Microsoft.Cpp.Default.props</c> que solo trae Visual Studio) y
    /// cualquier línea que nombre un proyecto de los ajenos.
    /// </summary>
    private static bool IsForeign(string line, string code)
        => string.Equals(code, "MSB4019", StringComparison.OrdinalIgnoreCase)
           || BuildScopeResolver.ForeignProjectExtensions.Any(
               ext => line.Contains(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Firma estable: fuera la ruta del clon, fuera la columna, fuera el proyecto entre corchetes
    /// —que cambia con el objetivo— y todo a minúsculas.
    /// </summary>
    internal static string Signature(string line, string cloneRoot)
    {
        string text = line;
        if (!string.IsNullOrWhiteSpace(cloneRoot))
        {
            text = text.Replace(cloneRoot, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        text = Bracketed().Replace(text, string.Empty);
        text = Position().Replace(text, "(#)");
        text = Spaces().Replace(text, " ");
        return text.Trim().Replace('\\', '/').ToLowerInvariant();
    }

    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\(\d+,\d+\)")]
    private static partial Regex Position();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}

/// <summary>Lo que se sabía del objetivo ANTES de tocar nada, sobre este mismo commit.</summary>
/// <param name="Target">Ruta relativa del proyecto o de la solución.</param>
/// <param name="Commit">El commit del clon. Cambiar de commit invalida la línea base.</param>
/// <param name="CapturedUtc">Cuándo se midió; el informe lo dice.</param>
/// <param name="Ok">Compilaba limpio.</param>
/// <param name="Signatures">Las firmas de los errores que YA había.</param>
public sealed record BuildBaseline(
    string Target, string Commit, DateTimeOffset CapturedUtc, bool Ok, IReadOnlyList<string> Signatures);

/// <summary>
/// Guarda la línea base por (objetivo, commit) para no volver a medirla (H9.1 §2).
/// <para>
/// La precondición del arreglo asistido —árbol limpio— es justo lo que la hace reutilizable: si
/// dos sesiones arrancan sobre el mismo commit, lo que la solución hacía antes de tocarla es lo
/// mismo. Vive fuera del clon, con los snapshots, porque medir el clon no puede ensuciarlo.
/// </para>
/// </summary>
public sealed class BuildBaselineStore
{
    private readonly string _root;

    public BuildBaselineStore(AppPaths paths) => _root = paths.BuildBaselines;

    public BuildBaseline? TryGet(string target, string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            return null;
        }

        string file = FileFor(target, commit!);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            BuildBaseline? baseline = JsonSerializer.Deserialize<BuildBaseline>(File.ReadAllText(file));
            return baseline is not null
                   && string.Equals(baseline.Commit, commit, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(baseline.Target, target, StringComparison.OrdinalIgnoreCase)
                ? baseline
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(BuildBaseline baseline)
    {
        if (string.IsNullOrWhiteSpace(baseline.Commit))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(
                FileFor(baseline.Target, baseline.Commit),
                JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // Sin caché se vuelve a medir: es lento, no incorrecto.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string FileFor(string target, string commit)
        => Path.Combine(_root, Key(target, commit) + ".json");

    /// <summary>
    /// Nombre de fichero seguro y estable. La ruta del objetivo lleva barras y dos puntos, así que
    /// va por hash; el commit va delante en claro para poder mirar la carpeta y entenderla.
    /// </summary>
    internal static string Key(string target, string commit)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(target.Replace('\\', '/').ToLowerInvariant()));
        string shortCommit = commit.Length > 12 ? commit[..12] : commit;
        return $"{shortCommit}-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
}
