using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F17 §4 y §5 — <b>el diálogo «Configurar ciclo» en sus tres momentos, y el juez preferido</b>.
/// Todo sin abrir una ventana: el diálogo se sustituye por uno guionizado y lo que se afirma es
/// lo que el view-model propone, cuándo avisa, qué configuración sale y qué hace cada camino con
/// un «cancelar».
/// </summary>
public sealed class CycleConfigDialogTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public CycleConfigDialogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-cycleconfig", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        TestRates.Seed(_hub);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1 });
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(_clone, RepoUrl);
        _machines.SetClonePath("app", _clone);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Un temporal que no se deja borrar no invalida el test.
        }
    }

    // ---------------------------------------------------------------- utillaje

    /// <summary>El diálogo guionizado: mira lo que se le propone y contesta lo que le digan.</summary>
    private sealed class ScriptedDialog : ICycleConfigDialog
    {
        public AuditTheme? ChooseTheme { get; set; }

        public string? ChooseModel { get; set; }

        public bool Accept { get; set; } = true;

        public List<CycleConfigViewModel> Shown { get; } = new();

        public bool Show(CycleConfigViewModel viewModel)
        {
            Shown.Add(viewModel);
            if (ChooseTheme is { } theme)
            {
                viewModel.SelectedTheme = viewModel.ThemeOptions.Single(o => o.Theme == theme);
            }

            if (ChooseModel is not null)
            {
                viewModel.SelectedModelId = ChooseModel;
            }

            viewModel.Accepted = Accept;
            return Accept;
        }
    }

    private static CycleConfigPreview Preview(int audited = 0, CycleConfig? current = null, int cycle = 1)
        => new("app", "App", cycle, current ?? CycleConfig.Default, audited);

    private static IReadOnlyList<ModelOption> Models(params string[] ids)
        => ids.Select(id => new ModelOption(id, id)).ToList();

    private CycleConfigFlow Flow(ScriptedDialog dialog, IAuditorProvider? provider = null)
        => new(dialog, _settings, provider is null ? null : AuditorProviderRegistry.Of(provider));

    private void Inventory(AuditTheme theme = AuditTheme.General, params (string Path, UnitState State)[] units)
    {
        var inv = new InventoryCycle { CycleN = 1, Theme = theme };
        foreach ((string path, UnitState state) in units)
        {
            inv.Units.Add(new InventoryUnit { Path = path, Module = "M", Loc = 10, State = state });
        }

        _hub.Store.WriteInventory("app", inv);
    }

    // ---------------------------------------------------------------- el view-model

    [Fact]
    public void En_el_alta_General_va_preseleccionada_y_marcada_como_recomendada()
    {
        var vm = new CycleConfigViewModel();
        vm.Load(Preview(), CycleConfigReason.Alta, "copilot", "GitHub Copilot", Models("gpt-5"), "gpt-5");

        vm.SelectedTheme.Theme.Should().Be(AuditTheme.General);
        vm.SelectedTheme.Label.Should().Be("General (recomendada)");
        vm.ThemeOptions.Should().HaveCount(6);
        vm.ThemeOptions.Where(o => o.Theme != AuditTheme.General).Should().OnlyContain(o => !o.Label.Contains("recomendada"));
        vm.Title.Should().Be("Configurar el primer ciclo");
        vm.HasWarning.Should().BeFalse();
        vm.SelectedModelId.Should().Be("gpt-5");
    }

    [Fact]
    public void Cambiar_de_tematica_con_trabajo_hecho_avisa_con_el_numero_y_la_frase()
    {
        var vm = new CycleConfigViewModel();
        vm.Load(Preview(audited: 7), CycleConfigReason.Configurar, "copilot", "GitHub Copilot", Models("gpt-5"), "gpt-5");

        vm.SelectedTheme = vm.ThemeOptions.Single(o => o.Theme == AuditTheme.Rendimiento);

        vm.HasWarning.Should().BeTrue();
        vm.Warning.Should().Be("7 unidades auditadas pasarán a pendientes; los hallazgos existentes no se tocan.");

        vm.SelectedTheme = vm.ThemeOptions.Single(o => o.Theme == AuditTheme.General);
        vm.HasWarning.Should().BeFalse("volver a la lupa vigente no cuesta nada");
    }

    [Fact]
    public void Sin_unidades_auditadas_cambiar_de_tematica_no_avisa()
    {
        var vm = new CycleConfigViewModel();
        vm.Load(Preview(audited: 0), CycleConfigReason.Cierre, "copilot", "GitHub Copilot", Models("gpt-5"), "gpt-5");

        vm.SelectedTheme = vm.ThemeOptions.Single(o => o.Theme == AuditTheme.Seguridad);

        vm.HasWarning.Should().BeFalse();
        vm.Title.Should().Be("Ciclo 1 abierto: ¿con qué lupa?");
    }

    [Fact]
    public void El_resultado_lleva_la_tematica_y_el_juez_preferido()
    {
        var vm = new CycleConfigViewModel();
        vm.Load(Preview(), CycleConfigReason.Configurar, "claude-code", "Claude Code", Models("opus", "sonnet"), "sonnet");
        vm.SelectedTheme = vm.ThemeOptions.Single(o => o.Theme == AuditTheme.Concurrencia);
        vm.SelectedModelId = "opus";

        vm.Result.Should().Be(new CycleConfig(AuditTheme.Concurrencia, "claude-code", "opus"));
    }

    /// <summary>El preferido del ciclo manda sobre el configurado en la máquina, si su proveedor es el mismo.</summary>
    [Fact]
    public void El_modelo_preseleccionado_es_el_preferido_del_ciclo_si_es_de_este_proveedor()
    {
        var current = new CycleConfig(AuditTheme.General, "copilot", "claude-sonnet-4");
        var vm = new CycleConfigViewModel();
        vm.Load(Preview(current: current), CycleConfigReason.Configurar, "copilot", "GitHub Copilot", Models("gpt-5", "claude-sonnet-4"), "gpt-5");
        vm.SelectedModelId.Should().Be("claude-sonnet-4");

        var other = new CycleConfigViewModel();
        other.Load(Preview(current: current), CycleConfigReason.Configurar, "claude-code", "Claude Code", Models("opus", "sonnet"), "sonnet");
        other.SelectedModelId.Should().Be("sonnet", "el preferido es de otra casa: cae al configurado en esta máquina");
    }

    // ---------------------------------------------------------------- el flujo

    [Fact]
    public async Task El_flujo_lista_los_modelos_del_proveedor_de_quien_configura()
    {
        var dialog = new ScriptedDialog { ChooseTheme = AuditTheme.Rendimiento };
        var agent = new FakeCopilotAgent(modelsScript: () => new[] { new AgentModel("m1", "Uno"), new AgentModel("m2", "Dos") });

        CycleConfig? chosen = await Flow(dialog, agent).AskAsync(Preview(), CycleConfigReason.Configurar);

        chosen.Should().Be(new CycleConfig(AuditTheme.Rendimiento, "fake", "m1"));
        dialog.Shown.Single().Models.Select(m => m.Id).Should().Equal("m1", "m2");
        dialog.Shown.Single().ProviderName.Should().Be("Agente falso");
    }

    [Fact]
    public async Task Cancelar_devuelve_nada()
    {
        var dialog = new ScriptedDialog { Accept = false, ChooseTheme = AuditTheme.Seguridad };

        (await Flow(dialog).AskAsync(Preview(), CycleConfigReason.Configurar)).Should().BeNull();
    }

    [Fact]
    public async Task Sin_lista_de_modelos_el_combo_trae_el_configurado_y_dice_por_que()
    {
        _settings.SetModelFor("fake", "mi-modelo");
        var dialog = new ScriptedDialog();
        var agent = new FakeCopilotAgent(modelsScript: () => throw new InvalidOperationException("sin red"));

        await Flow(dialog, agent).AskAsync(Preview(), CycleConfigReason.Configurar);

        CycleConfigViewModel vm = dialog.Shown.Single();
        vm.Models.Should().ContainSingle().Which.Id.Should().Be("mi-modelo");
        vm.ModelsNotice.Should().Contain("No se pudo consultar la lista de modelos");
    }

    // ---------------------------------------------------------------- reinicio y alta

    [Fact]
    public async Task Reiniciar_el_ciclo_pregunta_la_lupa_y_el_ciclo_nuevo_nace_con_ella()
    {
        Inventory(AuditTheme.General, ("src/A.cs", UnitState.Auditada));
        var dialog = new ScriptedDialog { ChooseTheme = AuditTheme.Mantenibilidad };
        InventoryViewModel vm = InventoryWith(dialog);

        await vm.ResetCycleCommand.ExecuteAsync(null);

        dialog.Shown.Single().Reason.Should().Be(CycleConfigReason.Reinicio);
        dialog.Shown.Single().CycleN.Should().Be(2);
        InventoryCycle next = _hub.Store.TryReadInventory("app", 2)!;
        next.Theme.Should().Be(AuditTheme.Mantenibilidad);
        next.OpenedUtc.Should().NotBeNull();
        next.Units.Single().State.Should().Be(UnitState.Pendiente);
        _hub.Store.ListSessions("app").Single(s => s.Mode == AuditMode.Reset).Theme.Should().Be(AuditTheme.Mantenibilidad);
    }

    [Fact]
    public async Task Cancelar_el_dialogo_del_reinicio_cancela_el_reinicio()
    {
        Inventory(AuditTheme.General, ("src/A.cs", UnitState.Auditada));
        InventoryViewModel vm = InventoryWith(new ScriptedDialog { Accept = false });

        await vm.ResetCycleCommand.ExecuteAsync(null);

        _hub.Store.TryReadApp("app")!.CurrentCycle.Should().Be(1);
        _hub.Store.TryReadInventory("app", 2).Should().BeNull();
    }

    [Fact]
    public async Task Configurar_ciclo_desde_el_panel_aplica_y_re_siembra()
    {
        Inventory(AuditTheme.General, ("src/A.cs", UnitState.Auditada), ("src/B.cs", UnitState.Pendiente));
        var dialog = new ScriptedDialog { ChooseTheme = AuditTheme.Seguridad };
        InventoryViewModel vm = InventoryWith(dialog);
        vm.CycleTheme.Should().Be(AuditTheme.General);

        await vm.ConfigureCycleCommand.ExecuteAsync(null);

        dialog.Shown.Single().AuditedUnits.Should().Be(1);
        vm.CycleTheme.Should().Be(AuditTheme.Seguridad);
        vm.CycleThemeLabel.Should().Be("Seguridad");
        vm.AuditedUnits.Should().Be(0);
        vm.PendingUnits.Should().Be(2);
    }

    private InventoryViewModel InventoryWith(ScriptedDialog dialog)
    {
        var ingestion = new FindingIngestionService(_hub, _ulids);
        var agent = new FakeCopilotAgent();
        var live = new LiveSessionService(
            () => new SessionCoordinator(_hub, ingestion, new ReconciliationService(_hub), _machines, _ulids, agent, _settings),
            agent, new OpenSessionStore(_paths), _hub);
        var governance = new GovernanceService(_hub, _ulids);
        var directives = new DirectiveService(_hub, new Atalaya.Inventory.DirectiveScanner(), _ulids);
        var vm = new InventoryViewModel(
            _hub, _ulids, new NavigationService(new ServiceCollection().BuildServiceProvider()), live, _settings,
            new CostEstimator(_hub), new TestFactory.AlwaysConfirms(), new GroupExpansionMemory(), _toasts,
            TestFactory.Links(_hub, _paths), TestFactory.LinkFlow(_hub, _paths, _toasts),
            new InventoryRescanService(_hub, new Atalaya.Inventory.InventoryScanner()),
            governance, new TestFactory.NoPatternSilencesDialog(),
            directives, new TestFactory.NoDirectivesDialog(),
            new DriftQuery(_hub), new TestFactory.NoDeletedUnitsDialog(),
            new ThresholdPolicyService(_hub), new TestFactory.NoThresholdsDialog(),
            TestFactory.CostGaps(_hub), new ModelRatesService(_hub),
            new TestFactory.NoReconcileCostsDialog(),
            AuditorProviderRegistry.Of(agent),
            new CycleConfigService(_hub), Flow(dialog, agent));
        vm.SetApp("app");
        vm.LoadAsync().GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public async Task El_alta_pregunta_tras_el_escaneo_y_el_ciclo_1_nace_con_la_lupa_elegida()
    {
        string root = Path.Combine(_root, "nueva");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Program.cs"), "class Program { static void Main() { } }\n");
        File.WriteAllText(Path.Combine(root, "nueva.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _settings.Current.HubUrlOverride = Path.Combine(_root, "remote");
        var dialog = new ScriptedDialog { ChooseTheme = AuditTheme.Fiabilidad };
        OnboardingViewModel wizard = TestFactory.Onboarding(
            _hub, _paths, _machines, _toasts, _ulids, new NavigationService(new ServiceCollection().BuildServiceProvider()),
            settings: _settings, flow: Flow(dialog));
        wizard.Name = "Nueva";
        wizard.RepoUrl = "https://example.invalid/org/nueva.git";
        wizard.ClonePath = root;

        await wizard.CreateCommand.ExecuteAsync(null);

        dialog.Shown.Single().Reason.Should().Be(CycleConfigReason.Alta);
        dialog.Shown.Single().SelectedTheme.Theme.Should().Be(AuditTheme.Fiabilidad, "lo eligió el guion");
        InventoryCycle inv = _hub.Store.TryReadInventory("nueva", 1)!;
        inv.Theme.Should().Be(AuditTheme.Fiabilidad);
        inv.OpenedUtc.Should().NotBeNull();
        inv.Units.Should().NotBeEmpty("el escaneo ya había pasado cuando se preguntó");
    }

    // ---------------------------------------------------------------- tras el cierre

    [Fact]
    public async Task Tras_un_cierre_quien_cerro_ve_el_dialogo_del_ciclo_heredado()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 2 });
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 2,
            Theme = AuditTheme.Rendimiento,
            Units = { new InventoryUnit { Path = "src/A.cs", Module = "M", State = UnitState.Auditada } },
        });
        var dialog = new ScriptedDialog { ChooseTheme = AuditTheme.Seguridad };
        var close = new CycleCloseResult(true, CycleAging.None)
        {
            Slug = "app", ClosedCycle = 1, NextCycle = 2, NextConfig = new CycleConfig(AuditTheme.Rendimiento, null, null),
        };
        MainViewModel shell = TestFactory.Shell(_paths, _hub, toasts: _toasts, settings: _settings,
            cycleConfig: new CycleConfigService(_hub), configFlow: Flow(dialog));

        await shell.OfferCycleConfigAsync(close);

        dialog.Shown.Single().Reason.Should().Be(CycleConfigReason.Cierre);
        dialog.Shown.Single().SelectedTheme.Theme.Should().Be(AuditTheme.Seguridad);
        dialog.Shown.Single().Warning.Should().Contain("1 unidad auditada pasará a pendiente", "el heredado ya tenía trabajo sembrado");
        InventoryCycle inv = _hub.Store.TryReadInventory("app", 2)!;
        inv.Theme.Should().Be(AuditTheme.Seguridad);
        inv.Units.Single().State.Should().Be(UnitState.Pendiente);
    }

    [Fact]
    public async Task Sin_cierre_no_se_ofrece_nada()
    {
        var dialog = new ScriptedDialog();
        MainViewModel shell = TestFactory.Shell(_paths, _hub, settings: _settings,
            cycleConfig: new CycleConfigService(_hub), configFlow: Flow(dialog));

        await shell.OfferCycleConfigAsync(CycleCloseResult.NotClosed);

        dialog.Shown.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- §5 el juez preferido

    [Fact]
    public void El_aviso_de_preferencia_solo_aparece_cuando_el_juez_difiere()
    {
        var prefers = new CycleConfig(AuditTheme.General, "claude-code", "opus");

        CyclePreference.Notice(prefers, "claude-code", "opus", "Claude Code").Should().BeNull();
        CyclePreference.Notice(prefers, "claude-code", "sonnet", "Claude Code")
            .Should().Be("Este ciclo prefiere opus (Claude Code); vas a auditar con sonnet (Claude Code). Auditar con otro juez puede producir disputas.");
        CyclePreference.Notice(prefers, "copilot", "gpt-5", "GitHub Copilot").Should().Contain("prefiere opus (Claude Code)");
        CyclePreference.Notice(CycleConfig.Default, "copilot", "gpt-5", "GitHub Copilot").Should().BeNull("sin preferencia no hay aviso");
        CyclePreference.Notice(new CycleConfig(AuditTheme.General, null, "gpt-5"), "copilot", "GPT-5", "GitHub Copilot")
            .Should().BeNull("sin proveedor declarado basta con el modelo, sin distinguir mayúsculas");
    }

    [Fact]
    public void El_dialogo_de_lanzar_lleva_el_aviso_en_una_linea_y_no_bloquea()
    {
        var with = new AuditLaunchConfirmation("App", new CostEstimator(_hub).Estimate("app", 2, 1, "fake"),
            "Agente falso", "fake-model", preferenceNotice: "Este ciclo prefiere opus.");
        with.HasPreferenceNotice.Should().BeTrue();

        var without = new AuditLaunchConfirmation("App", new CostEstimator(_hub).Estimate("app", 2, 1, "fake"), "Agente falso", "fake-model");
        without.HasPreferenceNotice.Should().BeFalse();

        string xaml = File.ReadAllText(Source("src/Atalaya.App/Views/AuditLaunchDialog.xaml"));
        xaml.Should().Contain("{Binding PreferenceNotice}").And.Contain("HasPreferenceNotice");
        xaml.Should().Contain("Confirmar y auditar", "el botón sigue ahí: es preferencia, no imposición");
    }

    [Fact]
    public void El_inventario_compara_el_juez_de_esta_maquina_con_el_preferido_del_ciclo()
    {
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            PreferredProvider = "claude-code",
            PreferredModel = "opus",
            Units = { new InventoryUnit { Path = "src/A.cs", Module = "M", State = UnitState.Pendiente } },
        });
        var dialog = new ScriptedDialog();
        InventoryViewModel vm = InventoryWith(dialog);

        AuditLaunchConfirmation confirmation = vm.ConfirmationFor(1);

        confirmation.HasPreferenceNotice.Should().BeTrue();
        confirmation.PreferenceNotice.Should().Contain("prefiere opus (Claude Code)").And.Contain("Agente falso");
        vm.PreferredModelLabel.Should().Be("opus (Claude Code)");
    }

    private static string Source(string relative)
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Atalaya.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("Atalaya.sln");
        }

        return Path.Combine(dir, relative);
    }
}
