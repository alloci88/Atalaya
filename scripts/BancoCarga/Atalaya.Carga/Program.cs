using System.IO;
using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using LibGit2Sharp;

namespace Atalaya.Carga;

/// <summary>
/// <b>El banco de concurrencia</b> (F31 §1). N personas auditando a la vez contra el mismo hub,
/// durante un tiempo fijo, con el agente falso y sobre un <c>--bare</c> temporal (N-1: nunca el hub
/// real). Es la prueba de carga que el hub nunca tuvo.
/// </summary>
public static class Program
{
    private const string Slug = "bancocarga";

    public static async Task<int> Main(string[] args)
    {
        int n = Entero(args, "-N", 3);
        double minutos = Doble(args, "-Minutos", 10);
        string salida = Texto(args, "-Salida", null);
        Sesionista.Verboso = args.Any(a => string.Equals(a, "-Verboso", StringComparison.OrdinalIgnoreCase));

        string raiz = Path.Combine(Path.GetTempPath(), "atalaya-carga", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string bare = Path.Combine(raiz, "hub.git");
        Directory.CreateDirectory(raiz);
        Repository.Init(bare, isBare: true);

        Console.WriteLine($"Banco de concurrencia · N={n} · {minutos:0.##} min");
        Console.WriteLine($"Hub de pruebas: {bare}");
        Console.WriteLine();

        Sembrar(raiz, bare);

        var nombres = new[] { "Alvaro Cillero", "Daniel Rodriguez", "Maria Lopez", "Jorge Saiz", "Ana Pardo" };
        var ritmos = new[] { Ritmo.Rapido, Ritmo.Lento, Ritmo.QueSeCae };

        var gente = new List<Sesionista>();
        for (int i = 0; i < n; i++)
        {
            gente.Add(new Sesionista(
                nombres[i % nombres.Length],
                ritmos[i % ritmos.Length],
                Path.Combine(raiz, $"persona{i + 1}"),
                bare,
                Slug));
        }

        var arranque = DateTimeOffset.UtcNow;
        DateTimeOffset hasta = arranque.AddMinutes(minutos);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(minutos + 2));

        foreach (Sesionista quien in gente)
        {
            Console.WriteLine($"  · {quien.Nombre} — {quien.Ritmo}");
        }

        Console.WriteLine();
        Console.WriteLine("Trabajando…");

        // TODAS A LA VEZ, que es lo único que este banco viene a medir.
        await Task.WhenAll(gente.Select(quien => quien.TrabajarAsync(hasta, cts.Token))).ConfigureAwait(false);

        TimeSpan duracion = DateTimeOffset.UtcNow - arranque;
        string parte = Parte(gente, bare, raiz, n, duracion);

        Console.WriteLine();
        Console.WriteLine(parte);

        if (salida is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(salida))!);
            File.WriteAllText(salida, parte, new UTF8Encoding(false));
            Console.WriteLine($"Parte escrito en {salida}");
        }

        return 0;
    }

    /// <summary>
    /// La línea base que todas comparten: el hub, la aplicación y el inventario del ciclo. Sin
    /// inventario no hay nada que reclamar y las sesiones terminarían con cero.
    /// </summary>
    private static void Sembrar(string raiz, string bare)
    {
        string clon = Path.Combine(raiz, "semilla");
        var paths = new HubPaths(clon);
        using var sync = new HubSyncService(paths, ("Semilla", "semilla@example.invalid"));
        sync.EnsureCloned(bare);

        var store = new HubStore(paths);
        store.WriteHub(new HubInfo { OrganizationName = "Banco-de-carga" });
        store.WriteApp(new AppConfig
        {
            Slug = Slug,
            Name = "banco-de-carga",
            RepoUrl = "https://example.invalid/org/bancocarga.git",
            CurrentCycle = 1,
        });

        var ciclo = new InventoryCycle { CycleN = 1 };
        foreach (string unidad in Guion.EscribirCodigo(Path.Combine(raiz, "semilla-codigo"), 8))
        {
            ciclo.Units.Add(new InventoryUnit
            {
                Path = unidad,
                Module = unidad.Split('/')[^2],
                State = UnitState.Pendiente,
            });
        }

        store.WriteInventory(Slug, ciclo);

        if (!sync.CommitAndPush("semilla del banco de carga"))
        {
            throw new InvalidOperationException("No se pudo sembrar el hub de pruebas.");
        }
    }

    /// <summary>El parte de la tanda, en Markdown, para pegarlo tal cual.</summary>
    private static string Parte(List<Sesionista> gente, string bare, string raiz, int n, TimeSpan duracion)
    {
        // Lo que de verdad llegó al hub, leído de un clon NUEVO: preguntarle a los participantes
        // qué publicaron es preguntarle al sospechoso.
        string testigo = Path.Combine(raiz, "testigo");
        var paths = new HubPaths(testigo);
        using var sync = new HubSyncService(paths, ("Testigo", "testigo@example.invalid"));
        sync.EnsureCloned(bare);
        sync.Pull();
        var store = new HubStore(paths);

        IReadOnlyList<Finding> hallazgos = store.ListFindings(Slug);
        IReadOnlyList<AuditSession> sesiones = store.ListSessions(Slug);
        IReadOnlyList<Claim> reclamaciones = store.ListClaims(Slug);

        var sb = new StringBuilder();
        sb.AppendLine($"# Tanda del banco de concurrencia · N={n} · {duracion.TotalMinutes:0.#} min");
        sb.AppendLine();
        sb.AppendLine("| Persona | Ritmo | Sesiones | Hallazgos | Reintentos | Publicación más larga | Conflictos | Roturas |");
        sb.AppendLine("|---|---|--:|--:|--:|--:|--:|--:|");
        foreach (Sesionista quien in gente)
        {
            Medidas m = quien.Medidas;
            string ritmo = m.SeCayoAMitad ? $"{m.Ritmo} (se cayó)" : m.Ritmo.ToString();
            sb.AppendLine(
                $"| {m.Quien} | {ritmo} | {m.SesionesTerminadas} | {m.HallazgosNuevos} | {m.Reintentos} "
                + $"| {m.PublicacionMasLarga.TotalSeconds:0.00} s | {m.ConflictosResueltos.Count} | {m.Roturas.Count} |");
        }

        TimeSpan peor = gente.Count == 0 ? TimeSpan.Zero : gente.Max(q => q.Medidas.PublicacionMasLarga);

        sb.AppendLine();
        sb.AppendLine("## Lo que llegó al hub (leído de un clon nuevo)");
        sb.AppendLine();
        sb.AppendLine($"- Sesiones en el hub: **{sesiones.Count}**");
        sb.AppendLine($"- Hallazgos en el hub: **{hallazgos.Count}**");
        sb.AppendLine($"- Reclamaciones vivas al acabar: **{reclamaciones.Count}**");
        sb.AppendLine($"- Publicación más larga de toda la tanda: **{peor.TotalSeconds:0.00} s** "
            + $"(tope {HubSyncService.DefaultPushTimeout.TotalSeconds:0} s)");
        sb.AppendLine($"- Ninguna publicación por encima del tope: **{(peor < HubSyncService.DefaultPushTimeout ? "sí" : "NO")}**");
        sb.AppendLine();

        sb.AppendLine("### Sesiones publicadas, por persona");
        sb.AppendLine();
        foreach (IGrouping<string, AuditSession> grupo in sesiones.GroupBy(s => s.By).OrderBy(g => g.Key))
        {
            sb.AppendLine($"- {grupo.Key}: {grupo.Count()}");
        }

        var conflictos = gente.SelectMany(q => q.Medidas.ConflictosResueltos).Distinct().ToList();
        if (conflictos.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Conflictos y cómo se resolvieron");
            sb.AppendLine();
            foreach (string c in conflictos)
            {
                sb.AppendLine($"- {c}");
            }
        }

        var roturas = gente.SelectMany(q => q.Medidas.Roturas).ToList();
        if (roturas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Roturas");
            sb.AppendLine();
            foreach (string r in roturas)
            {
                sb.AppendLine($"- {r}");
            }
        }

        return sb.ToString();
    }

    private static string Texto(string[] args, string nombre, string porDefecto)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, nombre, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : porDefecto;
    }

    private static int Entero(string[] args, string nombre, int porDefecto)
        => int.TryParse(Texto(args, nombre, null), out int v) ? v : porDefecto;

    private static double Doble(string[] args, string nombre, double porDefecto)
        => double.TryParse(Texto(args, nombre, null), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : porDefecto;
}
