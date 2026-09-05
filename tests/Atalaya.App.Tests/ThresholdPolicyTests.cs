using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F13 — el umbral de tamaño es <b>política de la aplicación</b>, no preferencia de quien audita.
/// <para>
/// <b>La regla:</b> lo que escribe estado compartido se gobierna con ajuste compartido; lo personal
/// solo gobierna lo local. Este umbral clasifica el inventario y crea o resuelve hallazgos de
/// tamaño, y las dos cosas viven en el hub: con el número en la máquina de cada uno, dos
/// compañeros se pisaban en cada re-escaneo y mandaba el último que pasara por ahí.
/// </para>
/// <para>
/// Lo que se fija aquí: que los cuatro caminos que clasifican leen el <c>app.json</c>; que la
/// pantalla que lo edita valida y publica; que el umbral heredado de la etapa personal se ofrece
/// una vez y no se pierde en silencio; y —lo que de verdad prueba que esto era el arreglo— que un
/// cambio de política en una máquina clasifica igual en la de al lado después de un pull.
/// </para>
/// </summary>
public sealed class ThresholdPolicyTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";

    /// <summary>El fichero del parte: 1.117 líneas, por debajo del umbral de fábrica.</summary>
    private const int LegacyLoc = 1117;

    private const string LegacyUnit = "src/Legacy/MotorCalculoLegacy.cs";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly FindingIngestionService _ingestion;
    private readonly MeasuredFindingService _measured;
    private readonly InventoryRescanService _rescan;
    private readonly ThresholdPolicyService _policy;
    private readonly ToastCenter _toasts = new();

    public ThresholdPolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-politica", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        _clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(_clone, RepoUrl);
        _machines.SetClonePath("app", _clone);

        _ingestion = new FindingIngestionService(_hub, _ulids);
        _measured = new MeasuredFindingService(_hub, _ingestion, _machines);
        _rescan = new InventoryRescanService(_hub, new InventoryScanner(), _measured);
        _policy = new ThresholdPolicyService(_hub);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private void WriteUnit(string relative, int lines)
    {
        string abs = Path.Combine(_clone, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, string.Join('\n', Enumerable.Range(0, lines).Select(i => $"// linea {i}")));
    }

    /// <summary>Fija la política COMO LA FIJA LA PANTALLA: por el servicio, no a mano.</summary>
    private void SetPolicy(int loc) => _policy.Set("app", loc, _policy.Read("app").LargeUnitChars);

    private InventoryUnit UnitOf(string path)
        => _hub.Store.TryReadInventory("app", 1)!.Units.Single(u => u.Path == path);

    private IReadOnlyList<Finding> SizeFindings()
        => _hub.Store.ListFindings("app").Where(f => UnitMeasure.IsMeasured(f.RuleId)).ToList();

    // ================================================================ 1 · la regresión, contra la política

    /// <summary>
    /// El caso del parte, de ida y de vuelta, apuntando ahora a la política de la aplicación: con
    /// 30 la unidad de 1.117 líneas sale <b>Grande</b> con su hallazgo medido; con 1.500 vuelve a
    /// ser auditable y ese hallazgo se resuelve <b>por medida</b>, con el número escrito.
    /// </summary>
    [Fact]
    public void Politica_30_hace_grande_el_legacy_y_1500_lo_devuelve_a_auditable()
    {
        WriteUnit(LegacyUnit, LegacyLoc);
        SetPolicy(30);

        _rescan.Rescan("app", _clone);

        UnitOf(LegacyUnit).State.Should().Be(UnitState.Grande);
        Finding size = SizeFindings().Should().ContainSingle().Subject;
        size.Status.Should().Be(FindingStatus.Activo);
        size.Description.Should().Contain("1117");

        SetPolicy(1500);

        _rescan.Rescan("app", _clone);

        UnitOf(LegacyUnit).State.Should().Be(UnitState.Pendiente);
        Finding resolved = SizeFindings().Should().ContainSingle().Subject;
        resolved.Status.Should().Be(FindingStatus.Resuelto);
        resolved.Resolved!.Via.Should().Be(ResolutionVia.Medida);
        resolved.Resolved!.Justification.Should().Contain("1117 LOC < umbral 1500");
    }

    /// <summary>
    /// Y el umbral vive en un solo sitio: el <c>app.json</c>. Ni los ajustes de la máquina tienen
    /// dónde guardarlo ni la pantalla de Ajustes dónde editarlo — dos sitios editables para el
    /// mismo valor son dos verdades esperando a discrepar.
    /// </summary>
    [Fact]
    public void El_umbral_solo_existe_en_la_politica_de_la_aplicacion()
    {
        typeof(Thresholds).GetProperty("LargeUnitLoc").Should().NotBeNull();
        typeof(Thresholds).GetProperty("LargeUnitChars").Should().NotBeNull();

        typeof(LocalThresholds).GetProperty("LargeUnitLoc")
            .Should().BeNull("el ajuste personal ya no gobierna la clasificación");
        typeof(SettingsViewModel).GetProperty("LargeUnitLoc")
            .Should().BeNull("y Ajustes no lo edita");
    }

    // ---------------------------------------------------------------- los caminos que clasifican

    [Fact]
    public void El_escaneo_clasifica_con_la_politica_de_la_aplicacion()
    {
        WriteUnit(LegacyUnit, LegacyLoc);
        AppConfig app = _hub.Store.TryReadApp("app")!;

        ScanOutput conFabrica = new InventoryScanner().Scan(_clone, app, 1);
        app.Thresholds.LargeUnitLoc = 30;
        ScanOutput conPolitica = new InventoryScanner().Scan(_clone, app, 1);

        conFabrica.Inventory.Units.Should().OnlyContain(u => u.State == UnitState.Pendiente);
        conPolitica.Inventory.Units.Should().Contain(u => u.State == UnitState.Grande);
        conPolitica.LargeUnitFindings.Should().ContainSingle();
    }

    [Fact]
    public void La_siembra_de_ciclo_clasifica_con_la_politica_de_la_aplicacion()
    {
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                new InventoryUnit
                {
                    Path = LegacyUnit, Module = "Legacy", Loc = LegacyLoc, State = UnitState.Auditada,
                },
            },
        });
        SetPolicy(30);

        new CycleService(_hub, _ulids).TryCloseCycle("app", 1).Closed.Should().BeTrue();

        _hub.Store.TryReadInventory("app", 2)!.Units.Single().State.Should().Be(UnitState.Grande);
    }

    [Fact]
    public void La_re_medicion_de_un_hallazgo_usa_la_politica_de_la_aplicacion()
    {
        WriteUnit(LegacyUnit, LegacyLoc);
        SetPolicy(30);
        _rescan.Rescan("app", _clone);
        Finding size = SizeFindings().Single();

        _measured.Verify("app", size).Message.Should().Contain("1117 LOC ≥ umbral 30");

        SetPolicy(1500);
        _measured.Verify("app", _hub.Store.TryReadFinding("app", size.Id.ToString())!)
            .Message.Should().Contain("1117 LOC < umbral 1500");
    }

    // ================================================================ 2 · la pantalla que la edita

    private ThresholdsViewModel Dialog()
    {
        var vm = new ThresholdsViewModel(_policy, _toasts);
        vm.Load("app", "App", _hub.Store.TryReadInventory("app", 1)?.Units ?? new List<InventoryUnit>());
        return vm;
    }

    [Fact]
    public void La_pantalla_guarda_la_politica_y_la_deja_en_el_app_json()
    {
        ThresholdsViewModel vm = Dialog();
        vm.LargeUnitLoc.Should().Be(1500, "estrena la política vigente");

        vm.LargeUnitLoc = 30;
        vm.SaveCommand.Execute(null);

        _hub.Store.TryReadApp("app")!.Thresholds.LargeUnitLoc.Should().Be(30);
        _toasts.Items.Should().Contain(t => t.Text.Contains("30 LOC") && t.Text.Contains("re-escaneo"));
    }

    /// <summary>Los mínimos son los de siempre, y se dicen — nunca un descarte en silencio.</summary>
    [Theory]
    [InlineData(0, 60_000, "el umbral por líneas", 1)]
    [InlineData(1500, 0, "el umbral por peso", 1)]
    public void Un_valor_por_debajo_del_minimo_se_corrige_y_se_dice(int loc, int chars, string what, int min)
    {
        ThresholdPolicyResult result = _policy.Set("app", loc, chars);

        result.Corrections.Should().ContainSingle(c => c.Contains(what) && c.Contains($"el mínimo es {min}"));
        result.Message.Should().Contain("correcciones");
        _hub.Store.TryReadApp("app")!.Thresholds.LargeUnitLoc.Should().BeGreaterThanOrEqualTo(1);
        _hub.Store.TryReadApp("app")!.Thresholds.LargeUnitChars.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// La pantalla cuenta lo que cambiaría antes de decidir, sobre el inventario que ya hay. No
    /// re-escanea: el umbral aplica al re-escanear, y prometer aquí un efecto inmediato sería
    /// mentir sobre lo único que hay que entender de este ajuste.
    /// </summary>
    [Fact]
    public void La_pantalla_cuenta_lo_que_cambiaria_sin_reclasificar_nada()
    {
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                new InventoryUnit { Path = LegacyUnit, Module = "L", Loc = LegacyLoc },
                new InventoryUnit { Path = "src/Chica.cs", Module = "L", Loc = 40 },
            },
        });

        ThresholdsViewModel vm = Dialog();
        vm.LargeUnitLoc = 30;

        vm.Preview.Should().Contain("pasarían a serlo 2");
        UnitOf(LegacyUnit).State.Should().Be(UnitState.Pendiente, "editar no reclasifica: eso lo hace el re-escaneo");
    }

    // ================================================================ 3 · el valor local, retirado

    /// <summary>
    /// <b>La mudanza del umbral personal se retira</b> (F26 §C, revisión).
    /// <para>
    /// Entre <c>d859d16</c> y F13 el umbral de unidad grande fue un ajuste de esta máquina; F13 lo
    /// convirtió en política de cada aplicación y dejó una oferta —«tenías 30 LOC configurados
    /// aquí… ¿lo aplico a la política de XBLAST?»— que se hacía una vez por aplicación. La
    /// transición terminó hace tiempo y lo que quedaba era un aviso preguntando por un número que
    /// ya no gobierna nada.
    /// </para>
    /// <para>
    /// <b>Y no hace falta migrador.</b> La propiedad ya no existe, así que un <c>settings.json</c>
    /// viejo con <c>largeUnitLoc</c> dentro se lee ignorándolo y el primer guardado lo deja fuera
    /// del fichero. Es lo que este test comprueba: que se ignore, y que desaparezca sin preguntar.
    /// </para>
    /// </summary>
    [Fact]
    public void Un_umbral_local_viejo_se_ignora_y_se_borra_sin_preguntar()
    {
        // Un fichero de antes de F13, con el umbral personal y la lista de aplicaciones ya
        // preguntadas dentro.
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SettingsJson)!);
        File.WriteAllText(_paths.SettingsJson, """
            {
              "defaultThresholds": { "freshnessDays": 45, "largeUnitLoc": 30 },
              "largeUnitOfferedApps": [ "app" ],
              "editor": "vscode"
            }
            """);

        var settings = new SettingsService(_paths);
        AppSettings leidos = settings.Load();

        leidos.Thresholds.FreshnessDays.Should().Be(45, "lo que sí sigue siendo suyo se conserva");
        leidos.Editor.Should().Be("vscode");
        typeof(LocalThresholds).GetProperty("LegacyLargeUnitLoc")
            .Should().BeNull("el umbral personal ya no tiene dónde vivir");
        typeof(AppSettings).GetProperty("LargeUnitOfferedApps")
            .Should().BeNull("ni la lista de a quién ya se le preguntó");

        settings.Save(leidos);

        string escrito = File.ReadAllText(_paths.SettingsJson);
        escrito.Should().NotContain("largeUnitLoc", "el primer guardado lo borra, sin preguntar");
        escrito.Should().NotContain("largeUnitOfferedApps");
        escrito.Should().Contain("\"freshnessDays\": 45");
    }

    /// <summary>Y el Inventario no pregunta nada: no queda ni la oferta ni sus dos enlaces.</summary>
    [Fact]
    public void El_inventario_ya_no_ofrece_mudar_ningun_umbral()
    {
        typeof(InventoryViewModel).GetProperty("HasLargeUnitOffer").Should().BeNull();
        typeof(InventoryViewModel).GetProperty("LargeUnitOfferLabel").Should().BeNull();
        typeof(InventoryViewModel).GetProperty("AcceptLargeUnitOfferCommand").Should().BeNull();
        typeof(InventoryViewModel).GetProperty("DismissLargeUnitOfferCommand").Should().BeNull();

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        File.ReadAllText(Path.Combine(
                dir!.FullName, "src", "Atalaya.App", "Views", "InventoryView.xaml"))
            .Should().NotContain("LargeUnitOffer", "ni el aviso ni sus dos enlaces");
    }

    /// <summary>
    /// Y las apps que ya traían su <c>Thresholds</c> en el <c>app.json</c> lo conservan: la mudanza
    /// no puede estrenar a nadie con el valor de fábrica encima del suyo.
    /// </summary>
    [Fact]
    public void Un_app_json_con_umbral_propio_se_sigue_leyendo()
    {
        string path = new HubPaths(_paths.Hub).AppJson("app");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"largeUnitLoc\": 1500", "\"largeUnitLoc\": 800"));

        _policy.Read("app").LargeUnitLoc.Should().Be(800);
    }

    /// <summary>
    /// El Inventario de una visita nueva: ajustes releídos del disco, que es lo único que cruza
    /// entre una visita y la siguiente.
    /// </summary>
    private InventoryViewModel Inventory()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        HubContext hub = TestFactory.Hub(_paths, settings);
        InventoryViewModel vm = TestFactory.Inventory(hub, _paths, _machines, _ulids, settings, _toasts);
        vm.SetApp("app");
        vm.LoadAsync().GetAwaiter().GetResult();
        return vm;
    }
}

