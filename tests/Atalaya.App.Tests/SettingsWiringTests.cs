using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-AJUSTES — que cada ajuste de la pantalla llegue a la función que dice gobernar.
/// <para>
/// El parte: con el umbral de unidad grande puesto a 30 LOC, un fichero de 1.117 líneas seguía sin
/// salir «Grande». Se probó re-escanear, reiniciar y reiniciar el ciclo. El valor SÍ estaba
/// guardado (<c>settings.json</c> → <c>defaultThresholds.largeUnitLoc: 30</c>): lo que pasaba es
/// que quien clasifica leía <b>otro fichero</b>, el <c>app.json</c> del hub, donde nadie había
/// escrito nunca ese 30 y seguía el 1500 de fábrica.
/// </para>
/// <para>
/// Por eso estos tests no comprueban que el ajuste se guarde —eso ya funcionaba y no habría
/// detectado nada—, sino que <b>el consumidor lee el valor configurado</b>. Uno por campo, con la
/// regresión del umbral (30 → Grande → 1.500 → auditable) delante.
/// </para>
/// </summary>
public sealed class SettingsWiringTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";

    /// <summary>El fichero del parte: 1.117 líneas, por debajo del umbral de fábrica.</summary>
    private const int LegacyLoc = 1117;

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

    public SettingsWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-ajustes-cable", Guid.NewGuid().ToString("N"));
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
        _measured = new MeasuredFindingService(_hub, _ingestion, _machines, _settings);
        _rescan = new InventoryRescanService(_hub, new InventoryScanner(), _settings, _measured);
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

    /// <summary>Pone el umbral COMO LO PONE LA PANTALLA: guardado, no inyectado.</summary>
    private void SetThresholdFromSettings(int loc)
    {
        AppSettings s = _settings.Current;
        s.Thresholds.LargeUnitLoc = loc;
        _settings.Save(s);
    }

    private InventoryUnit UnitOf(string path)
        => _hub.Store.TryReadInventory("app", 1)!.Units.Single(u => u.Path == path);

    private IReadOnlyList<Finding> SizeFindings()
        => _hub.Store.ListFindings("app").Where(f => UnitMeasure.IsMeasured(f.RuleId)).ToList();

    // ================================================================ 1 · la regresión del parte

    /// <summary>
    /// El caso exacto del parte, de ida y de vuelta. Con el umbral en 30 la unidad de 1.117 líneas
    /// sale <b>Grande</b> y aparece su hallazgo medido; devuelto el umbral a 1.500 vuelve a ser
    /// auditable y ese hallazgo se resuelve <b>por medida</b>, con el número escrito.
    /// </summary>
    [Fact]
    public void Umbral_30_hace_grande_el_legacy_y_1500_lo_devuelve_a_auditable()
    {
        WriteUnit("src/Legacy/MotorCalculoLegacy.cs", LegacyLoc);
        SetThresholdFromSettings(30);

        _rescan.Rescan("app", _clone);

        UnitOf("src/Legacy/MotorCalculoLegacy.cs").State.Should().Be(UnitState.Grande,
            "1.117 LOC pasan de 30, y quien clasifica tiene que leer el umbral de Ajustes");
        Finding size = SizeFindings().Should().ContainSingle().Subject;
        size.Status.Should().Be(FindingStatus.Activo);
        size.Description.Should().Contain("1117");

        // Y de vuelta: el umbral se sube y el mismo gesto deshace la clasificación.
        SetThresholdFromSettings(1500);

        _rescan.Rescan("app", _clone);

        UnitOf("src/Legacy/MotorCalculoLegacy.cs").State.Should().Be(UnitState.Pendiente,
            "por debajo del umbral la unidad vuelve a la cola de auditoría");
        Finding resolved = SizeFindings().Should().ContainSingle().Subject;
        resolved.Status.Should().Be(FindingStatus.Resuelto);
        resolved.Resolved!.Via.Should().Be(ResolutionVia.Medida);
        resolved.Resolved!.Justification.Should().Contain("1117 LOC < umbral 1500");
    }

    /// <summary>
    /// La causa, fijada donde estaba: el <c>app.json</c> del hub NO decide esto. Antes traía su
    /// propio <c>largeUnitLoc</c> y era el que mandaba; ahora ni siquiera existe como campo, que es
    /// la única forma de que nadie vuelva a leerlo (misma regla que D-097).
    /// </summary>
    [Fact]
    public void El_umbral_ya_no_existe_en_el_app_json_del_hub()
    {
        typeof(Thresholds).GetProperty("LargeUnitLoc").Should().BeNull();
        typeof(Thresholds).GetProperty("LargeUnitChars").Should().BeNull();
        typeof(Thresholds).GetProperty("FreshnessDays").Should().BeNull();

        typeof(AppSettings).GetProperty("Thresholds")!.PropertyType
            .Should().Be<MeasureThresholds>("la única fuente del umbral son los ajustes de la máquina");
    }

    /// <summary>
    /// Y la clave del fichero no se movió: la máquina que ya tenía el 30 escrito lo conserva. Un
    /// arreglo que empieza tirando el valor que el usuario había puesto no arregla nada.
    /// </summary>
    [Fact]
    public void Un_settings_json_con_el_umbral_ya_puesto_se_sigue_leyendo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SettingsJson)!);
        File.WriteAllText(_paths.SettingsJson,
            """{"defaultThresholds":{"largeUnitLoc":30,"largeUnitChars":60000,"freshnessDays":45}}""");

        AppSettings loaded = new SettingsService(_paths).Load();

        loaded.Thresholds.LargeUnitLoc.Should().Be(30);
        loaded.Thresholds.FreshnessDays.Should().Be(45);
    }

    // ---------------------------------------------------------------- los tres caminos que clasifican

    /// <summary>El escaneo inicial (alta de app) — el primero de los tres.</summary>
    [Fact]
    public void El_escaneo_clasifica_con_el_umbral_configurado()
    {
        WriteUnit("src/Legacy/MotorCalculoLegacy.cs", LegacyLoc);
        AppConfig app = _hub.Store.TryReadApp("app")!;

        ScanOutput conUmbralAlto = new InventoryScanner().Scan(_clone, app, 1, new MeasureThresholds());
        ScanOutput conUmbralBajo = new InventoryScanner()
            .Scan(_clone, app, 1, new MeasureThresholds { LargeUnitLoc = 30 });

        conUmbralAlto.Inventory.Units.Should().OnlyContain(u => u.State == UnitState.Pendiente);
        conUmbralBajo.Inventory.Units.Should().Contain(u => u.State == UnitState.Grande);
        conUmbralBajo.LargeUnitFindings.Should().ContainSingle();
    }

    /// <summary>
    /// La siembra del ciclo — el tercero, y el que el usuario probó el último. Leía el mismo
    /// <c>app.json</c>, así que reiniciar el ciclo tampoco servía de nada.
    /// </summary>
    [Fact]
    public void La_siembra_de_ciclo_clasifica_con_el_umbral_configurado()
    {
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                new InventoryUnit
                {
                    Path = "src/Legacy/MotorCalculoLegacy.cs", Module = "Legacy",
                    Loc = LegacyLoc, State = UnitState.Auditada,
                },
            },
        });
        SetThresholdFromSettings(30);

        var cycles = new CycleService(_hub, _ulids, _settings);
        cycles.TryCloseCycle("app", 1).Closed.Should().BeTrue();

        InventoryCycle next = _hub.Store.TryReadInventory("app", 2)!;
        next.Units.Single().State.Should().Be(UnitState.Grande,
            "el ciclo nuevo se siembra contra el umbral de Ajustes, no contra el del app.json");
    }

    /// <summary>
    /// Y la re-medición de UN hallazgo («Verificar ahora» sobre uno medido) usa el mismo umbral: si
    /// leyera otro, el inventario y la ficha del hallazgo se contradirían.
    /// </summary>
    [Fact]
    public void La_re_medicion_de_un_hallazgo_usa_el_umbral_configurado()
    {
        WriteUnit("src/Legacy/MotorCalculoLegacy.cs", LegacyLoc);
        SetThresholdFromSettings(30);
        _rescan.Rescan("app", _clone);
        Finding size = SizeFindings().Single();

        MeasuredVerdict conservador = _measured.Verify("app", size);
        conservador.Applied.Should().BeTrue();
        conservador.Message.Should().Contain("1117 LOC ≥ umbral 30");

        SetThresholdFromSettings(1500);
        MeasuredVerdict generoso = _measured.Verify("app", _hub.Store.TryReadFinding("app", size.Id.ToString())!);

        generoso.Message.Should().Contain("1117 LOC < umbral 1500");
    }

    // ================================================================ 2 · un test por campo

    /// <summary>Frescura: la lista de hallazgos mira los días configurados, no una constante.</summary>
    [Fact]
    public void La_frescura_la_gobierna_el_valor_configurado()
    {
        AppSettings s = _settings.Current;
        s.Thresholds.FreshnessDays = 7;
        _settings.Save(s);

        _settings.Current.Thresholds.FreshnessDays.Should().Be(7);
        Reflection.ReadsSetting(typeof(FindingsViewModel), "Thresholds.FreshnessDays")
            .Should().BeTrue("la vista de hallazgos lee el ajuste al reconstruir la lista");
    }

    /// <summary>Tope de pasadas: la sesión nace con el configurado y lo deja escrito.</summary>
    [Fact]
    public async Task El_tope_de_pasadas_de_la_sesion_sale_del_ajuste()
    {
        WriteUnit("A.cs", 5);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 3;
        _settings.Save(s);

        var coordinator = new SessionCoordinator(
            _hub, _ingestion, new ReconciliationService(_hub), _machines, _ulids,
            new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>()), _settings);

        await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        _hub.Store.ListSessions("app").Should().Contain(x => x.MaxPassesPerUnit == 3);
    }

    /// <summary>
    /// Timeout de Copilot: el agente lo lee EN CADA envío. Capturado al construir —que es como
    /// estaba— cambiarlo obligaba a reiniciar la aplicación sin que nada lo dijera.
    /// </summary>
    [Fact]
    public void El_timeout_del_agente_sigue_al_ajuste_sin_reconstruirlo()
    {
        AppSettings s = _settings.Current;
        s.CopilotTimeoutMinutes = 20;
        _settings.Save(s);

        var agent = new RealCopilotAgent(
            sendTimeout: () => TimeSpan.FromMinutes(_settings.Current.CopilotTimeoutMinutes));
        agent.SendTimeout.Should().Be(TimeSpan.FromMinutes(20));

        s.CopilotTimeoutMinutes = 7;
        _settings.Save(s);

        agent.SendTimeout.Should().Be(TimeSpan.FromMinutes(7), "el mismo agente, sin reiniciar nada");
    }

    /// <summary>Y el mismo ajuste gobierna la compilación del arreglo asistido.</summary>
    [Fact]
    public void El_timeout_de_la_compilacion_sigue_al_mismo_ajuste()
    {
        AppSettings s = _settings.Current;
        s.CopilotTimeoutMinutes = 9;
        _settings.Save(s);

        var builds = new BuildRunner(
            timeout: () => TimeSpan.FromMinutes(_settings.Current.CopilotTimeoutMinutes));

        builds.Timeout.Should().Be(TimeSpan.FromMinutes(9));
    }

    /// <summary>Sincronización del hub: la carcasa lee el ajuste vigente, no el del arranque.</summary>
    [Fact]
    public void El_intervalo_de_sondeo_sale_del_ajuste_vigente()
    {
        using var repo = new DriftRepo();
        MainViewModel shell = TestFactory.Shell(repo.Paths, repo.Hub, settings: repo.Settings);
        int inicial = shell.PollingSeconds;

        AppSettings s = repo.Settings.Current;
        s.PollingSeconds = 45;
        repo.Settings.Save(s);

        shell.PollingSeconds.Should().Be(45).And.NotBe(inicial);
    }

    /// <summary>Y por debajo del mínimo la carcasa no se lo cree: 15 s es el suelo del sondeo.</summary>
    [Fact]
    public void El_intervalo_de_sondeo_nunca_baja_del_minimo()
    {
        using var repo = new DriftRepo();
        MainViewModel shell = TestFactory.Shell(repo.Paths, repo.Hub, settings: repo.Settings);

        AppSettings s = repo.Settings.Current;
        s.PollingSeconds = 1;
        repo.Settings.Save(s);

        shell.PollingSeconds.Should().Be(SettingsLimits.MinPollingSeconds);
    }

    /// <summary>Editor preferido: se lee al abrir, así que cambiarlo surte efecto sin reiniciar.</summary>
    [Fact]
    public void El_editor_preferido_lo_lee_el_lanzador_en_cada_apertura()
    {
        AppSettings s = _settings.Current;
        s.Editor = "vscode";
        _settings.Save(s);

        Reflection.ReadsSetting(typeof(EditorLauncher), "Editor")
            .Should().BeTrue("el lanzador pregunta por el editor cada vez que abre código");
        _settings.Current.Editor.Should().Be("vscode");
    }

    /// <summary>Modelo: lo resuelve quien lanza, leyendo el ajuste.</summary>
    [Fact]
    public void El_modelo_lo_lee_el_resolutor_del_ajuste()
    {
        AppSettings s = _settings.Current;
        s.CopilotModel = "un-modelo-elegido";
        _settings.Save(s);

        Reflection.ReadsSetting(typeof(ModelResolver), "CopilotModel").Should().BeTrue();
        _settings.Current.CopilotModel.Should().Be("un-modelo-elegido");
    }

    /// <summary>Arreglo asistido: apagado, el lanzador se niega y lo dice.</summary>
    [Fact]
    public void El_interruptor_del_arreglo_asistido_lo_consulta_el_lanzador()
    {
        Reflection.ReadsSetting(typeof(AssistedFixLauncher), "EnableAssistedFix").Should().BeTrue();
    }

    /// <summary>
    /// El TTL del claim: estaba en <c>app.json</c> desde §2 y NADIE lo leía — todos los claims
    /// nacían con los 30 minutos por defecto del modelo. Ahora sale de donde se configura.
    /// </summary>
    [Fact]
    public async Task El_ttl_del_claim_sale_del_app_json()
    {
        WriteUnit("A.cs", 5);
        AppConfig app = _hub.Store.TryReadApp("app")!;
        app.Thresholds.ClaimTtlMinutes = 90;
        _hub.Store.WriteApp(app);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        var claims = new List<int>();
        var coordinator = new SessionCoordinator(
            _hub, _ingestion, new ReconciliationService(_hub), _machines, _ulids,
            new FakeCopilotAgent(_ =>
            {
                claims.Add(_hub.Store.TryReadClaim("app", Domain.Hashing.HashUtil.UnitHash("A.cs"))?.TtlMinutes ?? 0);
                return Array.Empty<SubmitFindingArgs>();
            }),
            _settings);

        await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        claims.Should().NotBeEmpty().And.OnlyContain(ttl => ttl == 90);
    }

    // ================================================================ 3 · defaults y mínimos

    /// <summary>
    /// Un <c>settings.json</c> vacío estrena TODOS los valores de fábrica. La clave ausente que
    /// deserializa a su tipo por defecto —el <c>false</c> de D-563— ya mordió una vez; esto lo
    /// comprueba campo a campo en vez de fiarse.
    /// </summary>
    [Fact]
    public void Un_settings_vacio_estrena_todos_los_valores_de_fabrica()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SettingsJson)!);
        File.WriteAllText(_paths.SettingsJson, "{}");

        AppSettings s = new SettingsService(_paths).Load();

        s.Editor.Should().Be("vs");
        s.Theme.Should().Be("dark");
        s.PollingSeconds.Should().Be(60);
        s.Thresholds.LargeUnitLoc.Should().Be(1500);
        s.Thresholds.LargeUnitChars.Should().Be(60_000);
        s.Thresholds.FreshnessDays.Should().Be(60);
        s.MaxPassesPerUnit.Should().Be(5);
        s.CopilotTimeoutMinutes.Should().Be(15);
        s.EnableAssistedFix.Should().BeTrue();
        s.CopilotModel.Should().BeEmpty("un nombre de modelo caduca; se le pregunta al runtime");
    }

    /// <summary>Y los mismos valores salen de una instancia nueva, sin fichero ninguno.</summary>
    [Fact]
    public void Los_valores_de_fabrica_estan_escritos_en_un_solo_sitio()
    {
        var enMemoria = new AppSettings();

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SettingsJson)!);
        File.WriteAllText(_paths.SettingsJson, "{}");
        AppSettings desdeDisco = new SettingsService(_paths).Load();

        desdeDisco.Should().BeEquivalentTo(enMemoria, o => o.Excluding(x => x.ConnectionMigrated)
            .Excluding(x => x.AssistedFixDefaultApplied),
            "el fichero sin claves y el objeto recién construido tienen que ser el mismo ajuste");
    }

    private static class Reflection
    {
        /// <summary>
        /// ¿El código fuente de este tipo lee ese ajuste? Se mira el fichero, no el IL: lo que se
        /// quiere impedir es exactamente lo que pasó —que el consumidor lea OTRA cosa— y eso se ve
        /// en la línea que hace la lectura.
        /// </summary>
        public static bool ReadsSetting(Type type, string path)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
            {
                dir = dir.Parent;
            }

            string? file = Directory
                .EnumerateFiles(Path.Combine(dir!.FullName, "src"), $"{type.Name}.cs", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

            return file is not null && File.ReadAllText(file).Contains($"Current.{path}", StringComparison.Ordinal);
        }
    }
}
