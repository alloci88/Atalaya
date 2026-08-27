using System.Diagnostics;
using Atalaya.Domain.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atalaya.App.Services;

/// <summary>Con cuánta certeza se encontró una referencia. Viaja al prompt: nunca se finge.</summary>
public enum ReferencePrecision
{
    /// <summary>
    /// Árbol sintáctico de C#: comentarios, documentación y cadenas quedan fuera. No resuelve
    /// tipos, así que un miembro del mismo nombre en otro tipo puede colarse — y se dice.
    /// </summary>
    Sintaxis,

    /// <summary>Búsqueda del nombre en el texto. Aproximada, y el prompt lo ETIQUETA.</summary>
    Texto,
}

/// <summary>Un sitio donde se usa el código afectado.</summary>
/// <param name="Path">Ruta relativa al clon, con barras normales.</param>
/// <param name="Line">Línea 1-based.</param>
/// <param name="Member">El miembro que contiene la llamada, o <c>null</c> si no se supo derivar.</param>
/// <param name="Text">La línea de la llamada, recortada. Una sola línea de contexto (§1).</param>
public sealed record ReferenceSite(string Path, int Line, string? Member, string Text);

/// <summary>Lo que se pudo averiguar sobre quién usa el código de un hallazgo.</summary>
/// <param name="Symbols">Los nombres que se buscaron.</param>
/// <param name="Sites">Los sitios listados, ya recortados al tope.</param>
/// <param name="Total">Cuántos había en total (puede ser mayor que los listados).</param>
/// <param name="OverflowAreas">Los proyectos donde viven los que no se listan.</param>
/// <param name="Unavailable">
/// Por qué no se pudo mirar. Cuando lo hay, el resto del informe no significa nada: el prompt
/// dice que va SIN referencias en vez de dejar entender que no hay ninguna.
/// </param>
public sealed record ReferenceReport(
    IReadOnlyList<string> Symbols,
    IReadOnlyList<ReferenceSite> Sites,
    int Total,
    ReferencePrecision Precision,
    bool TimedOut,
    IReadOnlyList<string> OverflowAreas,
    string? Unavailable = null)
{
    public static ReferenceReport NotCollected(string reason) => new(
        Array.Empty<string>(), Array.Empty<ReferenceSite>(), 0,
        ReferencePrecision.Texto, TimedOut: false, Array.Empty<string>(), reason);

    /// <summary>Se pudo mirar. Que el total sea 0 es un resultado, no un fallo.</summary>
    public bool Collected => Unavailable is null;

    /// <summary>Cuántos sitios existen y no se listan.</summary>
    public int Hidden => Math.Max(0, Total - Sites.Count);
}

/// <summary>Los topes de la recolección (§3). Configurables; nunca ausentes.</summary>
/// <param name="Time">
/// Presupuesto de reloj. Al agotarse se corta con lo que haya y se DICE: generar el prompt nunca
/// puede tardar minutos.
/// </param>
/// <param name="MaxSites">Cuántos sitios se listan como mucho.</param>
/// <param name="MaxChars">
/// Tope del tamaño de la sección de referencias, en caracteres (~2-3k tokens). Es el cinturón del
/// tope de sitios: treinta líneas larguísimas también se pasan de presupuesto.
/// </param>
public sealed record ReferenceBudget(TimeSpan Time, int MaxSites, int MaxChars)
{
    public static readonly ReferenceBudget Default = new(TimeSpan.FromSeconds(25), 30, 9000);
}

