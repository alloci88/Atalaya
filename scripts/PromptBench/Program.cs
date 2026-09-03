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
// Cuántas pasadas del barrido se simulan por unidad (F20). Una sola mide la primera pasada, que es
// el caso barato; el gasto de F20 está en las siguientes, donde el prefijo se vuelve a escribir.
int passes = int.TryParse(Flag(argv, "--pasadas"), out int pn) ? Math.Max(1, pn) : 1;
// F21 — el corte en `unit_done` va encendido en producción; el banco puede apagarlo porque la
// tanda SIN corte es la línea contra la que se compara. No hay otra forma de enseñar que la
// escritura de caché baja y que los hallazgos no se mueven.
bool noCut = argv.Contains("--sin-corte");
// F24 — la regla de las variantes va encendida en producción; el banco la apaga para tener la
// línea contra la que comparar. Apaga las DOS capas a la vez (contrato del prompt y filtro de la
// puerta): media medida diría que una hace el trabajo de la otra.
bool noVariants = argv.Contains("--sin-variantes");
// El clon sobre el que corre el barrido real. Por defecto, el propio repositorio.
string? cloneRoot = Flag(argv, "--clon");
int maxPasses = int.TryParse(Flag(argv, "--tope"), out int mp) ? Math.Max(1, mp) : 6;
int tandas = int.TryParse(Flag(argv, "--tandas"), out int td) ? Math.Max(1, td) : 1;

string root = RepoRoot();
// Los valores de las opciones (--model sonnet) NO son unidades: sin esto, «sonnet» acabaría
// buscándose como fichero. Se descartan la opción y lo que va detrás de ella.
var reserved = new HashSet<string>(StringComparer.Ordinal);
for (int k = 0; k < argv.Length; k++)
{
    if (argv[k] is "--model" or "--tema" or "--existentes" or "--pasadas" or "--clon" or "--tope"
            or "--tandas"
        && k + 1 < argv.Length)
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

Console.WriteLine($"Banco de medida · modo {mode}"
    + (mode == "claude" ? (split ? " --split" : " --whole") + (noCut ? " --sin-corte" : " (con corte)") : string.Empty));
Console.WriteLine($"Raíz: {root}");
Console.WriteLine(
    $"Temática: {theme} · unidades: {units.Count} · hallazgos conocidos: {existing} · pasadas: {passes}");
Console.WriteLine();

// El barrido real no compone nada por su cuenta: lo compone el coordinador, como en producción.
// Por eso se atiende ANTES de resolver las unidades contra este repositorio — las suyas viven en
// el clon que se le pase.
if (mode == "barrido")
{
    return await SweepBench.RunAsync(units, cloneRoot ?? root, model, !noVariants, maxPasses, tandas);
}

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
    "claude" => await ClaudeAsync(composed, split, model, passes, known, brief, theme, !noCut),
    _ => Uso(),
};

