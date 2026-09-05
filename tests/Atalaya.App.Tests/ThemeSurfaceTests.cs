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
/// F17 §2 — <b>dónde se ve la temática</b>: el filtro de Hallazgos, la ficha, la tarjeta del
/// Portafolio y el panel del ciclo. Un distintivo, no un párrafo; y en Hallazgos un combo más que
/// arranca en «Todas» y vuelve ahí con «Limpiar filtros», como los demás.
/// </summary>
public sealed class ThemeSurfaceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public ThemeSurfaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-themesurface", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig { Slug = "alpha", Name = "Alpha", RepoUrl = "u", CurrentCycle = 1 });
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

    private Finding Seed(string title, AuditTheme theme)
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.recursos.no-liberado",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = title,
            Theme = theme,
            Locations = { new Location("src/A.cs", 12, CodeAnchor.ComputeSnippetHash(title)) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("alpha", f);
        return f;
    }

    private FindingsViewModel Findings()
    {
        var vm = new FindingsViewModel(_hub, new NavigationService(new ServiceCollection().BuildServiceProvider()), _settings, new GroupExpansionMemory());
        vm.LoadAsync().GetAwaiter().GetResult();
        return vm;
    }

    // ---------------------------------------------------------------- Hallazgos

    [Fact]
    public void El_filtro_de_tematica_arranca_en_Todas_y_recorta_por_la_lupa_que_lo_detecto()
    {
        Seed("Credencial", AuditTheme.Seguridad);
        Seed("Doble recorrido", AuditTheme.Rendimiento);
        Seed("Nulo", AuditTheme.General);
        FindingsViewModel vm = Findings();

        vm.SelectedTheme.Should().Be(FindingsViewModel.AllThemes);
        vm.ThemeOptions.Should().HaveCount(7).And.Subject.First().Label.Should().Be("Todas");
        vm.ResultCount.Should().Be(3);

        vm.SelectedTheme = vm.ThemeOptions.Single(o => o.Value == AuditTheme.Rendimiento);
        vm.ResultCount.Should().Be(1);
        vm.Items.OfType<FindingRow>().Single().Title.Should().Be("Doble recorrido");
        vm.HasActiveFilters.Should().BeTrue();

        vm.ClearFiltersCommand.Execute(null);
        vm.SelectedTheme.Should().Be(FindingsViewModel.AllThemes);
        vm.ResultCount.Should().Be(3);
    }

    /// <summary>Lo anterior a F17 no trae temática y se filtra como General: era la única mirada.</summary>
    [Fact]
    public void Un_hallazgo_anterior_a_F17_cae_en_General()
    {
        Finding legacy = Seed("Viejo", AuditTheme.General);
        string path = _hub.HubPaths.FindingFile("alpha", legacy.Id.ToString());
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"tematica\": \"general\"", "\"x\": 0"));
        FindingsViewModel vm = Findings();

        vm.SelectedTheme = vm.ThemeOptions.Single(o => o.Value == AuditTheme.General);

        vm.ResultCount.Should().Be(1);
    }

    [Fact]
    public void La_vista_de_hallazgos_trae_el_combo_de_tematica()
    {
        string xaml = File.ReadAllText(Source("src/Atalaya.App/Views/FindingsView.xaml"));

        xaml.Should().Contain("{Binding ThemeOptions}").And.Contain("{Binding SelectedTheme}");
    }

    // ---------------------------------------------------------------- la ficha

    [Fact]
    public void La_ficha_dice_con_que_lupa_se_detecto()
    {
        Finding f = Seed("Credencial", AuditTheme.Seguridad);
        var machines = new MachineConfigStore(_paths.MachinesJson);
        var toasts = new ToastCenter();
        var vm = new FindingDetailViewModel(
            _hub, new GovernanceService(_hub, _ulids), machines,
            new VerifyCoordinator(_hub, machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, machines), toasts,
            TestFactory.Links(_hub, _paths), TestFactory.LinkFlow(_hub, _paths, toasts));

        vm.Load("alpha", f.Id);

        vm.Meta.Should().Contain(m => m.Label == "Temática" && m.Value == "Seguridad");
        int origen = vm.Meta.ToList().FindIndex(m => m.Label == "Origen");
        vm.Meta.ToList().FindIndex(m => m.Label == "Temática").Should().Be(origen + 1, "va pegada al origen: es del ciclo");
    }

    // ---------------------------------------------------------------- Portafolio y panel

    [Fact]
    public void La_tarjeta_del_portafolio_lleva_la_lupa_del_ciclo_vigente()
    {
        _hub.Store.WriteInventory("alpha", new InventoryCycle
        {
            CycleN = 1,
            Theme = AuditTheme.Concurrencia,
            Units = { new InventoryUnit { Path = "src/A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        AppCard card = new PortfolioQuery(_hub.Store).Build("alpha")!;

        card.Theme.Should().Be(AuditTheme.Concurrencia);
        card.ThemeLabel.Should().Be("Concurrencia y asincronía");
        card.ThemeTooltip.Should().Contain("no sustituye a uno General");

        // La temática se ESCRIBE, no se tiñe (UI-0019, UI-0050). Era una pastilla aquí y en el
        // resumen del ciclo, y texto corrido en la ficha y en el informe: el mismo dato con dos
        // formas según la puerta. Y su relleno usaba grises que no están en ninguna paleta —el
        // único par que fallaba AA en los DOS temas—. Lo que la tarjeta tiene que enseñar es el
        // nombre de la lupa; el color no decía nada que el nombre no dijera.
        string xaml = File.ReadAllText(Source("src/Atalaya.App/Views/PortfolioView.xaml"));
        xaml.Should().Contain("{Binding ThemeLabel}").And.NotContain("ThemeToBrush");
    }

    [Fact]
    public void Una_app_sin_ciclo_configurado_es_General_en_la_tarjeta()
        => new PortfolioQuery(_hub.Store).Build("alpha")!.Theme.Should().Be(AuditTheme.General);

    [Fact]
    public void El_panel_del_ciclo_ensena_la_lupa_y_ofrece_configurarla()
    {
        string xaml = File.ReadAllText(Source("src/Atalaya.App/Views/InventoryView.xaml"));

        xaml.Should().Contain("{Binding CycleThemeLabel}")
            .And.Contain("ConfigureCycleCommand")
            .And.Contain("Configurar ciclo")
            .And.Contain("{Binding PreferredModelLabel, Mode=OneWay}");
    }

    /// <summary>Los dos convertidores del distintivo existen y están registrados en la aplicación.</summary>
    [Fact]
    public void El_distintivo_tiene_su_color_y_su_nombre_registrados()
    {
        // F26 §A: los converters salieron de `App.xaml` a `Themes/Converters.xaml`, para que los
        // estilos del sistema —que son un diccionario fusionado— puedan pedirlos. Siguen
        // declarados una sola vez; lo que cambia es en qué fichero.
        string converters = File.ReadAllText(Source("src/Atalaya.App/Themes/Converters.xaml"));

        converters.Should().Contain("x:Key=\"ThemeToLabel\"", "el NOMBRE de la lupa se escribe");
        converters.Should().NotContain("x:Key=\"ThemeToBrush\"",
            "su color se retiró con la pastilla (UI-0019): eran grises fuera de paleta que no "
            + "llegaban a AA en ninguno de los dos temas, y dejarlo declarado deja el atajo abierto");
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
