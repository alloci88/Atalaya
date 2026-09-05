using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.4 — el rediseño de V3 Hallazgos.
/// <para>
/// <b>La lista encuentra; el detalle actúa.</b> Aquí se fija lo que la vista debe hacer (filtrar,
/// agrupar, contar) y —igual de importante— lo que ya NO puede hacer: escribir. Cada acción que
/// se le quitó a V3 se comprueba viva en V4, porque «quitar la botonera» y «perder la acción» se
/// parecen mucho desde fuera y solo se distinguen mirando dónde quedó.
/// </para>
/// <para>
/// El bug que dispara la mitad de estos tests: una vez filtrado por severidad no había forma de
/// volver a verlo todo — el combo no tenía «Todas». Filtrar era un viaje sin billete de vuelta.
/// </para>
/// </summary>
public sealed class FindingsViewTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly GovernanceService _governance;
    private readonly NavigationService _navigation;
    private readonly GroupExpansionMemory _expansion = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public FindingsViewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-v3", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        // Un clon de verdad: desde F5.8, verificar —que es auditar— exige repo git y origin que
        // coincida con el repo de la app.
        TestFactory.MakeClone(_clone, "https://example.invalid/org/alpha.git");
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _machines.SetClonePath("alpha", _clone);
        _governance = new GovernanceService(_hub, _ulids);
        _navigation = new NavigationService(new ServiceCollection().BuildServiceProvider());

        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "alpha", Name = "Alpha", RepoUrl = "https://example.invalid/org/alpha.git", CurrentCycle = 1,
        });
        _hub.Store.WriteApp(new AppConfig { Slug = "beta", Name = "Beta", RepoUrl = "u", CurrentCycle = 1 });
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

    private Finding Seed(
        string slug,
        string path,
        string title,
        Severity severity,
        string ruleId = "errores.recursos.no-liberado",
        FindingStatus status = FindingStatus.Activo,
        bool needsReview = false,
        string? assignee = null,
        string? disputedBy = null,
        int line = 12,
        int daysOld = 0)
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-daysOld), AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = ruleId,
            Pillar = Pillar.Errores,
            Severity = severity,
            Confidence = Confidence.Media,
            Status = status,
            Title = title,
            Locations = { new Location(path, line, CodeAnchor.ComputeSnippetHash(title)) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            NeedsReview = needsReview,
            Assignee = assignee,
            Resolved = status == FindingStatus.Resuelto
                ? new ResolutionStamp(stamp.Utc, ResolutionVia.Manual, AuditMode.Verify, "abc", "alvaro", "cerrado")
                : null,
        };

        if (disputedBy is not null)
        {
            f.Dispute(DateTimeOffset.UtcNow, "alvaro", disputedBy, "no es un defecto, es el patrón del proyecto");
        }

        _hub.Store.WriteFinding(slug, f);
        return f;
    }

    /// <summary>El escenario base: dos apps, tres unidades, los tres estados y una disputa.</summary>
    private void SeedPortfolio()
    {
        Seed("alpha", "src/Common.cs", "Fuga de conexión que nunca se cierra", Severity.Critica);
        Seed("alpha", "src/Common.cs", "Método demasiado largo para seguirlo de un vistazo", Severity.Media);
        Seed("alpha", "src/Otro.cs", "Consulta N+1 en el bucle de carga", Severity.Alta, disputedBy: "gpt-5");
        Seed("alpha", "src/Otro.cs", "Nombre poco claro", Severity.Baja, needsReview: true);
        Seed("beta", "app/Api.cs", "Sin validación de entrada", Severity.Alta, assignee: "maria lopez");
        Seed("beta", "app/Api.cs", "Esto ya se arregló", Severity.Media, status: FindingStatus.Resuelto);
        Seed("beta", "app/Api.cs", "Aceptado como deuda", Severity.Baja, status: FindingStatus.Silenciado);
    }

    private FindingsViewModel NewVm()
        => new(_hub, _navigation, _settings, _expansion);

    private async Task<FindingsViewModel> LoadedVm()
    {
        FindingsViewModel vm = NewVm();
        await vm.LoadAsync();
        return vm;
    }

    /// <summary>
    /// Los comandos que declara ESTA vista, no los que hereda. <c>DeclaredOnly</c> desde
    /// UI-AUDIT-1: <c>ViewModelBase.SubCrumbParentCommand</c> es un `ICommand` que toda página
    /// tiene, y no es una acción suya — es el puntero que la miga sigue para volver al eslabón de
    /// arriba (UI-0044). Contarlo aquí haría que la frontera V3/V4 fallara por algo que no es una
    /// acción de la lista.
    /// </summary>
    private static string[] CommandNames(Type t) => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
        .Select(p => p.Name)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

    // ------------------------------------------------- filtros: valores iniciales

    [Fact]
    public async Task Arranca_en_Todas_las_apps_Todas_las_severidades_y_solo_Activos()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.SelectedApp.Should().Be(FindingsViewModel.AllApps);
        vm.SelectedSeverity.Should().Be(FindingsViewModel.AllSeverities);
        vm.SelectedScope!.Value.Should().Be(FindingsScope.Activos);
        vm.HasActiveFilters.Should().BeFalse();

        // 7 sembrados, 2 fuera de «Activos» (uno resuelto y uno silenciado).
        vm.ResultCount.Should().Be(5);
    }

    [Fact]
    public async Task Todos_los_combos_de_filtro_ofrecen_una_opcion_neutra_y_es_la_primera()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.AppOptions[0].Slug.Should().BeNull();
        vm.AppOptions[0].Label.Should().Be("Todas");
        vm.SeverityOptions[0].Value.Should().BeNull();
        vm.SeverityOptions[0].Label.Should().Be("Todas");
        vm.ScopeOptions.Should().Contain(o => o.Value == FindingsScope.Todos);
    }

    [Fact]
    public async Task El_combo_de_aplicacion_lista_las_apps_del_portafolio_por_su_nombre()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.AppOptions.Select(o => o.Label).Should().Equal("Todas", "Alpha", "Beta");
        vm.AppOptions.Select(o => o.Slug).Should().Equal(null, "alpha", "beta");
    }

    // ------------------------------------------------- filtros: ida y VUELTA

    [Fact]
    public async Task Filtrar_por_severidad_y_volver_a_Todas_devuelve_la_lista_entera()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        int todos = vm.ResultCount;

        vm.SelectedSeverity = vm.SeverityOptions.Single(o => o.Value == Severity.Critica);
        vm.ResultCount.Should().Be(1);
        vm.HasActiveFilters.Should().BeTrue();

        // El billete de vuelta: ANTES de F5.4 esta línea no tenía a dónde ir.
        vm.SelectedSeverity = FindingsViewModel.AllSeverities;
        vm.ResultCount.Should().Be(todos);
        vm.HasActiveFilters.Should().BeFalse();
    }

    [Fact]
    public async Task Filtrar_por_aplicacion_y_volver_a_Todas_devuelve_la_lista_entera()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        int todos = vm.ResultCount;

        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "beta");
        vm.ResultCount.Should().Be(1);
        vm.Groups.Should().ContainSingle().Which.UnitPath.Should().Be("app/Api.cs");

        vm.SelectedApp = FindingsViewModel.AllApps;
        vm.ResultCount.Should().Be(todos);
    }

    [Fact]
    public async Task El_filtro_de_estado_recorre_activos_resueltos_silenciados_y_todos()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.ResultCount.Should().Be(5);

        vm.SelectedScope = vm.ScopeOptions.Single(o => o.Value == FindingsScope.Resueltos);
        vm.ResultCount.Should().Be(1);

        vm.SelectedScope = vm.ScopeOptions.Single(o => o.Value == FindingsScope.Silenciados);
        vm.ResultCount.Should().Be(1);

        vm.SelectedScope = vm.ScopeOptions.Single(o => o.Value == FindingsScope.Todos);
        vm.ResultCount.Should().Be(7);
    }

    [Fact]
    public async Task Los_filtros_se_combinan_entre_si()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "alpha");
        vm.SelectedSeverity = vm.SeverityOptions.Single(o => o.Value == Severity.Alta);
        vm.ResultCount.Should().Be(1);
        vm.Items.OfType<FindingRow>().Single().Title.Should().Be("Consulta N+1 en el bucle de carga");

        // Y una combinación imposible da vacío en vez de ignorar uno de los dos.
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "beta");
        vm.ResultCount.Should().Be(1);
        vm.SelectedSeverity = vm.SeverityOptions.Single(o => o.Value == Severity.Critica);
        vm.ResultCount.Should().Be(0);
        vm.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Los_toggles_de_needsReview_y_disputados_recortan_sobre_el_resto()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.OnlyDisputed = true;
        vm.ResultCount.Should().Be(1);
        vm.Items.OfType<FindingRow>().Single().IsDisputed.Should().BeTrue();

        vm.OnlyDisputed = false;
        vm.OnlyNeedsReview = true;
        vm.ResultCount.Should().Be(1);
        vm.Items.OfType<FindingRow>().Single().NeedsReview.Should().BeTrue();

        // Ambos a la vez: ningún hallazgo cumple los dos.
        vm.OnlyDisputed = true;
        vm.ResultCount.Should().Be(0);
    }

    [Fact]
    public async Task Buscar_mira_titulo_ruleId_y_ruta()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.SearchText = "N+1";
        vm.ResultCount.Should().Be(1);

        vm.SearchText = "recursos.no-liberado";
        vm.ResultCount.Should().Be(5);

        vm.SearchText = "Api.cs";
        vm.ResultCount.Should().Be(1);

        vm.SearchText = "  ";   // solo espacios no es un filtro
        vm.ResultCount.Should().Be(5);
        vm.HasActiveFilters.Should().BeFalse();
    }

    // ------------------------------------------------- limpiar filtros

    [Fact]
    public async Task Limpiar_filtros_restaura_TODOS_los_valores_por_defecto()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.SearchText = "fuga";
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "alpha");
        vm.SelectedSeverity = vm.SeverityOptions.Single(o => o.Value == Severity.Critica);
        vm.SelectedScope = vm.ScopeOptions.Single(o => o.Value == FindingsScope.Todos);
        vm.OnlyNeedsReview = true;
        vm.OnlyDisputed = true;
        vm.HasActiveFilters.Should().BeTrue();

        vm.ClearFiltersCommand.Execute(null);

        vm.SearchText.Should().BeEmpty();
        vm.SelectedApp!.Slug.Should().BeNull();
        vm.SelectedSeverity!.Value.Should().BeNull();
        vm.SelectedScope!.Value.Should().Be(FindingsScope.Activos);
        vm.OnlyNeedsReview.Should().BeFalse();
        vm.OnlyDisputed.Should().BeFalse();
        vm.HasActiveFilters.Should().BeFalse();
        vm.ResultCount.Should().Be(5);
    }

    [Fact]
    public async Task Limpiar_filtros_solo_se_ofrece_cuando_hay_algo_que_limpiar()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        vm.HasActiveFilters.Should().BeFalse();

        vm.OnlyNeedsReview = true;
        vm.HasActiveFilters.Should().BeTrue();
        vm.OnlyNeedsReview = false;
        vm.HasActiveFilters.Should().BeFalse();

        vm.SearchText = "x";
        vm.HasActiveFilters.Should().BeTrue();
        vm.SearchText = string.Empty;
        vm.HasActiveFilters.Should().BeFalse();
    }

    // ------------------------------------------------- el contador

    [Fact]
    public async Task El_contador_dice_cuantos_hay_y_nombra_los_disputados()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.ResultsSummary.Should().Be("5 hallazgos · 1 disputado");

        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "beta");
        vm.ResultsSummary.Should().Be("1 hallazgo");   // singular, y sin cola de disputas
    }

    /// <summary>
    /// El contador cuenta LO QUE SE VE, no el total del hub. Con el filtro puesto en una app y en
    /// un estado, el número de arriba tiene que hablar de ese recorte: un contador global junto a
    /// una lista filtrada son dos cifras que no cuadran, y la que se cree es la grande.
    /// </summary>
    [Fact]
    public async Task El_contador_cuenta_lo_FILTRADO_y_no_el_total()
    {
        SeedPortfolio();   // 7 hallazgos en total, 5 activos, 1 disputado
        FindingsViewModel vm = await LoadedVm();
        vm.ResultsSummary.Should().Be("5 hallazgos · 1 disputado");

        // Aplicación + estado a la vez.
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "beta");
        vm.SelectedScope = vm.ScopeOptions.Single(o => o.Value == FindingsScope.Resueltos);
        vm.ResultCount.Should().Be(1);
        vm.ResultsSummary.Should().Be("1 hallazgo");

        // La cola de disputas también es del recorte, no del hub.
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "alpha");
        vm.SelectedScope = vm.ScopeOptions.Single(o => o.Value == FindingsScope.Activos);
        vm.ResultsSummary.Should().Be("4 hallazgos · 1 disputado");

        // El disputado de alpha es Alta: al filtrar por Baja desaparece del recuento.
        vm.SelectedSeverity = vm.SeverityOptions.Single(o => o.Value == Severity.Baja);
        vm.DisputedCount.Should().Be(0);
        vm.ResultsSummary.Should().Be("1 hallazgo");

        // Y la búsqueda cuenta igual que los combos.
        vm.ClearFiltersCommand.Execute(null);
        vm.SearchText = "Consulta N+1";
        vm.ResultsSummary.Should().Be("1 hallazgo · 1 disputado");
    }

    [Fact]
    public async Task Sin_resultados_el_contador_lo_dice_y_la_vista_se_declara_vacia()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.SearchText = "esto no existe en ninguna parte";
        vm.ResultsSummary.Should().Be("0 hallazgos");
        vm.IsEmpty.Should().BeTrue();
        vm.Groups.Should().BeEmpty();
        vm.Items.Should().BeEmpty();
    }

    // ------------------------------------------------- agrupación por unidad

    [Fact]
    public async Task Agrupa_por_unidad_y_el_grupo_mas_severo_va_primero()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        // Api.cs va ANTES que Otro.cs por nombre, pero DESPUÉS de Common.cs por severidad: el
        // orden que sale no se explica por el alfabeto, que es justo lo que se está fijando.
        vm.Groups.Select(g => g.UnitPath)
            .Should().Equal("src/Common.cs", "app/Api.cs", "src/Otro.cs");

        FindingGroupHeader first = vm.Groups[0];
        first.FileName.Should().Be("Common.cs");          // el nombre en grande
        first.Subtitle.Should().Contain("src/Common.cs"); // la ruta completa al lado
        first.WorstSeverity.Should().Be(Severity.Critica);
        first.CountLabel.Should().Be("2 hallazgos");
        // LAS CUATRO RANURAS, SIEMPRE (UI-0024). Se filtraban las de cero, y por eso las pastillas
        // se empaquetaban a la derecha: la misma gravedad caía en una columna distinta según
        // cuántas tuviera la fila, y una pastilla roja aparecía donde el ojo ya se había
        // acostumbrado a ver azul. La que no tiene casos ocupa su sitio y no se pinta.
        first.Chips.Select(c => c.Label).Should().Equal("1 Crítica", "0 Altas", "1 Media", "0 Bajas");
    }

    [Fact]
    public async Task Con_el_filtro_en_Todas_la_app_va_en_la_cabecera_del_grupo_y_no_en_las_filas()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.Groups.Should().OnlyContain(g => g.ShowApp);
        vm.Groups.Single(g => g.Slug == "beta").Subtitle.Should().Be("Beta · app/Api.cs");

        // Filtrada la app, la cabecera deja de repetirla: ya se sabe cuál es.
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "beta");
        vm.Groups.Should().OnlyContain(g => !g.ShowApp);
        vm.Groups.Single().Subtitle.Should().Be("app/Api.cs");
    }

    [Fact]
    public async Task La_lista_intercala_cabeceras_y_filas_para_virtualizar_por_fila()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        // 3 cabeceras + 5 filas, y cada cabecera precede a las suyas.
        vm.Items.OfType<FindingGroupHeader>().Should().HaveCount(3);
        vm.Items.OfType<FindingRow>().Should().HaveCount(5);
        vm.Items[0].Should().BeOfType<FindingGroupHeader>();
        vm.Items[1].Should().BeOfType<FindingRow>();
    }

    [Fact]
    public async Task Plegar_un_grupo_retira_sus_filas_de_la_lista_y_el_pliegue_sobrevive_a_la_recarga()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        FindingGroupHeader common = vm.Groups.Single(g => g.UnitPath == "src/Common.cs");

        vm.ToggleGroupCommand.Execute(common);

        common.IsExpanded.Should().BeFalse();
        vm.Items.Should().Contain(common);
        vm.Items.OfType<FindingRow>().Should().HaveCount(3);   // las 2 de Common.cs ya no se pintan
        common.Rows.Should().HaveCount(2);                     // pero el grupo las sigue teniendo
        vm.ResultCount.Should().Be(5);                         // y el contador cuenta hallazgos, no filas

        // El polling recarga la página cada tick: plegar a mano no puede deshacerse solo.
        await vm.LoadAsync();
        vm.Groups.Single(g => g.UnitPath == "src/Common.cs").IsExpanded.Should().BeFalse();
        vm.Items.OfType<FindingRow>().Should().HaveCount(3);
    }

    // ------------------------------------------------- la fila

    [Fact]
    public async Task La_fila_lleva_el_titulo_ENTERO_y_sus_metadatos()
    {
        Seed("alpha", "src/Common.cs", "Fuga de conexión que nunca se cierra", Severity.Critica, assignee: "maria lopez");
        FindingsViewModel vm = await LoadedVm();

        FindingRow row = vm.Items.OfType<FindingRow>().Single();
        row.Title.Should().Be("Fuga de conexión que nunca se cierra");   // sin elipsis, sin recorte
        row.Meta.Should().Contain("confianza Media").And.Contain("L12");
        row.HasAssignee.Should().BeTrue();
        row.AssigneeInitials.Should().Be("ML");
        row.FreshnessLabel.Should().Be("hoy");
        row.UnitPath.Should().Be("src/Common.cs");
    }

    [Fact]
    public async Task La_frescura_enciende_el_semaforo_al_pasar_el_umbral()
    {
        int umbral = _settings.Current.Thresholds.FreshnessDays;
        Seed("alpha", "src/A.cs", "Reciente", Severity.Media, daysOld: 0);
        Seed("alpha", "src/B.cs", "Rancio", Severity.Media, daysOld: umbral + 5);

        FindingsViewModel vm = await LoadedVm();

        vm.Items.OfType<FindingRow>().Single(r => r.Title == "Reciente").IsStale.Should().BeFalse();
        FindingRow rancio = vm.Items.OfType<FindingRow>().Single(r => r.Title == "Rancio");
        rancio.IsStale.Should().BeTrue();
        rancio.FreshnessLabel.Should().Be($"hace {umbral + 5} días");
    }

    [Fact]
    public async Task Un_hallazgo_que_no_esta_activo_dice_su_estado_en_la_fila()
    {
        Seed("beta", "app/Servicio.cs", "Activo", Severity.Media);
        Seed("beta", "app/Servicio.cs", "Cerrado", Severity.Media, status: FindingStatus.Resuelto);
        FindingsViewModel vm = await LoadedVm();

        vm.SelectedScope = vm.ScopeOptions.Single(o => o.Value == FindingsScope.Todos);

        vm.Items.OfType<FindingRow>().Single(r => r.Title == "Activo").Meta.Should().NotContain("Resuelto");
        vm.Items.OfType<FindingRow>().Single(r => r.Title == "Cerrado").Meta.Should().StartWith("Resuelto");
    }

    // ------------------------------------------------- navegación desde V2

    [Fact]
    public async Task SetApp_preselecciona_la_aplicacion_al_entrar_desde_el_inventario()
    {
        SeedPortfolio();

        // Es el orden real de NavigateToAsync: inicializar y DESPUÉS cargar.
        FindingsViewModel vm = NewVm();
        vm.SetApp("beta");
        await vm.LoadAsync();

        vm.SelectedApp!.Slug.Should().Be("beta");
        vm.ResultCount.Should().Be(1);
        vm.HasActiveFilters.Should().BeTrue();

        // Y desde ahí se puede volver a verlo todo.
        vm.ClearFiltersCommand.Execute(null);
        vm.SelectedApp!.Slug.Should().BeNull();
        vm.ResultCount.Should().Be(5);
    }

    [Fact]
    public async Task Recargar_no_pierde_la_aplicacion_elegida_por_el_usuario()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "alpha");

        await vm.LoadAsync();

        vm.SelectedApp!.Slug.Should().Be("alpha");
    }

    [Fact]
    public async Task Una_app_nueva_aparece_en_el_combo_sin_tirar_la_seleccion_actual()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "alpha");

        _hub.Store.WriteApp(new AppConfig { Slug = "gamma", Name = "Gamma", RepoUrl = "u", CurrentCycle = 1 });
        await vm.LoadAsync();

        vm.AppOptions.Select(o => o.Label).Should().Equal("Todas", "Alpha", "Beta", "Gamma");
        vm.SelectedApp!.Slug.Should().Be("alpha");
    }

    // ------------------------------------------------- plegar y desplegar

    /// <summary>Siembra <paramref name="n"/> unidades distintas, una por grupo.</summary>
    private void SeedManyUnits(int n)
    {
        for (int i = 0; i < n; i++)
        {
            Seed("alpha", $"src/Modulo{i:00}/Servicio{i:00}.cs", $"Hallazgo de la unidad {i}", Severity.Alta);
        }
    }

    [Fact]
    public async Task Con_pocos_grupos_la_lista_abre_desplegada()
    {
        SeedPortfolio();   // 3 grupos activos
        FindingsViewModel vm = await LoadedVm();

        vm.Groups.Should().HaveCount(3).And.OnlyContain(g => g.IsExpanded);
        vm.AllCollapsed.Should().BeFalse();
        vm.ToggleAllLabel.Should().Be("Colapsar todo");
    }

    /// <summary>
    /// Una lista de treinta unidades abierta de par en par no se lee. Plegada es el resumen: la
    /// primera impresión tiene que ser legible en los dos tamaños.
    /// </summary>
    [Fact]
    public async Task Con_muchos_grupos_la_lista_abre_plegada()
    {
        SeedManyUnits(GroupExpansionMemory.SmallListGroups + 3);
        FindingsViewModel vm = await LoadedVm();

        vm.Groups.Should().HaveCount(GroupExpansionMemory.SmallListGroups + 3)
            .And.OnlyContain(g => !g.IsExpanded);
        vm.Items.Should().HaveCount(vm.Groups.Count, "plegado, cada grupo aporta solo su cabecera");
        vm.AllCollapsed.Should().BeTrue();
        vm.ToggleAllLabel.Should().Be("Expandir todo");
    }

    [Fact]
    public async Task El_umbral_se_aplica_al_filtrar_no_solo_al_abrir()
    {
        SeedManyUnits(GroupExpansionMemory.SmallListGroups + 3);
        Seed("beta", "app/Api.cs", "Sin validación", Severity.Alta);
        FindingsViewModel vm = await LoadedVm();
        vm.Groups.Should().OnlyContain(g => !g.IsExpanded);

        // Al recortar a una sola app quedan pocos grupos: los que nadie tocó vuelven a abrirse.
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "beta");
        vm.Groups.Should().ContainSingle().Which.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public async Task Con_todo_plegado_la_cabecera_se_sostiene_sola()
    {
        SeedManyUnits(GroupExpansionMemory.SmallListGroups + 3);
        FindingsViewModel vm = await LoadedVm();

        // Lo único que se ve es la cabecera: tiene que decir de qué clase habla y cuánto pesa.
        foreach (FindingGroupHeader group in vm.Groups)
        {
            group.FileName.Should().EndWith(".cs").And.NotBeEmpty();
            group.Chips.Should().NotBeEmpty();
            group.Chips.Sum(c => c.Count).Should().Be(group.Rows.Count);
            group.CountLabel.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task Un_solo_boton_alterna_entre_colapsar_todo_y_expandir_todo()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.ToggleAllGroupsCommand.Execute(null);
        vm.Groups.Should().OnlyContain(g => !g.IsExpanded);
        vm.Items.Should().HaveCount(3);
        vm.ToggleAllLabel.Should().Be("Expandir todo");

        vm.ToggleAllGroupsCommand.Execute(null);
        vm.Groups.Should().OnlyContain(g => g.IsExpanded);
        vm.Items.OfType<FindingRow>().Should().HaveCount(5);
        vm.ToggleAllLabel.Should().Be("Colapsar todo");
    }

    /// <summary>
    /// «Colapsar todo» es una decisión, no un efecto visual: el siguiente tick del polling recarga
    /// la página y no puede deshacerla.
    /// </summary>
    [Fact]
    public async Task Colapsar_todo_sobrevive_a_la_recarga()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        vm.ToggleAllGroupsCommand.Execute(null);

        await vm.LoadAsync();

        vm.Groups.Should().OnlyContain(g => !g.IsExpanded);
        vm.ToggleAllLabel.Should().Be("Expandir todo");
    }

    /// <summary>Con parte abierta y parte cerrada, la acción útil es cerrar: el botón lo ofrece.</summary>
    [Fact]
    public async Task Con_los_grupos_a_medias_el_boton_ofrece_colapsar()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        vm.ToggleGroupCommand.Execute(vm.Groups[0]);

        vm.AllCollapsed.Should().BeFalse();
        vm.ToggleAllLabel.Should().Be("Colapsar todo");

        vm.ToggleAllGroupsCommand.Execute(null);
        vm.Groups.Should().OnlyContain(g => !g.IsExpanded);
    }

    [Fact]
    public async Task Lo_que_decide_el_usuario_gana_a_la_regla_automatica()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        FindingGroupHeader common = vm.Groups.Single(g => g.UnitPath == "src/Common.cs");
        vm.ToggleGroupCommand.Execute(common);   // plegado a mano, con la lista corta

        // Sigue habiendo pocos grupos, así que la regla lo abriría; la decisión del usuario manda.
        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "alpha");
        vm.Groups.Single(g => g.UnitPath == "src/Common.cs").IsExpanded.Should().BeFalse();
        vm.Groups.Single(g => g.UnitPath == "src/Otro.cs").IsExpanded.Should().BeTrue();
    }

    /// <summary>
    /// V3 es transitoria: ir al detalle y volver construye un view-model nuevo. Si la memoria
    /// viviera dentro, el plegado se perdería en cada ida y vuelta — el gesto más frecuente aquí.
    /// </summary>
    [Fact]
    public async Task El_plegado_sobrevive_a_salir_de_la_vista_y_volver()
    {
        SeedPortfolio();
        FindingsViewModel primera = await LoadedVm();
        primera.ToggleGroupCommand.Execute(primera.Groups.Single(g => g.UnitPath == "src/Common.cs"));

        FindingsViewModel segunda = await LoadedVm();   // misma memoria de sesión, otro view-model

        segunda.Groups.Single(g => g.UnitPath == "src/Common.cs").IsExpanded.Should().BeFalse();
        segunda.Groups.Single(g => g.UnitPath == "src/Otro.cs").IsExpanded.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_grupos_no_hay_nada_que_plegar_y_el_control_se_retira()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();
        vm.HasGroups.Should().BeTrue();

        vm.SearchText = "no existe";
        vm.HasGroups.Should().BeFalse();
        vm.AllCollapsed.Should().BeFalse("sin grupos no se puede afirmar que estén todos plegados");
    }

    // ------------------------------------------------- la interfaz habla castellano

    /// <summary>
    /// Lee las etiquetas del propio XAML, como <see cref="ShellChromeTests"/>: lo que se escribe en
    /// un <c>Content</c> o un <c>PlaceholderText</c> es una propiedad de la PLANTILLA y no existe
    /// como estado que un view-model pueda devolver.
    /// </summary>
    private static string FindingsViewXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        string path = Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", "FindingsView.xaml");
        File.Exists(path).Should().BeTrue($"se esperaba la vista en {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Todo texto literal que el usuario llega a leer: etiquetas, placeholders y tooltips.</summary>
    private static IEnumerable<string> VisibleLabels(string xaml)
    {
        string markup = Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(markup, "(?:Text|Content|PlaceholderText|ToolTip)=\"([^\"]*)\""))
        {
            string value = m.Groups[1].Value;
            if (!value.StartsWith("{", StringComparison.Ordinal) && value.Trim().Length > 0)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// «needsReview» era el nombre de un campo del modelo puesto de etiqueta, y el placeholder de
    /// búsqueda nombraba «ruleId». La interfaz está en castellano; los identificadores se quedan en
    /// el código y en los ficheros del hub, que es donde significan algo.
    /// </summary>
    [Fact]
    public void Ninguna_etiqueta_visible_escribe_jerga_interna()
    {
        string[] jerga = { "needsReview", "ruleId", "displayId", "slug", "ULID", "fingerprint", "isStale" };
        var labels = VisibleLabels(FindingsViewXaml()).ToList();

        labels.Should().NotBeEmpty("si el barrido no encuentra etiquetas, no está probando nada");

        foreach (string label in labels)
        {
            foreach (string term in jerga)
            {
                label.Should().NotContainEquivalentOf(
                    term, $"«{term}» es del modelo de datos, no del usuario (etiqueta: «{label}»)");
            }
        }
    }

    /// <summary>
    /// El identificador de la enumeración va sin tilde porque C# no las lleva. Volcarlo con
    /// <c>ToString()</c> escribía «Critica» en una interfaz en castellano.
    /// </summary>
    [Fact]
    public async Task La_severidad_se_escribe_como_se_escribe_en_castellano()
    {
        SeedPortfolio();
        FindingsViewModel vm = await LoadedVm();

        vm.SeverityOptions.Select(o => o.Label)
            .Should().Equal("Todas", "Crítica", "Alta", "Media", "Baja");

        vm.Groups[0].Chips.Should().Contain(c => c.Label == "1 Crítica");
        vm.SeverityOptions.Should().NotContain(o => o.Label == "Critica");
    }

    // ------------------------------------------------- el ancho de la ventana

    /// <summary>
    /// La barra de filtros de V3 necesita <b>1064 px de página</b> para caber en una línea, y por
    /// eso la ventana no puede estrenarse pequeña: abría con 1180 y el último filtro caía a una
    /// segunda línea nada más arrancar. La regla es la PRIMERA IMPRESIÓN, no el mínimo — por
    /// debajo, los filtros se reacomodan y bajan de línea, que es el comportamiento correcto.
    /// <para>
    /// <b>F26 §A cambia el mecanismo y refuerza la regla</b> (D-956). Antes se fijaba un ancho
    /// declarado de 1314; el problema es que un número escrito en el XAML no sabe en qué monitor
    /// va a abrir, y en uno de 1920 dejaba media pantalla vacía —que es la queja que trae esta
    /// fase—. Ahora la ventana <b>arranca maximizada la primera vez</b>: la primera impresión es
    /// la pantalla entera, que cabe siempre. De la segunda en adelante manda lo que el usuario
    /// dejara, que es suyo.
    /// </para>
    /// </summary>
    [Fact]
    public void La_ventana_se_estrena_maximizada_para_que_los_filtros_quepan_en_una_linea()
    {
        // Una máquina donde Atalaya no se ha cerrado nunca: no hay preferencia que respetar.
        var (maximized, left, top, width, height) = WindowPlacementService.Resolve(
            new WindowPlacement(),
            new System.Windows.Rect(0, 0, 1920, 1080));

        maximized.Should().BeTrue("estrenar la aplicación en una ventana pequeña es estrenarla en su peor tamaño");
        left.Should().BeNull("sin posición guardada, la ventana se centra");
        top.Should().BeNull();

        // Y el mínimo respeta el principio 1: usable a 1280×720, nunca por debajo de 1100×700.
        width.Should().BeGreaterThanOrEqualTo(WindowPlacementService.MinWidth);
        height.Should().BeGreaterThanOrEqualTo(WindowPlacementService.MinHeight);
    }

    /// <summary>
    /// Y de la segunda vez en adelante se respeta lo que el usuario dejó — incluido haberla dejado
    /// pequeña, que es una decisión suya y no un defecto que corregir.
    /// </summary>
    [Fact]
    public void Despues_de_la_primera_vez_manda_lo_que_el_usuario_dejo()
    {
        var saved = new WindowPlacement
        {
            Saved = true, Maximized = false, Left = 100, Top = 80, Width = 1400, Height = 900,
        };

        var (maximized, left, top, width, height) = WindowPlacementService.Resolve(
            saved, new System.Windows.Rect(0, 0, 1920, 1080));

        maximized.Should().BeFalse();
        left.Should().Be(100);
        top.Should().Be(80);
        width.Should().Be(1400);
        height.Should().Be(900);
    }

    /// <summary>
    /// <b>Salvo que esa posición ya no exista.</b> Es el caso que rompe estas funciones en la vida
    /// real: se cierra Atalaya en el segundo monitor, se desconecta el monitor, y al abrirla
    /// vuelve a unas coordenadas que no están en ninguna pantalla — la ventana existe, responde y
    /// no se ve. Entonces se conserva el TAMAÑO y se descarta la posición.
    /// </summary>
    [Fact]
    public void Una_posicion_guardada_en_un_monitor_que_ya_no_esta_se_descarta()
    {
        var saved = new WindowPlacement
        {
            Saved = true, Maximized = false, Left = -2600, Top = 100, Width = 1400, Height = 900,
        };

        var (_, left, top, width, height) = WindowPlacementService.Resolve(
            saved, new System.Windows.Rect(0, 0, 1920, 1080));

        left.Should().BeNull("fuera de todo escritorio, la ventana se centra en vez de esconderse");
        top.Should().BeNull();
        width.Should().Be(1400, "el tamaño que eligió sí sigue siendo suyo");
        height.Should().Be(900);
    }

    [Theory]
    [InlineData(1340, 900, 1920, 1340)]   // cabe de sobra: se respeta lo pedido
    [InlineData(1340, 900, 1280, 1240)]   // pantalla al 150 %: se recorta al escritorio
    [InlineData(1340, 900, 800, 900)]     // pantalla diminuta: nunca por debajo del mínimo
    public void El_tamano_inicial_se_recorta_a_la_pantalla_pero_no_por_debajo_del_minimo(
        double desired, double minimum, double available, double expected)
        => StartupSize.Clamp(desired, minimum, available).Should().Be(expected);

    // ------------------------------------------------- la frontera V3 / V4

    [Fact]
    public void V3_no_expone_NINGUNA_accion_de_escritura()
    {
        // La lista exacta, no «contiene»: si aparece un comando nuevo hay que mirarlo y decidir si
        // escribe. Los cuatro que hay filtran, pliegan o navegan; ninguno toca el hub.
        //
        // Fueron seis durante la vista rápida (D-974): `ActivateRow` señalaba una fila y cargaba su
        // ficha para el panel, y `OpenSelected` navegaba a la ficha de la señalada. Los dos se
        // retiran con el panel (D-981) y `OpenDetail` vuelve a ser lo único que hace pulsar una
        // fila. Ninguno de los dos escribía, así que la frontera no se mueve: lo que cambia es que
        // hay menos superficie que vigilar.
        CommandNames(typeof(FindingsViewModel))
            .Should().Equal(
                "ClearFiltersCommand", "OpenDetailCommand",
                "ToggleAllGroupsCommand", "ToggleGroupCommand");
    }

    [Fact]
    public void V4_conserva_el_juego_COMPLETO_de_acciones_incluidas_las_que_bajaron_de_V3()
    {
        string[] commands = CommandNames(typeof(FindingDetailViewModel));

        commands.Should().Contain(new[]
        {
            // Ya vivían en V4.
            "SilenceCommand", "UnsilenceCommand", "ApplyAssignCommand", "ApplySeverityCommand",
            "ResolveManuallyCommand", "ReopenCommand", "AddCommentCommand", "GenerateFixPromptCommand",
            // Bajaron de V3 en F5.4. Si alguna faltase, la acción se habría PERDIDO, no cedido.
            "VerifyCommand", "AcceptDisputeCommand", "DismissDisputeCommand", "OpenInEditorCommand",
        });
    }

    [Fact]
    public void V4_silencia_y_asigna_lo_que_V3_ya_no_puede()
    {
        Finding f = Seed("alpha", "src/Common.cs", "Fuga", Severity.Critica);
        FindingDetailViewModel v4 = NewDetail();
        v4.Load("alpha", f.Id);

        v4.Assignee = "maria";
        v4.ApplyAssignCommand.Execute(null);
        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!.Assignee.Should().Be("maria");

        v4.SilenceReason = SilenceReason.DeudaAceptada;
        v4.SilenceNotes = "lo asumimos este trimestre";
        v4.SilenceCommand.Execute(null);

        Finding after = _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Silenciado);
        _hub.Store.TryReadSilence("alpha", f.Id)!.Reason.Should().Be(SilenceReason.DeudaAceptada);
    }

    [Fact]
    public void V4_cierra_la_disputa_dando_la_razon_al_que_discrepo()
    {
        Finding f = Seed("alpha", "src/Otro.cs", "Consulta N+1", Severity.Alta, disputedBy: "gpt-5");
        FindingDetailViewModel v4 = NewDetail();
        v4.Load("alpha", f.Id);
        v4.IsDisputed.Should().BeTrue();
        v4.DisputeSummary.Should().Contain("gpt-5");

        v4.SilenceNotes = "es el patrón del proyecto";
        v4.AcceptDisputeCommand.Execute(null);

        Finding after = _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Silenciado);
        after.Disputes.Should().BeEmpty();
        _hub.Store.TryReadSilence("alpha", f.Id)!.Reason.Should().Be(SilenceReason.FalsoPositivo);
        v4.IsDisputed.Should().BeFalse();
    }

    [Fact]
    public void V4_cierra_la_disputa_dando_la_razon_al_que_lo_reporto()
    {
        Finding f = Seed("alpha", "src/Otro.cs", "Consulta N+1", Severity.Alta, disputedBy: "gpt-5");
        FindingDetailViewModel v4 = NewDetail();
        v4.Load("alpha", f.Id);

        v4.DismissDisputeCommand.Execute(null);

        Finding after = _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;
        after.Disputes.Should().BeEmpty();
        after.Status.Should().Be(FindingStatus.Activo);   // sigue siendo un defecto: no se cierra nada
        after.History.Should().Contain(h => h.Event == FindingEvent.DisputeCleared);
    }

    [Fact]
    public async Task V4_verifica_un_hallazgo_suelto()
    {
        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A {\nvar x = Open();\n}");
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.recursos.no-liberado",
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = "Fuga",
            Locations = { new Location("A.cs", 2, CodeAnchor.ComputeSnippetHash("var x = Open();")) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("alpha", f);

        FindingDetailViewModel v4 = NewDetail(new FakeCopilotAgent(verdictScript: _ => "resuelto"));
        v4.Load("alpha", f.Id);

        await v4.VerifyCommand.ExecuteAsync(null);

        Finding after = _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Resuelto);
        after.Resolved!.Via.Should().Be(ResolutionVia.Verify);
    }

    private FindingDetailViewModel NewDetail(IAuditorProvider? agent = null)
        => new(
            _hub,
            _governance,
            _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, agent ?? new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines),
            new ToastCenter(),
            TestFactory.Links(_hub, _paths),
            TestFactory.LinkFlow(_hub, _paths));
}