/// <summary>
/// <b>Quién usa el código de un hallazgo</b> (F6.8 §1). Servicio reutilizable: hoy alimenta el
/// prompt de arreglo, y el arreglo integrado (H9) hereda exactamente esta recolección.
/// <para>
/// <b>El problema que resuelve.</b> Un agente al que se le enseña SOLO el método afectado aplica
/// un arreglo localmente correcto —una validación, una excepción nueva, un contrato distinto— sin
/// saber quién llama a ese método, y rompe un proceso aguas arriba. El caso real: añadir una
/// excepción a un método que antes truncaba en silencio rompe a todo llamador que dependiera del
/// truncado. El arreglo es que el prompt VIAJE con sus referencias.
/// </para>
/// <para>
/// <b>Por qué el árbol sintáctico y no la solución cargada.</b> Se evaluó
/// <c>MSBuildWorkspace</c> + <c>FindReferences</c>, que es lo exacto: resuelve tipos y distingue
/// dos miembros con el mismo nombre. Pero exige cargar y RESTAURAR una solución ajena —docenas de
/// proyectos, a menudo .NET Framework, a menudo sin paquetes restaurados en el clon—, y una
/// solución que no compila devuelve símbolos sin resolver, es decir, cero referencias
/// silenciosas: el peor resultado posible, porque «no tiene llamadores» es justo la frase que
/// autoriza a cambiar el contrato. El analizador sintáctico, en cambio, tolera ficheros que no
/// compilan (igual que <see cref="MethodBoundary"/>), no necesita proyectos ni paquetes
/// restaurados, y descarta comentarios, documentación y cadenas, que es de donde salen los falsos
/// positivos de una búsqueda de texto. Lo que no hace —resolver tipos— se DECLARA en el prompt.
/// </para>
/// <para>
/// <b>Solo un nivel.</b> Llamadores directos. El impacto de segundo orden se menciona como frase
/// en el prompt; calcularlo es lo que convierte esto en un barrido de la solución entera.
/// </para>
/// </summary>
public sealed class ReferenceCollector
{
    /// <summary>Cuántos nombres distintos se buscan como mucho. Más es un barrido, no una consulta.</summary>
    public const int MaxSymbols = 3;

    /// <summary>La línea de la llamada se recorta aquí: es contexto, no el fichero.</summary>
    public const int MaxLineChars = 160;

    /// <summary>
    /// Tope duro de la lista cruda antes de ordenar. Un nombre desafortunado (<c>Get</c>, <c>Id</c>)
    /// puede tener decenas de miles de usos; el total se sigue contando por encima de esto.
    /// </summary>
    private const int MaxRawHits = 5000;

