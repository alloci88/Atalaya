using System.Diagnostics;
using System.Globalization;
using Atalaya.Agents;
using Atalaya.App.Services;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.PromptBench;

/// <summary>
/// <b>El barrido ENTERO, medido</b> (F24 §§3–4).
/// <para>
/// <b>Por qué no bastaba el modo <c>claude</c>.</b> Aquel mide el coste de un prompt: corre N
/// pasadas fijas contra un toolbox que acepta todo y no persiste. Sirve para lo que se construyó
/// —de qué está hecho un prompt y qué le pasa a la caché—, y no puede contestar la pregunta de
/// F24, que es <b>cuántas pasadas hacen falta</b>: eso lo deciden la puerta de las variantes y la
/// regla de parada, y las dos viven en la aplicación.
/// </para>
/// <para>
/// <b>Así que aquí corre la aplicación de verdad</b>: <see cref="SessionCoordinator"/>, su
/// <c>SessionToolbox</c>, su reconciliación, su regla de dos secas seguidas y su informe, sobre un
/// hub temporal. Lo único de mentira es el hub —vacío y en un temporal, para no ensuciar el de
/// nadie—; el resto es producción. Medir esto con una maqueta sería medir la maqueta.
/// </para>
/// <para>
/// <b>Se construyó para F24</b> y aquella regla se retiró (D-907), pero el banco se queda: es lo
/// único que sabe contestar «cuántas pasadas hacen falta» con la aplicación de verdad delante, y esa
/// pregunta sigue viva. <c>--sin-corte</c> apaga el corte de F21, que con <c>opus</c> se cae a veces.
/// </para>
/// </summary>
internal static class SweepBench
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> units, string cloneRoot, string? model,
        int maxPasses, int tandas, bool cut = true,
        AuditStyle style = AuditStyle.Libre, AuditTheme tema = AuditTheme.General)
    {
        if (!Directory.Exists(cloneRoot))
        {
            Console.Error.WriteLine($"No existe el clon: {cloneRoot}");
            return 2;
        }

        foreach (string u in units)
        {
            string abs = Path.Combine(cloneRoot, u.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                Console.Error.WriteLine($"No existe la unidad: {abs}");
                return 2;
            }
        }

        string bridge = Path.Combine(AppContext.BaseDirectory, "Atalaya.Mcp.exe");
        if (!File.Exists(bridge))
        {
            bridge = Path.Combine(AppContext.BaseDirectory, "Atalaya.Mcp");
        }

        Console.WriteLine($"BARRIDO REAL · tope {maxPasses} pasadas · {tandas} tanda(s) "
            + $"· modelo {model ?? "(por defecto)"}"
            + (cut ? " · con corte" : " · SIN corte")
            + $" · brazo {style}"
            + (tema == AuditTheme.General ? string.Empty : $" · lupa {tema}"));
        Console.WriteLine($"Clon: {cloneRoot}");
        Console.WriteLine();
        Console.WriteLine("| Tanda | Unidad | Pasadas | Llamadas | Nuevos | Ubic. | Veredicto | Duración |");
        Console.WriteLine("|---:|---|---:|---:|---:|---:|---|---:|");

        for (int tanda = 1; tanda <= tandas; tanda++)
        {
            int code = await OneAsync(units, cloneRoot, model, maxPasses, bridge, tanda, cut, style, tema);
            if (code != 0)
            {
                return code;
            }
        }

        return 0;
    }

    private static async Task<int> OneAsync(
        IReadOnlyList<string> units, string cloneRoot, string? model,
        int maxPasses, string bridge, int tanda, bool cut, AuditStyle style, AuditTheme tema)
    {
        // Un hub NUEVO por tanda. Es la condición para que dos tandas sean dos muestras y no una
        // segunda auditoría: con el hub de la anterior, la tanda 2 vería sus hallazgos como
        // existentes y estaría midiendo otra cosa (D-755 al revés).
        string root = Path.Combine(
            Path.GetTempPath(), "atalaya-f24-bench", $"{DateTime.UtcNow:yyyyMMddHHmmss}-t{tanda}");
        Directory.CreateDirectory(root);

        var paths = new AppPaths(Path.Combine(root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        AppSettings s = settings.Current;
        s.MaxPassesPerUnit = maxPasses;
        settings.Save(s);

        var account = new GitHubAccountService(new AccountStore(paths), SystemClock.Instance);
        var hub = new HubContext(paths, settings, account, new DeployConfig(), NullLoggerFactory.Instance);
        var machines = new MachineConfigStore(paths.MachinesJson);
        var ulids = new UlidFactory(SystemClock.Instance);
        var ingestion = new FindingIngestionService(hub, ulids);
        var reconciliation = new ReconciliationService(hub);

        machines.SetClonePath("banco", cloneRoot);
        hub.Store.WriteHub(new HubInfo { OrganizationName = "Banco" });
        hub.Store.WriteApp(new AppConfig
        {
            Slug = "banco", Name = "AtalayaBanco", RepoUrl = "local", Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        var cycle = new InventoryCycle { CycleN = 1, Theme = tema };
        foreach (string u in units)
        {
            cycle.Units.Add(new InventoryUnit { Path = u, Module = "Servicios", State = UnitState.Pendiente });
        }

        hub.Store.WriteInventory("banco", cycle);

        // El corte de F21 va encendido en producción, y aquí también por defecto. Se puede apagar
        // porque NO todos los modelos lo aguantan: con `opus` el CLI termina por «aborted_streaming»
        // en vez de «aborted_tools» y el proveedor se lleva la sesión entera por delante. Una medida
        // que no se puede tomar con el corte puesto se toma sin él, y se declara en la cabecera.
        var provider = new ClaudeCodeProvider(bridge, () => model, () => Path.Combine(root, "work"))
        {
            CutOnUnitDone = cut,
        };
        AgentReadiness ready = await provider.CheckAsync(CancellationToken.None);
        if (!ready.Ready)
        {
            Console.Error.WriteLine($"Claude Code no está listo: {ready.Message}");
            return 3;
        }

        var coordinator = new SessionCoordinator(
            hub, ingestion, reconciliation, machines, ulids, provider, settings)
        {
            Style = style,
        };

        var clock = Stopwatch.StartNew();
        SessionResult result = await coordinator.RunAsync(
            new SessionRequest("banco", AuditMode.Lotes, units), CancellationToken.None);
        clock.Stop();

        AuditSession session = hub.Store.ListSessions("banco").Single();
        foreach (UnitVerdictRecord unit in session.Units)
        {
            List<UnitPassRecord> passes = unit.Passes ?? new List<UnitPassRecord>();
            UnitUsageBreakdown? usage = session.UsageBreakdown
                .FirstOrDefault(b => string.Equals(b.Unit, unit.Unit, StringComparison.Ordinal));
            Console.WriteLine(
                $"| {tanda} | {Short(unit.Unit)} | {passes.Count} | {usage?.Calls ?? 0} "
                + $"| {passes.Sum(p => p.New)} | {passes.Sum(p => p.LocationsAdded)} "
                + $"| {unit.Verdict}{(unit.CoverageIncomplete ? " (incompleta)" : string.Empty)} "
                + $"| {(usage?.DurationMs ?? 0) / 1000.0:0.#} s |");
        }

        Console.WriteLine();
        Console.WriteLine($"  tanda {tanda}: {result.Counters.New} nuevos · "
            + $"{result.Counters.LocationsAdded} ubicaciones · {clock.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s");

        // El barrido pasada a pasada, que es el dato de F24: dónde deja de aportar.
        foreach (UnitVerdictRecord unit in session.Units)
        {
            string trace = string.Join(" · ", (unit.Passes ?? new List<UnitPassRecord>()).Select(p =>
                $"p{p.Index}: {(p.Dry ? "seca" : $"{p.New}n")}"
                + (p.LocationsAdded > 0 ? $"+{p.LocationsAdded}u" : string.Empty)
));
            Console.WriteLine($"  {Short(unit.Unit)}: {trace}");
        }

        // Los TÍTULOS, que es lo que permite la tabla de correspondencia con el caso de referencia.
        Console.WriteLine();
        foreach (Finding f in hub.Store.ListFindings("banco")
                     .OrderBy(f => f.Locations.Count > 0 ? f.Locations[0].Path : string.Empty, StringComparer.Ordinal)
                     .ThenBy(f => f.Locations.Count > 0 ? f.Locations[0].Line : 0))
        {
            string loc = f.Locations.Count > 0 ? $"{Short(f.Locations[0].Path)}:{f.Locations[0].Line}" : "—";
            Console.WriteLine($"  [{f.Severity}] {f.Title} · {f.RuleId} · {loc} · símbolo {f.Symbol ?? "—"}");
        }

        string report = hub.HubPaths.ReportFile("banco", result.SessionId.ToString());
        Console.WriteLine();
        Console.WriteLine($"  informe: {report}");
        Console.WriteLine();
        return 0;
    }

    private static string Short(string path) => Path.GetFileName(path);
}
