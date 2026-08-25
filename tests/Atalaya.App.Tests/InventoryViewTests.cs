using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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
/// F5.6 (V2 Inventario) — plegado compartido con V3, gestión de selección a escala y el diálogo
/// de confirmación con la estimación de coste.
/// <para>
/// Todo lo que se prueba aquí es lógica del view-model: con 900 unidades, lo que rompe no es el
/// dibujo sino la contabilidad —qué está marcado, cuánto suma y qué pasa al filtrar—. La barra de
/// selección tiene que decir la verdad del TOTAL, no de lo que se ve.
/// </para>
/// </summary>
public sealed class InventoryViewTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly RecordingConfirmer _confirmer = new();
    private readonly ServiceProvider _provider;

    public InventoryViewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-inventory-v2", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 5;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_paths);
        services.AddSingleton(_settings);
        services.AddSingleton(new MachineConfigStore(_paths.MachinesJson));
        services.AddSingleton<IUlidFactory>(_ulids);
        services.AddSingleton<InventoryScanner>();
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<ICopilotAgent>(new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>()));
        services.AddSingleton<NavigationService>();
        services.AddSingleton<OpenSessionStore>();
        services.AddSingleton(sp => new LiveSessionService(
            sp.GetRequiredService<SessionCoordinator>,
            sp.GetRequiredService<ICopilotAgent>(),
            sp.GetRequiredService<OpenSessionStore>(),
            sp.GetRequiredService<HubContext>()));
        services.AddTransient<SessionCoordinator>();
        services.AddTransient<SessionViewModel>();
        services.AddSingleton<CostEstimator>();
        services.AddSingleton<GroupExpansionMemory>();
        services.AddSingleton<IAuditLaunchConfirmer>(_confirmer);
        services.AddTransient<InventoryViewModel>();
        _provider = services.BuildServiceProvider();
    }

    /// <summary>Recuerda qué se preguntó y responde lo que le digan. El diálogo real no aparece.</summary>
    private sealed class RecordingConfirmer : IAuditLaunchConfirmer
    {
        public bool Answer { get; set; } = true;

        public List<AuditLaunchConfirmation> Asked { get; } = new();

        public bool Confirm(AuditLaunchConfirmation confirmation)
        {
            Asked.Add(confirmation);
            return Answer;
        }
    }

    private void SeedInventory(params (string Module, string Path, UnitState State)[] units)
    {
        var cycle = new InventoryCycle { CycleN = 1 };
        foreach ((string module, string path, UnitState state) in units)
        {
            cycle.Units.Add(new InventoryUnit { Path = path, Module = module, State = state });
        }

        _hub.Store.WriteInventory("app", cycle);
    }

    /// <summary><paramref name="modules"/> módulos con dos unidades pendientes cada uno.</summary>
    private void SeedModules(int modules)
        => SeedInventory(Enumerable.Range(0, modules)
            .SelectMany(m => new[]
            {
                ($"M{m:00}", $"src/M{m:00}/A{m:00}.cs", UnitState.Pendiente),
                ($"M{m:00}", $"src/M{m:00}/B{m:00}.cs", UnitState.Pendiente),
            })
            .ToArray());

    private async Task<InventoryViewModel> Loaded()
    {
        var vm = _provider.GetRequiredService<InventoryViewModel>();
        vm.SetApp("app");
        await vm.LoadAsync();
        return vm;
    }

    private static UnitNode Unit(InventoryViewModel vm, string path)
        => vm.Modules.SelectMany(m => m.Units).Single(u => u.Path == path);

    // =============================================================== §1 plegar y desplegar

    [Fact]
    public async Task Con_pocos_modulos_el_arbol_abre_desplegado()
    {
        SeedModules(3);
        InventoryViewModel vm = await Loaded();

        vm.Modules.Should().HaveCount(3).And.OnlyContain(m => m.IsExpanded);
        vm.HasGroups.Should().BeTrue();
        vm.AllCollapsed.Should().BeFalse();
        vm.ToggleAllLabel.Should().Be("Colapsar todo");
    }

    [Fact]
    public async Task Con_muchos_modulos_el_arbol_abre_plegado()
    {
        SeedModules(GroupExpansionMemory.SmallListGroups + 3);
        InventoryViewModel vm = await Loaded();

        vm.Modules.Should().OnlyContain(m => !m.IsExpanded);
        vm.AllCollapsed.Should().BeTrue();
        vm.ToggleAllLabel.Should().Be("Expandir todo");
    }

    [Fact]
    public async Task Un_solo_boton_alterna_entre_colapsar_todo_y_expandir_todo()
    {
        SeedModules(3);
        InventoryViewModel vm = await Loaded();

        vm.ToggleAllGroupsCommand.Execute(null);
        vm.Modules.Should().OnlyContain(m => !m.IsExpanded);
        vm.ToggleAllLabel.Should().Be("Expandir todo");

        vm.ToggleAllGroupsCommand.Execute(null);
        vm.Modules.Should().OnlyContain(m => m.IsExpanded);
        vm.ToggleAllLabel.Should().Be("Colapsar todo");
    }

    /// <summary>El estado de plegado es una decisión: recargar la página no puede deshacerla.</summary>
    [Fact]
    public async Task El_plegado_sobrevive_a_la_recarga()
    {
        SeedModules(3);
        InventoryViewModel vm = await Loaded();
        vm.ToggleAllGroupsCommand.Execute(null);

        await vm.LoadAsync();

        vm.Modules.Should().OnlyContain(m => !m.IsExpanded);
        vm.ToggleAllLabel.Should().Be("Expandir todo");
    }

    /// <summary>
    /// La memoria es la MISMA que la de V3 (F5.6 §1) y las dos vistas conviven. Las claves llevan
    /// prefijo por eso: un módulo llamado como una unidad de V3 no puede plegar el grupo ajeno.
    /// </summary>
    [Fact]
    public async Task La_clave_de_plegado_de_V2_no_pisa_la_de_V3()
    {
        SeedInventory(("src/Common.cs", "src/Common.cs", UnitState.Pendiente));
        InventoryViewModel vm = await Loaded();

        vm.Modules.Single().Key.Should().StartWith("inv ").And.Contain("app");
    }

    // =============================================================== §3 selección a escala

    [Fact]
    public async Task La_barra_de_seleccion_solo_existe_cuando_hay_algo_marcado()
    {
        SeedModules(2);
        InventoryViewModel vm = await Loaded();

        vm.HasSelection.Should().BeFalse();
        vm.SelectedCount.Should().Be(0);

        Unit(vm, "src/M00/A00.cs").IsSelected = true;

        vm.HasSelection.Should().BeTrue();
        vm.SelectedCount.Should().Be(1);
        vm.SelectionLabel.Should().Be("1 unidad seleccionada");

        Unit(vm, "src/M00/B00.cs").IsSelected = true;
        vm.SelectionLabel.Should().Be("2 unidades seleccionadas");
    }

    [Fact]
    public async Task Un_clic_en_la_cabecera_selecciona_el_modulo_entero_y_otro_lo_suelta()
    {
        SeedModules(2);
        InventoryViewModel vm = await Loaded();
        ModuleNode module = vm.Modules.Single(m => m.Name == "M00");

        module.IsChecked = true;
        module.Units.Should().OnlyContain(u => u.IsSelected);
        vm.SelectedCount.Should().Be(2, "solo el módulo marcado, no el otro");

        module.IsChecked = false;
        module.Units.Should().OnlyContain(u => !u.IsSelected);
        vm.SelectedCount.Should().Be(0);
    }

    [Fact]
    public async Task La_casilla_del_modulo_queda_indeterminada_con_seleccion_parcial()
    {
        SeedModules(1);
        InventoryViewModel vm = await Loaded();
        ModuleNode module = vm.Modules.Single();

        module.IsChecked.Should().Be(false);

        Unit(vm, "src/M00/A00.cs").IsSelected = true;
        module.IsChecked.Should().BeNull("una de dos: ni marcado ni vacío");

        Unit(vm, "src/M00/B00.cs").IsSelected = true;
        module.IsChecked.Should().Be(true);

        Unit(vm, "src/M00/A00.cs").IsSelected = false;
        module.IsChecked.Should().BeNull();
    }

    [Fact]
    public async Task Deseleccionar_todo_vacia_la_seleccion_de_una_vez()
    {
        SeedModules(3);
        InventoryViewModel vm = await Loaded();
        foreach (ModuleNode module in vm.Modules)
        {
            module.IsChecked = true;
        }

        vm.SelectedCount.Should().Be(6);

        vm.ClearSelectionCommand.Execute(null);

        vm.SelectedCount.Should().Be(0);
        vm.HasSelection.Should().BeFalse();
        vm.Modules.Should().OnlyContain(m => m.IsChecked == false);
    }

    [Fact]
    public async Task Seleccionar_pendientes_es_simetrico()
    {
        SeedInventory(
            ("M", "a.cs", UnitState.Pendiente),
            ("M", "b.cs", UnitState.Pendiente),
            ("M", "c.cs", UnitState.Auditada));
        InventoryViewModel vm = await Loaded();

        vm.PendingToggleLabel.Should().Be("Seleccionar pendientes");

        vm.SelectPendingCommand.Execute(null);
        vm.SelectedCount.Should().Be(2, "la auditada no es pendiente");
        vm.PendingToggleLabel.Should().Be("Deseleccionar pendientes");

        vm.SelectPendingCommand.Execute(null);
        vm.SelectedCount.Should().Be(0);
        vm.PendingToggleLabel.Should().Be("Seleccionar pendientes");
    }

    /// <summary>
    /// El caso que rompía con 900 unidades: buscar tiraba y reconstruía el árbol, y con él la
    /// selección. Filtrar es mirar, no decidir.
    /// </summary>
    [Fact]
    public async Task Filtrar_no_deselecciona_lo_que_queda_fuera_de_la_vista()
    {
        SeedInventory(
            ("Servicios", "src/Servicios/Uno.cs", UnitState.Pendiente),
            ("Vistas", "src/Vistas/Dos.cs", UnitState.Pendiente));
        InventoryViewModel vm = await Loaded();

        Unit(vm, "src/Servicios/Uno.cs").IsSelected = true;
        vm.SelectedCount.Should().Be(1);

        vm.SearchText = "Vistas";
        vm.Modules.SelectMany(m => m.Units).Should().ContainSingle().Which.Path.Should().Be("src/Vistas/Dos.cs");
        vm.SelectedCount.Should().Be(1, "la barra dice la verdad del TOTAL, no de lo visible");
        vm.HasSelection.Should().BeTrue();

        vm.SearchText = string.Empty;
        Unit(vm, "src/Servicios/Uno.cs").IsSelected.Should().BeTrue("y al volver sigue marcada");
    }

    [Fact]
    public async Task La_seleccion_sobrevive_a_colapsar_y_expandir()
    {
        SeedModules(2);
        InventoryViewModel vm = await Loaded();
        vm.Modules.Single(m => m.Name == "M00").IsChecked = true;

        vm.ToggleAllGroupsCommand.Execute(null);
        vm.ToggleAllGroupsCommand.Execute(null);

        vm.SelectedCount.Should().Be(2);
        vm.Modules.Single(m => m.Name == "M00").IsChecked.Should().Be(true);
    }

    /// <summary>
    /// Un re-escaneo puede llevarse una unidad por delante. El contador no puede seguir contando
    /// lo que ya no existe: sería exactamente el número que hace gastar de más.
    /// </summary>
    [Fact]
    public async Task Lo_seleccionado_que_desaparece_del_inventario_deja_de_contar()
    {
        SeedInventory(("M", "a.cs", UnitState.Pendiente), ("M", "b.cs", UnitState.Pendiente));
        InventoryViewModel vm = await Loaded();
        vm.SelectPendingCommand.Execute(null);
        vm.SelectedCount.Should().Be(2);

        SeedInventory(("M", "a.cs", UnitState.Pendiente));
        await vm.LoadAsync();

        vm.SelectedCount.Should().Be(1);
    }

    // =============================================================== §4 confirmación y coste

    /// <summary>Con historial, el diálogo enseña el mismo número que calcula el estimador.</summary>
    [Fact]
    public async Task Por_encima_del_umbral_se_pregunta_antes_de_gastar()
    {
        SeedModules(3);   // 6 unidades pendientes
        SeedCostHistory(maxPasses: 5, 10m, 10m, 10m);
        InventoryViewModel vm = await Loaded();
        vm.SelectPendingCommand.Execute(null);

        await vm.AuditSelectionCommand.ExecuteAsync(null);

        AuditLaunchConfirmation asked = _confirmer.Asked.Should().ContainSingle().Subject;
        asked.Estimate.Units.Should().Be(6);
        asked.Estimate.MaxPasses.Should().Be(5, "el tope vigente de los ajustes");
        asked.Estimate.Total.Should().Be(60m);
        asked.Breakdown.Should().Contain("6 unidades × ~10/unidad");
        asked.PassesLine.Should().Contain("5 pasadas");
    }

    [Fact]
    public async Task Cancelar_no_lanza_nada_y_deja_la_seleccion_intacta()
    {
        SeedModules(3);
        InventoryViewModel vm = await Loaded();
        vm.SelectPendingCommand.Execute(null);
        _confirmer.Answer = false;

        await vm.AuditSelectionCommand.ExecuteAsync(null);

        _confirmer.Asked.Should().ContainSingle();
        _hub.Store.ListSessions("app").Should().BeEmpty();
        vm.SelectedCount.Should().Be(6, "cancelar es volver atrás, no perder el trabajo hecho");
        vm.StatusMessage.Should().Contain("cancelado");
    }

    /// <summary>Tres unidades o menos no valen un clic de más: el umbral por defecto es 3.</summary>
    [Fact]
    public async Task Una_seleccion_pequeña_se_lanza_sin_preguntar()
    {
        SeedInventory(
            ("M", "a.cs", UnitState.Pendiente),
            ("M", "b.cs", UnitState.Pendiente),
            ("M", "c.cs", UnitState.Pendiente));
        InventoryViewModel vm = await Loaded();
        vm.SelectPendingCommand.Execute(null);

        await vm.AuditSelectionCommand.ExecuteAsync(null);

        _confirmer.Asked.Should().BeEmpty("3 ≤ el umbral por defecto");
    }

    [Fact]
    public async Task El_umbral_se_puede_bajar_por_aplicación()
    {
        AppConfig app = _hub.Store.TryReadApp("app")!;
        app.Thresholds.ConfirmLaunchUnits = 1;
        _hub.Store.WriteApp(app);
        SeedInventory(("M", "a.cs", UnitState.Pendiente), ("M", "b.cs", UnitState.Pendiente));
        InventoryViewModel vm = await Loaded();
        vm.SelectPendingCommand.Execute(null);

        await vm.AuditSelectionCommand.ExecuteAsync(null);

        _confirmer.Asked.Should().ContainSingle();
    }

    /// <summary>
    /// Sin historial la pregunta se hace igual: lo que cambia es que no hay número, y el diálogo
    /// lo dice en vez de inventarse uno (N-2).
    /// </summary>
    [Fact]
    public async Task Sin_historial_se_pregunta_igual_pero_sin_numero()
    {
        SeedModules(3);
        InventoryViewModel vm = await Loaded();
        vm.SelectPendingCommand.Execute(null);

        await vm.AuditSelectionCommand.ExecuteAsync(null);

        AuditLaunchConfirmation asked = _confirmer.Asked.Should().ContainSingle().Subject;
        asked.Estimate.HasNumber.Should().BeFalse();
        asked.IsWeakEstimate.Should().BeTrue();
        asked.Provenance.Should().Contain("Sin coste medido");
        asked.Headline.Should().Contain("6 unidades").And.Contain("App");
    }

    // =============================================================== §5 resumen del ciclo

    [Fact]
    public async Task El_ciclo_dice_desde_cuando_lo_es()
    {
        SeedModules(1);
        _hub.Store.WriteSession(SystemSession(AuditMode.Reset, cycleN: 1));

        InventoryViewModel vm = await Loaded();

        vm.CycleLabel.Should().StartWith("Ciclo 1 · iniciado ");
        vm.CycleTooltip.Should().Contain("Una vuelta completa al inventario")
            .And.Contain("los resets abren ciclo nuevo");
    }

    [Fact]
    public async Task Sin_traza_de_apertura_el_ciclo_es_solo_su_numero()
    {
        SeedModules(1);
        InventoryViewModel vm = await Loaded();

        vm.CycleLabel.Should().Be("Ciclo 1");
        vm.CycleTooltip.Should().Contain("no registra cuándo se abrió");
    }

    /// <summary>
    /// El contador decía «Sesiones: 7» contando TODAS las de la aplicación, de todos los ciclos y
    /// de todos los tipos. Ahora la etiqueta promete «este ciclo» y el número lo cumple.
    /// </summary>
    [Fact]
    public async Task Las_sesiones_que_se_cuentan_son_los_lanzamientos_de_este_ciclo()
    {
        SeedModules(1);
        _hub.Store.WriteSession(SystemSession(AuditMode.Lotes, cycleN: 1));
        _hub.Store.WriteSession(SystemSession(AuditMode.Reset, cycleN: 1));    // no lo lanza nadie
        _hub.Store.WriteSession(SystemSession(AuditMode.Lotes, cycleN: 2));    // otra vuelta

        InventoryViewModel vm = await Loaded();

        vm.SessionCount.Should().Be(1);
    }

    /// <summary>
    /// «Grandes» sale del panel (decisión del usuario), pero el panel tiene que seguir cuadrando:
    /// sin decirlo en algún sitio, auditadas + pendientes no suman el total y eso desconcierta más
    /// que el número que se quitó.
    /// </summary>
    [Fact]
    public async Task Lo_que_ya_no_sale_como_fila_se_explica_en_el_tooltip_de_unidades()
    {
        SeedInventory(
            ("M", "a.cs", UnitState.Pendiente),
            ("M", "b.cs", UnitState.Grande),
            ("M", "c.cs", UnitState.Grande));
        InventoryViewModel vm = await Loaded();

        vm.LargeUnits.Should().Be(2);
        vm.UnitsTooltip.Should().Contain("2 son demasiado grandes")
            .And.Contain("no cuentan como pendientes")
            .And.Contain("no impiden cerrar el ciclo");
    }

    [Fact]
    public async Task Sin_unidades_grandes_el_tooltip_no_habla_de_ellas()
    {
        SeedModules(1);
        InventoryViewModel vm = await Loaded();

        vm.UnitsTooltip.Should().Contain("Cada fichero que se audita por separado")
            .And.NotContain("Grande");
    }

    [Fact]
    public void El_panel_ya_no_enseña_el_recuento_de_grandes()
        => PanelMarkup().Should().NotContain("Grandes: ")
            .And.NotContain("LargeUnits", "el dato vive ahora en el tooltip de «Unidades»");

    [Fact]
    public void La_etiqueta_de_sesiones_dice_de_que_ciclo_habla()
        => PanelMarkup().Should().Contain("Sesiones este ciclo: ").And.NotContain("\"Sesiones: \"");

    /// <summary>
    /// La petición de F5.6 §5: cada dato del panel se explica solo. Un `TextBlock` de datos sin
    /// `ToolTip` es exactamente el que deja al compañero nuevo adivinando.
    /// </summary>
    [Fact]
    public void Cada_dato_del_panel_lleva_su_frase()
    {
        var rows = Regex.Matches(PanelMarkup(), "<TextBlock.*?(/>|</TextBlock>)", RegexOptions.Singleline);

        rows.Should().NotBeEmpty();
        foreach (Match row in rows)
        {
            row.Value.Should().Contain("ToolTip", $"este TextBlock del panel no se explica: {row.Value}");
        }
    }

    /// <summary>El bloque del resumen del ciclo, sin comentarios.</summary>
    private static string PanelMarkup()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        string xaml = File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", "InventoryView.xaml"));
        string markup = Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        int start = markup.IndexOf("Resumen del ciclo", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "el panel del resumen tiene que seguir ahí");
        return markup[start..];
    }

    private AuditSession SystemSession(AuditMode mode, int cycleN)
        => new()
        {
            Id = _ulids.NewUlid(), AppSlug = "app", Mode = mode, By = "alvaro", Machine = "m",
            StartedUtc = DateTimeOffset.UtcNow.AddDays(-2), EndedUtc = DateTimeOffset.UtcNow.AddDays(-2),
            CycleN = cycleN,
        };

    /// <summary>Una sesión pasada con coste medido por unidad, que es de donde sale la estimación.</summary>
    private void SeedCostHistory(int maxPasses, params decimal[] perUnitCosts)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alvaro",
            Machine = "m",
            StartedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            EndedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            MaxPassesPerUnit = maxPasses,
            CycleN = 1,
        };

        for (int i = 0; i < perUnitCosts.Length; i++)
        {
            session.UsageBreakdown.Add(new UnitUsageBreakdown { Unit = $"h{i}.cs", Cost = perUnitCosts[i] });
        }

        _hub.Store.WriteSession(session);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
