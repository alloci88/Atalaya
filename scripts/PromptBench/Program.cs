using System.Diagnostics;
using System.Globalization;
using Atalaya.Agents;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.PromptBench;

// ---------------------------------------------------------------------------------------------
// EL BANCO DE MEDIDA DE F18 — «medir antes, cambiar, medir después»
//
// Existe porque una fase que se juzga con números necesita que esos números se puedan volver a
// sacar dentro de seis meses, con una orden y sin montar nada. Reutiliza EL CÓDIGO DE PRODUCCIÓN
// —PromptComposer y ClaudeCodeProvider, los mismos que usa la aplicación—, así que lo que mide es
// lo que pasa de verdad y no una maqueta que se queda vieja a la primera.
//
//   PromptBench composicion [unidades...]      offline y gratis: de qué está hecho cada prompt
//   PromptBench claude --whole [unidades...]   una pasada real con el prompt entero por stdin
//   PromptBench claude --split [unidades...]   una pasada real con el prefijo estable cacheado
//
// El escenario por defecto son DOS unidades pequeñas de este mismo repositorio, elegidas por
// tamaño parecido al del banco de pruebas de la línea base (una clase de ~40 líneas). Se pueden
// dar otras por argumento; lo que no se puede es comparar dos ejecuciones con unidades distintas.
// ---------------------------------------------------------------------------------------------

string[] argv = args;
string mode = argv.Length > 0 && !argv[0].StartsWith("--", StringComparison.Ordinal) ? argv[0] : "composicion";
bool split = argv.Contains("--split");
string? model = Flag(argv, "--model") ?? "sonnet";
AuditTheme theme = Enum.TryParse(Flag(argv, "--tema"), true, out AuditTheme t) ? t : AuditTheme.General;
// Cuántos hallazgos conocidos lleva la unidad. Sin esto todas las medidas serían de una PRIMERA
// pasada, que es el caso barato: en una segunda el auditor además tiene que reconciliar, y es ahí
// donde se ve si agrupa sus herramientas en un turno o gasta una vuelta por cada cosa (F19 §1).
int existing = int.TryParse(Flag(argv, "--existentes"), out int n) ? Math.Max(0, n) : 0;

string root = RepoRoot();
// Los valores de las opciones (--model sonnet) NO son unidades: sin esto, «sonnet» acabaría
// buscándose como fichero. Se descartan la opción y lo que va detrás de ella.
var reserved = new HashSet<string>(StringComparer.Ordinal);
for (int k = 0; k < argv.Length; k++)
{
    if (argv[k] is "--model" or "--tema" or "--existentes" && k + 1 < argv.Length)
    {
        reserved.Add(argv[k + 1]);
    }
}

var units = argv
    .Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a != mode && !reserved.Contains(a))
    .ToList();
if (units.Count == 0)
{
    units = new List<string>
    {
        "src/Atalaya.Domain/Hashing/Hashing.cs",
        "src/Atalaya.App/Services/AxisScale.cs",
    };
}

Console.WriteLine($"Banco de medida · modo {mode}{(mode == "claude" ? (split ? " --split" : " --whole") : "")}");
Console.WriteLine($"Raíz: {root}");
Console.WriteLine($"Temática: {theme} · unidades: {units.Count} · hallazgos conocidos: {existing}");
Console.WriteLine();

AuditorBrief brief = PillarBrief.Parts(TechStack.DotNet);
var known = Enumerable.Range(1, existing)
    .Select(i => new ExistingFinding(
        $"01JBENCH00000000000000000{i:D1}",
        $"BUG-{i:D4}",
        $"Hallazgo conocido {i} sembrado por el banco de medida",
        i % 2 == 0 ? "alta" : "media",
        $"{units[0]}:{i}",
        "activo",
        "General"))
    .ToList();
var composed = new List<(string Path, string Content, ComposedUnitPrompt Prompt)>();
foreach (string relative in units)
{
    string absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(absolute))
    {
        Console.Error.WriteLine($"No existe: {absolute}");
        return 2;
    }

    string content = File.ReadAllText(absolute);
    composed.Add((relative, content, PromptComposer.Compose(
        relative, content, brief, AuditMode.Lotes, known, null, null, theme, null)));
}

