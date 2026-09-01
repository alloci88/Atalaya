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
/// BUGFIX-AJUSTES — que cada ajuste PERSONAL llegue a la función que dice gobernar.
/// <para>
/// Estos tests no comprueban que el ajuste se guarde —eso ya funcionaba y no habría detectado el
/// defecto que los trajo—, sino que <b>el consumidor lee el valor configurado</b>. Uno por campo.
/// </para>
/// <para>
/// El umbral de tamaño ya no está aquí: desde F13 es política de la aplicación —escribe estado
/// compartido, así que se gobierna con ajuste compartido— y vive en <c>ThresholdPolicyTests</c>.
/// Lo que queda en esta pantalla es lo que solo gobierna lo local.
/// </para>
/// </summary>
public sealed class SettingsWiringTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly FindingIngestionService _ingestion;

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

    /// <summary>
    /// Y la frescura PUEDE seguir siendo personal porque no escribe nada compartido (F13 §2): solo
    /// rellena <c>FindingRow.IsStale</c>, que es una columna de la lista de quien mira. El estado
    /// que sí viaja al hub es <c>NeedsReview</c>, y lo escriben la reconciliación y la
    /// verificación — nunca el reloj de una máquina.
    /// </summary>
    [Fact]
    public void La_frescura_es_una_lente_de_lectura_y_no_escribe_en_el_hub()
    {
        string fuente = Reflection.SourceOf(typeof(FindingsViewModel));

        fuente.Should().Contain("IsStale = days > freshness",
            "la frescura solo colorea la fila que se está pintando");
        fuente.Should().NotContain("NeedsReview = days",
            "si la frescura escribiera el estado persistido, sería política de la aplicación");
        typeof(Finding).GetProperty("IsStale")
            .Should().BeNull("«rancio» no es un campo del hallazgo: se deriva al leer");
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
        s.Thresholds.FreshnessDays.Should().Be(60);
        s.Thresholds.LegacyLargeUnitLoc.Should().Be(0, "sin umbral heredado no hay nada que ofrecer");
        new Thresholds().LargeUnitLoc.Should().Be(1500, "el umbral de fábrica es de la aplicación");
        new Thresholds().LargeUnitChars.Should().Be(60_000);
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
            => SourceOf(type).Contains($"Current.{path}", StringComparison.Ordinal);

        /// <summary>El fichero de ese tipo, para poder mirar la línea que hace la lectura.</summary>
        public static string SourceOf(Type type)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
            {
                dir = dir.Parent;
            }

            string? file = Directory
                .EnumerateFiles(Path.Combine(dir!.FullName, "src"), $"{type.Name}.cs", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

            return file is null ? string.Empty : File.ReadAllText(file);
        }
    }
}