static int Uso()
{
    Console.Error.WriteLine(
        "Uso: PromptBench [composicion|claude|barrido] [--split|--whole] "
        + "[--model X] [--tema X] [--existentes N] [--pasadas N] [unidades...]");
    Console.Error.WriteLine(
        "     barrido: [--clon RUTA] [--tope N] [--tandas N] [--sin-variantes] [unidades...]");
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
    List<(string Path, string Content, ComposedUnitPrompt Prompt)> composed, bool split, string? model,
    int passes, IReadOnlyList<ExistingFinding> seeded, AuditorBrief brief, AuditTheme theme, bool cut)
{
    string bridge = Path.Combine(AppContext.BaseDirectory, "Atalaya.Mcp.exe");
    if (!File.Exists(bridge))
    {
        bridge = Path.Combine(AppContext.BaseDirectory, "Atalaya.Mcp");
    }

    string work = Path.Combine(Path.GetTempPath(), "atalaya-bench");
    // Lo que el auditor DICE. En una auditoría normal no interesa —los hallazgos viajan por
    // herramienta—, pero cuando una pasada no llama a ninguna, su texto es la única pista de por
    // qué (F20 §3).
    var said = new System.Text.StringBuilder();
    // --split arma la palanca que F18 midió y dejó apagada en producción: el prefijo estable por
    // el system prompt del CLI. Es la única forma de volver a comprobar el resultado el día que el
    // CLI cambie dónde corta su caché.
    var provider = new ClaudeCodeProvider(bridge, () => model, () => work)
    {
        UseSystemPromptPrefix = split,
        CutOnUnitDone = cut,
    };

    // Las pasadas que NO se pudieron cortar, con su motivo. Es la mitad honesta de la medida: un
    // corte que a veces no ocurre tiene que verse en la tabla, no esconderse en la media.
    int skipped = 0;
    provider.CutSkipped += why =>
    {
        skipped++;
        Console.WriteLine($"  SIN CORTE en esta pasada — {why}");
    };

    AgentReadiness ready = await provider.CheckAsync(CancellationToken.None);
    if (!ready.Ready)
    {
        Console.Error.WriteLine($"Claude Code no está listo: {ready.Message}");
        return 3;
    }

    var samples = new List<UsageSample>();
    CallTrace? trace = null;
    provider.TextStreamed += t => said.Append(t);
    provider.UsageReported += s =>
    {
        samples.Add(s);
        trace?.Model(s);
    };

    var traces = new List<string>();

    Console.WriteLine("| Unidad | Pasada | Llamadas | Fresca | Leída | ESCRITA | Salida | Duración | Tools |");
    Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");

    long totIn = 0, totOut = 0, totRead = 0, totWrite = 0;
    int totCalls = 0;
    int lateFindings = 0;
    var first = new List<(string Path, int Pass, UsageSample Sample)>();
    foreach ((string path, string content, ComposedUnitPrompt _) in composed)
    {
        // El barrido del banco, pasada a pasada: cada una ve como CONOCIDO lo que reportaron las
        // anteriores, igual que en la aplicación. Sin eso, la pasada 2 volvería a descubrir lo
        // mismo y no mediría un barrido sino dos primeras pasadas.
        var known = seeded.ToList();

        for (int pass = 1; pass <= passes; pass++)
        {
            ComposedUnitPrompt prompt = PromptComposer.Compose(
                path, content, brief, AuditMode.Lotes, known, null, null, theme, null);

            samples.Clear();
            said.Clear();
            var bench = new BenchToolbox(path);
            trace = new CallTrace();
            var toolbox = new TracingToolbox(bench, trace);
            var clock = Stopwatch.StartNew();

            var request = new AuditUnitRequest(
                path, content, prompt.Text, TechStack.DotNet, AuditMode.Lotes,
                known, null,
                split ? prompt.StablePrefix : null,
                split ? prompt.UnitPart : null);

            try
            {
                await provider.AuditUnitAsync(request, toolbox, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ({path}, pasada {pass}) falló: {ex.Message}");
            }

            clock.Stop();
            long i = samples.Sum(x => x.InputTokens);
            long o = samples.Sum(x => x.OutputTokens);
            long r = samples.Sum(x => x.CacheReadTokens);
            long w = samples.Sum(x => x.CacheWriteTokens);
            int calls = samples.Sum(x => x.Calls);
            totIn += i; totOut += o; totRead += r; totWrite += w; totCalls += calls;

            // LA VARIABLE DE CONTROL de F20: lo que se encuentra en pasadas >= 2. Un cambio que
            // ahorre tokens y seque las pasadas tardías no ahorra, degrada.
            if (pass >= 2)
            {
                lateFindings += bench.Findings;
            }

            Console.WriteLine($"| {path} | {pass} | {calls} | {i} | {r} | {w} | {o} "
                + $"| {(clock.ElapsedMilliseconds / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} s "
                + $"| {bench.Describe()} |");
            traces.Add(trace.Render($"{path} · pasada {pass}"));

            // F21 §3 — de qué está hecho lo que la vuelta siguiente reescribe en caché. Solo sale
            // cuando hay más de una llamada: con una, no hay vuelta siguiente que pagar.
            if (trace.RenderWriteBreakdown() is { Length: > 0 } desglose)
            {
                traces.Add(desglose);
            }

            // Una pasada MUDA —sin una sola herramienta— es el fallo que hay que diagnosticar, no
            // contar: su texto dice si el modelo se creyó que había terminado o si le pasó otra cosa.
            if (trace.CallsWithoutTools == trace.Calls && said.Length > 0)
            {
                string texto = said.ToString().Trim();
                Console.WriteLine($"  PASADA MUDA ({path}, pasada {pass}) — lo que dijo el auditor:");
                Console.WriteLine("  «" + (texto.Length > 900 ? texto[..900] + "…" : texto) + "»");
            }

            // LA PRIMERA LLAMADA es la única medida DETERMINISTA de la serie: su prompt lo fijan
            // nuestros bytes y nada más. De la segunda en adelante el prompt lleva dentro lo que
            // contestó el modelo, que cambia en cada ejecución.
            if (samples.Count > 0)
            {
                first.Add((path, pass, samples[0]));
            }

            // Lo reportado pasa a ser conocido para la pasada siguiente.
            foreach (string title in bench.Titles)
            {
                known.Add(new ExistingFinding(
                    $"01JBENCHP{pass:D1}{known.Count:D14}",
                    $"BUG-{known.Count + 1:D4}",
                    title,
                    "media",
                    $"{path}:1",
                    "activo",
                    "General"));
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("Por qué cada llamada (F19 §1): qué herramienta pidió, o si fue solo texto");
    foreach (string t in traces)
    {
        Console.WriteLine(t);
    }

    Console.WriteLine();
    Console.WriteLine("Primera llamada de cada pasada (determinista: el prompt lo fijan nuestros bytes)");
    Console.WriteLine("| Unidad | Pasada | Fresca | Leída | ESCRITA | Entrada |");
    Console.WriteLine("|---|---:|---:|---:|---:|---:|");
    foreach ((string path, int pass, UsageSample f) in first)
    {
        Console.WriteLine($"| {path} | {pass} | {f.InputTokens} | {f.CacheReadTokens} | {f.CacheWriteTokens} "
            + $"| {f.InputTokens + f.CacheReadTokens + f.CacheWriteTokens} |");
    }

    long entrada = totIn + totRead + totWrite;
    Console.WriteLine();
    Console.WriteLine($"TOTAL · llamadas {totCalls} · entrada {entrada} (In {totIn} + caché {totRead}/{totWrite}) "
        + $"· salida {totOut}");
    if (totCalls > 0)
    {
        int prompts = composed.Count * passes;
        Console.WriteLine($"Entrada por llamada ~ {entrada / totCalls}"
            + $" · llamadas por pasada ~ {(double)totCalls / prompts:0.##}"
            + $" · ESCRITURA DE CACHE por pasada ~ {totWrite / prompts}"
            + $" · lectura por pasada ~ {totRead / prompts}");
        if (passes > 1)
        {
            Console.WriteLine($"Hallazgos en pasadas >= 2 (variable de control): {lateFindings}");
        }

        if (cut)
        {
            int prompts2 = composed.Count * passes;
            Console.WriteLine($"Corte en unit_done: {prompts2 - skipped}/{prompts2} pasadas cortadas"
                + (skipped > 0 ? $" · {skipped} pagaron su llamada de cortesía (motivo arriba)" : string.Empty));
        }
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