return mode switch
{
    "composicion" => Composicion(composed),
    "claude" => await ClaudeAsync(composed, split, model),
    _ => Uso(),
};

static int Uso()
{
    Console.Error.WriteLine(
        "Uso: PromptBench [composicion|claude] [--split|--whole] [--model X] [--tema X] [--existentes N] [unidades...]");
    return 2;
}

// ---------------------------------------------------------------------------------------------
// Modo COMPOSICIÓN: de qué está hecho cada prompt, y si el prefijo estable lo es de verdad.
// ---------------------------------------------------------------------------------------------
static int Composicion(List<(string Path, string Content, ComposedUnitPrompt Prompt)> composed)
{
    Console.WriteLine("| Unidad | Reglas~ | Rúbrica~ | Catálogo~ | Temática~ | Directivas~ | Patrones~ "
        + "| Existentes~ | Unidad~ | ESTABLE~ | VARIABLE~ | TOTAL~ | Código % |");
    Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach ((string path, _, ComposedUnitPrompt p) in composed)
    {
        PromptComposition c = p.Composition;
        Console.WriteLine(
            $"| {path} | {c.Reglas} | {c.Rubrica} | {c.Catalogo} | {c.Tematica} | {c.Directivas} | {c.Patrones} "
            + $"| {c.Existentes} | {c.Unidad} | {c.Estable} | {c.Variable} | {c.Total} "
            + $"| {Pct(c.Unidad, c.Total)} |");
    }

    Console.WriteLine();

    // La comprobación que da sentido a todo lo demás: el prefijo es EL MISMO, byte a byte, en
    // todas las unidades. Si no lo fuera, la caché no serviría de nada y la factura no lo diría.
    string first = composed[0].Prompt.StablePrefix;
    bool identical = composed.All(x => x.Prompt.StablePrefix == first);
    Console.WriteLine(identical
        ? $"Prefijo estable IDÉNTICO en las {composed.Count} unidades: "
          + $"{first.Length} caracteres, ~{PromptTokens.Estimate(first)} tokens."
        : "AVISO: el prefijo estable NO es idéntico entre unidades — la caché no puede servirlo.");

    long variable = composed.Sum(x => (long)PromptTokens.Estimate(x.Prompt.UnitPart));
    Console.WriteLine($"Parte variable de las {composed.Count} unidades: ~{variable} tokens.");
    Console.WriteLine();
    Console.WriteLine("Lo que se paga por prompt si el prefijo NO se cachea entre unidades:");
    Console.WriteLine($"  ~{PromptTokens.Estimate(first)} tokens × {composed.Count} unidades "
        + $"= ~{PromptTokens.Estimate(first) * (long)composed.Count} tokens de prefijo repetido.");
    return identical ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------
// Modo CLAUDE: una pasada REAL por unidad, con el CLI de verdad y el toolbox de verdad.
// ---------------------------------------------------------------------------------------------
static async Task<int> ClaudeAsync(
    List<(string Path, string Content, ComposedUnitPrompt Prompt)> composed, bool split, string? model)
{
    string bridge = Path.Combine(AppContext.BaseDirectory, "Atalaya.Mcp.exe");
    if (!File.Exists(bridge))
    {
        bridge = Path.Combine(AppContext.BaseDirectory, "Atalaya.Mcp");
    }

    string work = Path.Combine(Path.GetTempPath(), "atalaya-bench");
    // --split arma la palanca que F18 midió y dejó apagada en producción: el prefijo estable por
    // el system prompt del CLI. Es la única forma de volver a comprobar el resultado el día que el
    // CLI cambie dónde corta su caché.
    var provider = new ClaudeCodeProvider(bridge, () => model, () => work) { UseSystemPromptPrefix = split };

    AgentReadiness ready = await provider.CheckAsync(CancellationToken.None);
    if (!ready.Ready)
    {
        Console.Error.WriteLine($"Claude Code no está listo: {ready.Message}");
        return 3;
    }

    var samples = new List<UsageSample>();
    CallTrace? trace = null;
    provider.UsageReported += s =>
    {
        samples.Add(s);
        trace?.Model(s);
    };

    var traces = new List<string>();

    Console.WriteLine("| Unidad | Llamadas | In | Out | CacheRead | CacheWrite | Total entrada | Duración | Tools |");
    Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");

    long totIn = 0, totOut = 0, totRead = 0, totWrite = 0;
    int totCalls = 0;
    var first = new List<(string Path, UsageSample Sample)>();
    foreach ((string path, string content, ComposedUnitPrompt prompt) in composed)
    {
        samples.Clear();
        var bench = new BenchToolbox(path);
        trace = new CallTrace();
        var toolbox = new TracingToolbox(bench, trace);
        var clock = Stopwatch.StartNew();

        var request = new AuditUnitRequest(
            path, content, prompt.Text, TechStack.DotNet, AuditMode.Lotes,
            Array.Empty<ExistingFinding>(), null,   // el toolbox del banco acepta lo que llegue
            split ? prompt.StablePrefix : null,
            split ? prompt.UnitPart : null);

        try
        {
            await provider.AuditUnitAsync(request, toolbox, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ({path}) falló: {ex.Message}");
        }

        clock.Stop();
        long i = samples.Sum(s => s.InputTokens);
        long o = samples.Sum(s => s.OutputTokens);
        long r = samples.Sum(s => s.CacheReadTokens);
        long w = samples.Sum(s => s.CacheWriteTokens);
        int calls = samples.Sum(s => s.Calls);
        totIn += i; totOut += o; totRead += r; totWrite += w; totCalls += calls;

        Console.WriteLine($"| {path} | {calls} | {i} | {o} | {r} | {w} | {i + r + w} "
            + $"| {(clock.ElapsedMilliseconds / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} s "
            + $"| {bench.Describe()} |");
        traces.Add(trace.Render(path));

        // LA PRIMERA LLAMADA es la única medida DETERMINISTA de la serie: su prompt lo fijan
        // nuestros bytes y nada más. De la segunda en adelante el prompt lleva dentro lo que
        // contestó el modelo, que cambia en cada ejecución — y con ello la escritura de caché.
        // Comparar totales entre dos ejecuciones mide sobre todo esa varianza; comparar primeras
        // llamadas mide el prompt, que es lo que esta fase cambia.
        if (samples.Count > 0)
        {
            first.Add((path, samples[0]));
        }
    }

    Console.WriteLine();
    Console.WriteLine("Por qué cada llamada (F19 §1): qué herramienta pidió, o si fue solo texto");
    foreach (string t in traces)
    {
        Console.WriteLine(t);
    }

    Console.WriteLine();
    Console.WriteLine("Primera llamada de cada unidad (determinista: el prompt lo fijan nuestros bytes)");
    Console.WriteLine("| Unidad | In | CacheRead | CacheWrite | Entrada |");
    Console.WriteLine("|---|---:|---:|---:|---:|");
    foreach ((string path, UsageSample f) in first)
    {
        Console.WriteLine($"| {path} | {f.InputTokens} | {f.CacheReadTokens} | {f.CacheWriteTokens} "
            + $"| {f.InputTokens + f.CacheReadTokens + f.CacheWriteTokens} |");
    }

    long entrada = totIn + totRead + totWrite;
    Console.WriteLine();
    Console.WriteLine($"TOTAL · llamadas {totCalls} · entrada {entrada} (In {totIn} + caché {totRead}/{totWrite}) "
        + $"· salida {totOut}");
    if (totCalls > 0)
    {
        long codigo = composed.Sum(x => (long)x.Prompt.Composition.Unidad);
        Console.WriteLine($"Entrada por llamada ≈ {entrada / totCalls} · código auditado ≈ {codigo} tokens en total "
            + $"· llamadas por unidad ≈ {(double)totCalls / composed.Count:0.#}");
    }

    return 0;
}

static string Pct(int part, int total)
    => total <= 0 ? "—" : (100.0 * part / total).ToString("0.#", CultureInfo.InvariantCulture) + " %";

static string? Flag(string[] argv, string name)
{
    int i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