    /// <summary>Carpetas que nunca contienen fuentes del proyecto: no se entra.</summary>
    private static readonly HashSet<string> PrunedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".svn", ".hg", "node_modules", "packages", ".vs", ".idea",
        "dist", "out", "target", ".venv", "venv", "__pycache__", "coverage", ".nuget",
    };

    /// <summary>Extensiones que se miran en el camino textual (el de los stacks que no son C#).</summary>
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".vb", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go", ".rb",
        ".php", ".c", ".h", ".cpp", ".hpp", ".cc", ".kt", ".swift", ".rs", ".fs", ".scala",
        ".sql", ".cshtml", ".razor", ".vue", ".m", ".mm",
    };

    /// <summary>
    /// Los sitios donde se usa el código de <paramref name="finding"/>, dentro del clon local.
    /// Nunca lanza: no poder mirar es un resultado (<see cref="ReferenceReport.Unavailable"/>), no
    /// una excepción que tumbe la generación del prompt.
    /// </summary>
    public ReferenceReport Collect(
        string? clonePath,
        Finding finding,
        ReferenceBudget? budget = null,
        CancellationToken cancellation = default)
    {
        ReferenceBudget limits = budget ?? ReferenceBudget.Default;

        if (string.IsNullOrWhiteSpace(clonePath) || !Directory.Exists(clonePath))
        {
            return ReferenceReport.NotCollected(
                "no hay un clon local de esta aplicación vinculado en esta máquina");
        }

        try
        {
            IReadOnlyList<string> symbols = TargetSymbols(clonePath!, finding);
            if (symbols.Count == 0)
            {
                return ReferenceReport.NotCollected(
                    "el hallazgo no nombra ningún símbolo concreto que poder buscar");
            }

            return Scan(clonePath!, finding, symbols, limits, cancellation);
        }
        catch (OperationCanceledException)
        {
            return ReferenceReport.NotCollected("la recolección se canceló");
        }
        catch (Exception ex)
        {
            return ReferenceReport.NotCollected($"la recolección falló ({ex.GetType().Name})");
        }
    }

    // ------------------------------------------------------------------ qué se busca

    /// <summary>Los separadores con los que un auditor escribe VARIOS símbolos en un campo.</summary>
    private static readonly char[] SymbolListSeparators = { ',', '/', ';', '|', '+' };

    /// <summary>
    /// Los nombres a buscar, por fuentes en orden de fiabilidad. <b>Se usa una sola fuente</b>, la
    /// primera que dé algo: el <c>symbol</c> que declaró el auditor (D-223), si no el miembro que
    /// de verdad contiene cada ubicación en el clon de hoy, y si no los identificadores fuertes
    /// del título.
    /// <para>
    /// <b>Por qué una sola y no la unión.</b> Se probó a sumarlas contra el X-BLAST real y salió
    /// mal: BUG-0002 declara <c>StringToByteArray</c>, pero su línea guardada ya no cae dentro de
    /// ese método —el fichero se ha editado desde la auditoría— y la ubicación aportaba
    /// <c>ReadCSV</c>. La lista resultante traía nueve llamadores de los cuales ocho eran de otro
    /// método completamente distinto. Una lista de llamadores diluida es peor que una corta: el
    /// agente revisa ocho sitios que no le importan y se fía de un conjunto que no es el suyo.
    /// </para>
    /// <para>
    /// Del símbolo declarado se toma <b>el miembro de cada entrada</b>, no el tipo:
    /// <c>CommonStatics.StringToByteArray</c> busca el método, y
    /// <c>DateToByteArray,TimeToByteArray</c> —dos miembros hermanos en un hallazgo— busca los dos.
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> TargetSymbols(string clonePath, Finding finding)
    {
        var names = new List<string>();

        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && names.Count < MaxSymbols
                && !names.Contains(name!, StringComparer.Ordinal))
            {
                names.Add(name!);
            }
        }

        foreach (string entry in (finding.Symbol ?? string.Empty)
            .Split(SymbolListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            Add(SymbolAnchor.Candidates(entry, title: null).FirstOrDefault());
        }

        if (names.Count > 0)
        {
            return names;
        }

        foreach (Atalaya.Domain.Model.Location loc in finding.Locations.Take(MaxSymbols))
        {
            Add(MemberAt(clonePath, loc));
        }

        if (names.Count > 0)
        {
            return names;
        }

        foreach (string guess in SymbolAnchor.Candidates(symbol: null, finding.Title).Take(2))
        {
            Add(guess);
        }

        return names;
    }

    /// <summary>El nombre a secas del miembro que contiene una ubicación, leído del clon.</summary>
    private static string? MemberAt(string clonePath, Atalaya.Domain.Model.Location loc)
    {
        if (!MethodBoundary.IsCSharp(loc.Path))
        {
            return null;
        }

        string full = Path.Combine(clonePath, loc.Path.Replace('/', Path.DirectorySeparatorChar));
        string[]? lines = TryReadLines(full);
        if (lines is null)
        {
            return null;
        }

        string? member = MethodBoundary.ForLine(lines, loc.Line, loc.Path).Member;
        return member is null ? null : member[(member.LastIndexOf('.') + 1)..];
    }

    // ------------------------------------------------------------------ el barrido

    private ReferenceReport Scan(
        string root,
        Finding finding,
        IReadOnlyList<string> symbols,
        ReferenceBudget limits,
        CancellationToken cancellation)
    {
        // El camino sintáctico es para C#. Un hallazgo de otro stack cae al textual, ETIQUETADO:
        // media lista bien encontrada y media adivinada no puede presentarse como una sola cosa.
        bool csharp = MethodBoundary.IsCSharp(finding.Locations.FirstOrDefault()?.Path ?? string.Empty);
        ReferencePrecision precision = csharp ? ReferencePrecision.Sintaxis : ReferencePrecision.Texto;

        var hits = new List<ReferenceSite>();
        int total = 0;
        bool timedOut = false;
        var clock = Stopwatch.StartNew();

        foreach (string relative in WalkSources(root, csharp))
        {
            cancellation.ThrowIfCancellationRequested();
            if (clock.Elapsed > limits.Time)
            {
                timedOut = true;
                break;
            }

            // El filtro va sobre el TEXTO, antes de partirlo en líneas: en una solución de verdad
            // el nombre no aparece en el 99 % de los ficheros, y partir cada uno de ellos en un
            // array de cadenas para nada es lo que convertía el barrido en diez segundos.
            string? text = TryReadText(
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (text is null || !MentionsAny(text, symbols))
            {
                continue;
            }

            string[] lines = SplitLines(text);
            List<ReferenceSite> found = csharp
                ? SyntaxHits(relative, lines, symbols)
                : TextHits(relative, lines, symbols);

            total += found.Count;
            if (hits.Count < MaxRawHits)
            {
                hits.AddRange(found.Take(MaxRawHits - hits.Count));
            }
        }

        // Orden estable: el tope recorta SIEMPRE la misma lista, no la que dictó el orden del
        // sistema de ficheros. Dos generaciones del mismo prompt tienen que decir lo mismo.
        hits.Sort(static (a, b) =>
        {
            int byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });

        (List<ReferenceSite> listed, List<string> overflow) = ApplyCaps(root, hits, limits);
        return new ReferenceReport(symbols, listed, total, precision, timedOut, overflow);
    }

    /// <summary>
    /// El tope de sitios y el de tamaño, en ese orden. Lo que se queda fuera no desaparece: se
    /// cuenta y se dice en qué proyectos vive.
    /// </summary>
    private static (List<ReferenceSite> Listed, List<string> Overflow) ApplyCaps(
        string root, List<ReferenceSite> hits, ReferenceBudget limits)
    {
        var listed = new List<ReferenceSite>();
        int chars = 0;

        foreach (ReferenceSite site in hits)
        {
            int cost = site.Path.Length + (site.Member?.Length ?? 0) + site.Text.Length + 16;
            if (listed.Count >= limits.MaxSites || (listed.Count > 0 && chars + cost > limits.MaxChars))
            {
                break;
            }

            listed.Add(site);
            chars += cost;
        }

        var areas = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = listed.Count; i < hits.Count; i++)
        {
            areas.Add(AreaOf(root, hits[i].Path, cache));
        }

        return (listed, areas.ToList());
    }

    // ------------------------------------------------------------------ los dos caminos

    /// <summary>
    /// Usos del nombre según el árbol de C#. Solo cuentan los <see cref="SimpleNameSyntax"/>: la
    /// DECLARACIÓN del miembro no lo es —su nombre es un token de la declaración—, así que el
    /// método afectado no se cuenta como llamador de sí mismo. Y <c>DescendantNodes</c> no baja a
    /// la trivia, de modo que la documentación XML y los comentarios quedan fuera solos.
    /// </summary>
    private static List<ReferenceSite> SyntaxHits(string path, string[] lines, IReadOnlyList<string> symbols)
    {
        var sites = new List<ReferenceSite>();
        try
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(string.Join("\n", lines));
            SyntaxNode root = tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                if (node is not SimpleNameSyntax name
                    || !symbols.Contains(name.Identifier.Text, StringComparer.Ordinal))
                {
                    continue;
                }

                int line = tree.GetLineSpan(name.Span).StartLinePosition.Line + 1;
                if (line >= 1 && line <= lines.Length)
                {
                    sites.Add(new ReferenceSite(
                        path, line, SymbolAnchor.ContainingMember(node), Trim(lines[line - 1])));
                }
            }
        }
        catch (Exception)
        {
            // Un parser que se cae no puede tumbar la generación del prompt: este fichero no
            // aporta sitios y los demás siguen.
            return sites;
        }

        return sites;
    }

    /// <summary>
    /// El plan B: el nombre como palabra completa en el texto. Aproximado a sabiendas —el prompt
    /// lo etiqueta—, porque media referencia encontrada vale más que ninguna.
    /// </summary>
    private static List<ReferenceSite> TextHits(string path, string[] lines, IReadOnlyList<string> symbols)
    {
        var sites = new List<ReferenceSite>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (symbols.Any(s => ContainsWord(lines[i], s)))
            {
                sites.Add(new ReferenceSite(path, i + 1, null, Trim(lines[i])));
            }
        }

        return sites;
    }

    /// <summary>El nombre como palabra, no como trozo: <c>Parse</c> no casa dentro de <c>Parser</c>.</summary>
    internal static bool ContainsWord(string line, string word)
    {
        int from = 0;
        while (word.Length > 0 && from <= line.Length - word.Length)
        {
            int at = line.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            bool leftFree = at == 0 || !IsWordChar(line[at - 1]);
            int after = at + word.Length;
            bool rightFree = after >= line.Length || !IsWordChar(line[after]);
            if (leftFree && rightFree)
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ------------------------------------------------------------------ ficheros

    /// <summary>
    /// Los fuentes del clon, en rutas relativas con barras normales. Se podan las carpetas de
    /// compilación y de dependencias; los <b>tests NO se excluyen</b>, al contrario que en el
    /// inventario: un test que llama al método es exactamente un llamador que hay que mirar.
    /// </summary>
    private static IEnumerable<string> WalkSources(string root, bool csharpOnly)
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
            catch (Exception)
            {
                continue;
            }

            foreach (string sub in subdirs)
            {
                if (!PrunedDirectories.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files)
            {
                bool wanted = csharpOnly
                    ? MethodBoundary.IsCSharp(file)
                    : SourceExtensions.Contains(Path.GetExtension(file));
                if (wanted)
                {
                    yield return Path.GetRelativePath(root, file).Replace('\\', '/');
                }
            }
        }
    }

    /// <summary>
    /// El filtro barato de antes de parsear: si el nombre no aparece ni como texto, el fichero no
    /// puede tener una referencia. Es lo que hace que una solución de cientos de miles de líneas
    /// se recorra en un par de segundos en vez de en minutos.
    /// </summary>
    private static bool MentionsAny(string text, IReadOnlyList<string> symbols)
    {
        foreach (string symbol in symbols)
        {
            if (text.Contains(symbol, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryReadText(string fullPath)
    {
        try
        {
            return File.ReadAllText(fullPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string[]? TryReadLines(string fullPath)
    {
        string? text = TryReadText(fullPath);
        return text is null ? null : SplitLines(text);
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Trim(string line)
    {
        string t = line.Trim();
        return t.Length <= MaxLineChars ? t : t[..MaxLineChars] + "…";
    }

    /// <summary>
    /// En qué proyecto vive un fichero: el <c>.csproj</c>/<c>.vbproj</c> más cercano hacia arriba.
    /// Es lo que se dice de los sitios que no se listan («y 17 más en X, Y»), así que tiene que ser
    /// un nombre que el humano reconozca, no un prefijo de ruta.
    /// </summary>
    private static string AreaOf(string root, string relativePath, Dictionary<string, string> cache)
    {
        string dir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        if (cache.TryGetValue(dir, out string? known))
        {
            return known;
        }

        string area = ProbeArea(root, dir);
        cache[dir] = area;
        return area;
    }

    private static string ProbeArea(string root, string dir)
    {
        string current = dir;
        while (true)
        {
            try
            {
                string absolute = current.Length == 0
                    ? root
                    : Path.Combine(root, current.Replace('/', Path.DirectorySeparatorChar));
                string? project = Directory.EnumerateFiles(absolute, "*.csproj").FirstOrDefault()
                    ?? Directory.EnumerateFiles(absolute, "*.vbproj").FirstOrDefault();
                if (project is not null)
                {
                    return Path.GetFileNameWithoutExtension(project);
                }
            }
            catch (Exception)
            {
                // Una carpeta ilegible no interrumpe la subida.
            }

            if (current.Length == 0)
            {
                break;
            }

            int slash = current.LastIndexOf('/');
            current = slash < 0 ? string.Empty : current[..slash];
        }

        // Sin proyecto por encima, la primera carpeta de la ruta es lo más parecido a un área.
        int first = dir.IndexOf('/');
        return dir.Length == 0 ? "la raíz del repositorio" : (first < 0 ? dir : dir[..first]);
    }
}