/// <summary>
/// F13, la prueba que de verdad dice si esto era el arreglo: <b>dos clones contra el mismo
/// <c>--bare</c></b> (norma N-1, sin red). Ana cambia la política, María hace pull, y el
/// re-escaneo de María clasifica igual que el de Ana — que es exactamente lo que no pasaba cuando
/// el umbral vivía en la máquina de cada uno.
/// </summary>
public sealed class ThresholdPolicySyncTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";

    private readonly string _root;
    private readonly string _remote;

    public ThresholdPolicySyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-politica-sync", Guid.NewGuid().ToString("N"));
        _remote = Path.Combine(_root, "remote.git");
        Repository.Init(_remote, isBare: true);
    }

    public void Dispose()
    {
        try
        {
            // Los objetos de git nacen de solo lectura: sin quitarles el atributo, borrar el árbol
            // de un clon revienta con «acceso denegado» y tumbaría un test que ya pasó.
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Limpieza best-effort: un handle de git retenido no puede tumbar un test.
        }
    }

    private (HubContext Hub, AppPaths Paths, MachineConfigStore Machines) User(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        var settings = new SettingsService(paths);
        settings.Load();
        GitHubAccountService account = TestFactory.Account(paths);
        account.Connect("gho_x", new GitHubUser(1, name, name, null, null));
        var hub = new HubContext(
            paths, settings, account, new DeployConfig { HubUrl = _remote }, NullLoggerFactory.Instance);
        return (hub, paths, new MachineConfigStore(paths.MachinesJson));
    }

    [Fact]
    public void La_politica_viaja_por_el_hub_y_el_re_escaneo_del_otro_clasifica_igual()
    {
        // Ana: da de alta la app, la publica y fija la política en 30.
        (HubContext ana, AppPaths anaPaths, MachineConfigStore anaMachines) = User("ana");
        ana.EnsureHub();
        ana.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        ana.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        ana.Sync!.CommitAndPush("app: alta").Should().BeTrue();

        new ThresholdPolicyService(ana).Set("app", 30, 60_000).Published.Should().BeTrue();

        // María: clona el hub y se trae la política sin haber tocado ningún ajuste suyo.
        (HubContext maria, AppPaths mariaPaths, MachineConfigStore mariaMachines) = User("maria");
        maria.EnsureHub();
        maria.Sync!.Pull();

        maria.Store.TryReadApp("app")!.Thresholds.LargeUnitLoc.Should().Be(30);

        // Y su re-escaneo, sobre su propio clon del código, clasifica con ESE número.
        string clone = Path.Combine(_root, "maria-clone");
        TestFactory.MakeClone(clone, RepoUrl);
        mariaMachines.SetClonePath("app", clone);
        string unit = Path.Combine(clone, "src");
        Directory.CreateDirectory(unit);
        File.WriteAllText(
            Path.Combine(unit, "Legacy.cs"),
            string.Join('\n', Enumerable.Range(0, 1117).Select(i => $"// linea {i}")));

        var ingestion = new FindingIngestionService(maria, new UlidFactory(SystemClock.Instance));
        var rescan = new InventoryRescanService(
            maria, new InventoryScanner(),
            new MeasuredFindingService(maria, ingestion, mariaMachines));
        rescan.Rescan("app", clone);

        maria.Store.TryReadInventory("app", 1)!.Units
            .Single(u => u.Path == "src/Legacy.cs").State.Should().Be(UnitState.Grande,
                "la política del equipo la clasifica igual en las dos máquinas");
    }
}
