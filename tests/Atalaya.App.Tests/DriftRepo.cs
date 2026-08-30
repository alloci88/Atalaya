using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using LibGit2Sharp;

namespace Atalaya.App.Tests;

/// <summary>
/// Un clon de verdad, con historial de verdad, y el hub que le corresponde (F9).
/// <para>
/// La deriva se calcula HABLANDO CON GIT: commits, merge-base, renombrados, historiales reescritos.
/// Nada de eso se puede simular con un doble sin acabar probando el doble. Así que los tests de F9
/// construyen repositorios reales en un temporal — cuestan milisegundos y prueban lo que va a pasar.
/// </para>
/// </summary>
internal sealed class DriftRepo : IDisposable
{
    private readonly string _base;
    private readonly UlidFactory _ulids = new(Atalaya.Domain.Abstractions.SystemClock.Instance);
    private int _n;

    public DriftRepo(string slug = "app")
    {
        Slug = slug;
        _base = Path.Combine(Path.GetTempPath(), "atalaya-drift", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);

        Clone = Path.Combine(_base, "clone");
        Directory.CreateDirectory(Clone);
        Repository.Init(Clone);

        Paths = new AppPaths(Path.Combine(_base, "local"));
        Settings = new SettingsService(Paths);
        Settings.Load();
        Hub = TestFactory.Hub(Paths, Settings);
        Hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        Hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug,
            Name = slug.ToUpperInvariant(),
            RepoUrl = $"https://example.invalid/{slug}.git",
            CurrentCycle = 1,
        });
    }

    public string Slug { get; }

    public string Clone { get; }

    public AppPaths Paths { get; }

    public SettingsService Settings { get; }

    public HubContext Hub { get; }

    /// <summary>Escribe (o borra, con <c>null</c>) ficheros y commitea. Devuelve el sha corto.</summary>
    public string Commit(string message, params (string Path, string? Content)[] files)
    {
        foreach ((string path, string? content) in files)
        {
            string abs = Path.Combine(Clone, path.Replace('/', Path.DirectorySeparatorChar));
            if (content is null)
            {
                File.Delete(abs);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, content);
        }

        using var repo = new Repository(Clone);
        Commands.Stage(repo, "*");
        var who = new Signature("Tester", "t@example.com", DateTimeOffset.UtcNow.AddSeconds(_n++));
        return repo.Commit(message, who, who).Sha[..7];
    }

    /// <summary>Mueve un fichero y commitea, que es como git ve un renombrado.</summary>
    public string Move(string from, string to, string message = "move")
    {
        string source = Path.Combine(Clone, from.Replace('/', Path.DirectorySeparatorChar));
        string target = Path.Combine(Clone, to.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target);
        return Commit(message);
    }

    public string Head
    {
        get
        {
            using var repo = new Repository(Clone);
            return repo.Head.Tip!.Sha[..7];
        }
    }

    /// <summary>Deja el inventario con esas unidades AUDITADAS en el commit indicado.</summary>
    public Ulid Audit(string commit, params string[] paths)
    {
        Ulid session = _ulids.NewUlid();
        Hub.Store.WriteSession(new AuditSession
        {
            Id = session,
            AppSlug = Slug,
            Mode = AuditMode.Lotes,
            By = "tester",
            Machine = "test",
            StartedUtc = DateTimeOffset.UtcNow,
            Commit = commit,
            CycleN = 1,
        });

        InventoryCycle inv = Hub.Store.TryReadInventory(Slug, 1) ?? new InventoryCycle { CycleN = 1 };
        foreach (string path in paths)
        {
            InventoryUnit? unit = inv.Units.FirstOrDefault(u => u.Path == path);
            if (unit is null)
            {
                unit = new InventoryUnit { Path = path, Module = Module(path) };
                inv.Units.Add(unit);
            }

            unit.State = UnitState.Auditada;
            unit.AuditedInSession = session;
        }

        Hub.Store.WriteInventory(Slug, inv);
        return session;
    }

    /// <summary>Añade unidades PENDIENTES: cobertura inicial, que no es deriva.</summary>
    public void Pending(params string[] paths)
    {
        InventoryCycle inv = Hub.Store.TryReadInventory(Slug, 1) ?? new InventoryCycle { CycleN = 1 };
        foreach (string path in paths)
        {
            if (inv.Units.All(u => u.Path != path))
            {
                inv.Units.Add(new InventoryUnit { Path = path, Module = Module(path) });
            }
        }

        Hub.Store.WriteInventory(Slug, inv);
    }

    /// <summary>
    /// Registra un arreglo de la aplicación sobre el contenido que tienen esos ficheros AHORA, con
    /// su hallazgo detrás —que es lo que permite saber más tarde si ese arreglo quedó verificado—.
    /// Devuelve el ULID del hallazgo.
    /// </summary>
    public Ulid RecordFix(params string[] paths)
    {
        Ulid finding = SeedFinding(paths[0]);
        WriteFix(finding.ToString(), paths);
        return finding;
    }

    /// <summary>
    /// Un arreglo SIN hallazgo referenciado: los que escribió una versión anterior a F9.1. Sirve
    /// para probar que la migración es tolerante y cuenta como no cubierto.
    /// </summary>
    public void RecordLegacyFix(params string[] paths) => WriteFix(null, paths);

    private void WriteFix(string? findingId, string[] paths)
        => Hub.Store.WriteFix(new FixRecord
        {
            Id = _ulids.NewUlid(),
            AppSlug = Slug,
            By = "tester",
            Utc = DateTimeOffset.UtcNow,
            FindingId = findingId,
            Files = paths.Select(p => new FixFileStamp(
                p,
                Domain.Hashing.HashUtil.NormalizedContentHash(File.ReadAllBytes(
                    Path.Combine(Clone, p.Replace('/', Path.DirectorySeparatorChar)))))).ToList(),
        });

    /// <summary>Un hallazgo activo sobre esa unidad, como el que arreglaría una sesión fix.</summary>
    public Ulid SeedFinding(string path)
    {
        Ulid id = _ulids.NewUlid();
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, Head, "tester");
        Hub.Store.WriteFinding(Slug, new Finding
        {
            Id = id,
            RuleId = "R-1",
            Title = $"hallazgo de {path}",
            Severity = Severity.Media,
            Confidence = Confidence.Media,
            Locations = { new Location(path, 1) },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        });

        return id;
    }

    /// <summary>
    /// La verificación sale en VERDE: el hallazgo queda resuelto por la vía <c>verify</c>, con su
    /// evidencia. Es exactamente lo que escribe <c>VerifyCoordinator</c>.
    /// </summary>
    public void VerifyGreen(Ulid findingId)
    {
        Finding f = Hub.Store.TryReadFinding(Slug, findingId.ToString())!;
        f.Resolve(new ResolutionStamp(
            DateTimeOffset.UtcNow, ResolutionVia.Verify, AuditMode.Verify, Head, "tester",
            "el defecto ya no está en el código"));
        Hub.Store.WriteFinding(Slug, f);
    }

    /// <summary>
    /// La verificación FALLA: el hallazgo sigue ahí, reconfirmado. No cubre nada — y esa es
    /// justamente la mitad del guardarraíl que no puede aflojarse.
    /// </summary>
    public void VerifyRed(Ulid findingId)
    {
        Finding f = Hub.Store.TryReadFinding(Slug, findingId.ToString())!;
        f.Confirm(AuditMode.Verify, new DetectionStamp(
            DateTimeOffset.UtcNow, AuditMode.Verify, Head, "tester"));
        Hub.Store.WriteFinding(Slug, f);
    }

    public AppDrift Drift() => new DriftQuery(Hub).Compute(Slug, Clone);

    public UnitDrift Of(AppDrift drift, string path)
        => drift.Units.Single(u => u.Path == path);

    private static string Module(string path)
        => path.Contains('/') ? path[..path.IndexOf('/')] : "raiz";

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // Limpieza best-effort: un handle de git bloqueado no puede tumbar un test.
        }
    }
}
