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
/// F5.13 — el alcance de un lanzamiento. Lo que se prueba aquí es el invariante que el incidente
/// del 2026-08-26 rompió: <b>lo que dice el contador, lo que dice el diálogo y lo que se audita son
/// la misma lista</b>, y esa lista solo contiene unidades-hoja.
/// <para>
/// El coste de equivocarse aquí no es un pixel mal puesto: es una auditoría entera que nadie pidió,
/// facturada. Por eso hay además una salvaguarda que compara lo confirmado con lo que se va a
/// auditar y mata la sesión antes de la primera llamada al modelo.
/// </para>
/// </summary>
public sealed class LaunchScopeTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly ToastCenter _toasts = new();
    private readonly ServiceProvider _provider;
    private readonly MachineConfigStore _machines;
    private readonly RecordingConfirmer _confirmer = new();
    private const string RepoUrl = "https://example.invalid/org/app.git";

    /// <summary>Recuerda qué N se le enseñó al usuario y acepta. El diálogo real no aparece.</summary>
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

    public LaunchScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-scope", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        // El clon tiene los ficheros de verdad: una unidad que no está en disco se salta con
        // «no-localizado» y el test no distinguiría «no la auditó» de «no llegó a la lista».
        string clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(clone, RepoUrl);
        var cycle = new InventoryCycle { CycleN = 1 };
        for (int m = 0; m < 3; m++)
        {
            string dir = Path.Combine(clone, "src", $"M{m:00}");
            Directory.CreateDirectory(dir);
            foreach (string name in new[] { "A", "B" })
            {
                File.WriteAllText(Path.Combine(dir, $"{name}{m:00}.cs"), $"class {name}{m} {{ }}");
                cycle.Units.Add(new InventoryUnit
                {
                    Path = $"src/M{m:00}/{name}{m:00}.cs",
                    Module = $"M{m:00}",
                    State = UnitState.Pendiente,
                });
            }
        }

        _machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", cycle);

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_paths);
        services.AddSingleton(_settings);
        services.AddSingleton(_machines);
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
        services.AddSingleton(_toasts);
        services.AddSingleton<CloneLinkService>();
        services.AddSingleton<InventoryRescanService>();
        services.AddSingleton<IFolderPicker, TestFactory.NoFolderPicker>();
        services.AddSingleton<ILinkCloneDialog, TestFactory.NoLinkCloneDialog>();
        services.AddSingleton<LinkCloneFlow>();
        services.AddSingleton<GovernanceService>();
        services.AddSingleton<IPatternSilencesDialog, TestFactory.NoPatternSilencesDialog>();
        services.AddTransient<InventoryViewModel>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private async Task<InventoryViewModel> Loaded()
    {
        var vm = _provider.GetRequiredService<InventoryViewModel>();
        vm.SetApp("app");
        await vm.LoadAsync();
        return vm;
    }

    private static UnitNode Unit(InventoryViewModel vm, string path)
        => vm.Modules.SelectMany(m => m.Units).Single(u => u.Path == path);

    /// <summary>Lanza y espera a que la sesión termine, para poder leer lo que realmente auditó.</summary>
    private async Task<AuditSession> LaunchAndWait(InventoryViewModel vm)
    {
        var live = _provider.GetRequiredService<LiveSessionService>();
        await vm.AuditSelectionCommand.ExecuteAsync(null);
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        live.IsRunning.Should().BeFalse("la sesión de prueba tiene que haber terminado");
        return _hub.Store.ListSessions("app").Should().ContainSingle().Subject;
    }

    // ============================================================ §1 una sola fuente de verdad

    /// <summary>
    /// El caso del incidente: UNA clase marcada. El grupo se pinta indeterminado, el contador dice
    /// 1, y lo que se audita es exactamente esa unidad.
    /// </summary>
    [Fact]
    public async Task Una_hija_marcada_lanza_esa_unidad_y_solo_esa()
    {
        InventoryViewModel vm = await Loaded();
        Unit(vm, "src/M00/A00.cs").IsSelected = true;

        vm.Modules.Single(m => m.Name == "M00").IsChecked
            .Should().BeNull("el padre NUNCA se marca por tener una hija marcada");
        vm.SelectedUnits().Should().ContainSingle().Which.Should().Be("src/M00/A00.cs");
        vm.SelectedCount.Should().Be(1);

        AuditSession session = await LaunchAndWait(vm);

        session.Units.Should().ContainSingle()
            .Which.Unit.Should().Be("src/M00/A00.cs", "se audita lo que dice el contador, ni una más");
    }

    /// <summary>
    /// Marcar el padre a mano es un gesto legítimo: marca sus hijas. Lo que NUNCA ocurre es que el
    /// grupo entre como si fuera una unidad — un módulo no tiene ruta que auditar.
    /// </summary>
    [Fact]
    public async Task El_padre_marcado_entrega_sus_hijas_y_nunca_el_grupo()
    {
        InventoryViewModel vm = await Loaded();
        ModuleNode module = vm.Modules.Single(m => m.Name == "M01");

        module.IsChecked = true;

        vm.SelectedUnits().Should().BeEquivalentTo(new[] { "src/M01/A01.cs", "src/M01/B01.cs" });
        vm.SelectedUnits().Should().NotContain("M01").And.NotContain(module.Name);
        vm.SelectedCount.Should().Be(2);

        AuditSession session = await LaunchAndWait(vm);
        session.Units.Select(u => u.Unit).Should()
            .BeEquivalentTo(new[] { "src/M01/A01.cs", "src/M01/B01.cs" });
    }

    /// <summary>
    /// <b>La regresión del incidente.</b> Con el módulo indeterminado, un clic en su casilla es un
    /// gesto de CORRECCIÓN: limpia. Antes, con <c>IsThreeState="False"</c>, WPF mandaba ese clic a
    /// «marcado» y te llevabas el módulo entero a la selección justo cuando intentabas deshacer.
    /// </summary>
    [Fact]
    public async Task Un_clic_en_el_modulo_indeterminado_limpia_en_vez_de_marcarlo_entero()
    {
        InventoryViewModel vm = await Loaded();
        Unit(vm, "src/M00/A00.cs").IsSelected = true;
        ModuleNode module = vm.Modules.Single(m => m.Name == "M00");
        module.IsChecked.Should().BeNull();

        // Lo que hace WPF con IsThreeState="False" al pulsar sobre un indeterminado.
        module.IsChecked = true;

        vm.SelectedUnits().Should().BeEmpty("pulsar para deshacer no puede seleccionar el módulo entero");
        vm.SelectedCount.Should().Be(0);
        module.IsChecked.Should().Be(false);
    }

    /// <summary>Y desde vacío el mismo clic sí marca el módulo entero: es el gesto que se espera.</summary>
    [Fact]
    public async Task Un_clic_en_el_modulo_vacio_lo_marca_entero()
    {
        InventoryViewModel vm = await Loaded();
        ModuleNode module = vm.Modules.Single(m => m.Name == "M00");

        module.IsChecked = true;

        vm.SelectedUnits().Should().BeEquivalentTo(new[] { "src/M00/A00.cs", "src/M00/B00.cs" });
        module.IsChecked.Should().Be(true);
    }

    /// <summary>Un módulo lleno se limpia de un clic, como siempre.</summary>
    [Fact]
    public async Task Un_clic_en_el_modulo_lleno_lo_limpia()
    {
        InventoryViewModel vm = await Loaded();
        ModuleNode module = vm.Modules.Single(m => m.Name == "M00");
        module.IsChecked = true;

        module.IsChecked = false;

        vm.SelectedUnits().Should().BeEmpty();
        module.IsChecked.Should().Be(false);
    }

    /// <summary>
    /// El contador, el diálogo y la sesión dicen el MISMO número. Es el invariante entero en un
    /// solo test: los tres leen <see cref="InventoryViewModel.SelectedUnits"/>.
    /// </summary>
    [Fact]
    public async Task El_contador_el_dialogo_y_la_sesion_dicen_el_mismo_numero()
    {
        InventoryViewModel vm = await Loaded();
        vm.SelectPendingCommand.Execute(null);   // 6 unidades: por encima del umbral, se pregunta

        int counter = vm.SelectedCount;
        AuditSession session = await LaunchAndWait(vm);

        counter.Should().Be(6);
        vm.SelectedUnits().Count.Should().Be(counter);
        _confirmer.Asked.Should().ContainSingle().Which.Estimate.Units.Should().Be(counter);
        session.Units.Should().HaveCount(counter);
    }

    /// <summary>Filtrar la vista no cambia lo que se va a auditar: la lista es del TOTAL.</summary>
    [Fact]
    public async Task Filtrar_no_cambia_la_lista_de_lanzamiento()
    {
        InventoryViewModel vm = await Loaded();
        Unit(vm, "src/M00/A00.cs").IsSelected = true;

        vm.SearchText = "M02";
        vm.SelectedUnits().Should().ContainSingle().Which.Should().Be("src/M00/A00.cs");
        vm.SelectedCount.Should().Be(1);
    }

    // ============================================================ §2 salvaguarda de última línea

    /// <summary>
    /// Si alguna vez la lista vuelve a superar lo confirmado, la sesión muere ANTES de llamar al
    /// modelo: sin sesión registrada, sin claims publicados y sin un solo token gastado.
    /// </summary>
    [Fact]
    public async Task El_coordinador_aborta_si_se_lanza_mas_de_lo_confirmado()
    {
        var coordinator = _provider.GetRequiredService<SessionCoordinator>();
        var request = new SessionRequest(
            "app", AuditMode.Lotes,
            new[] { "src/M00/A00.cs", "src/M00/B00.cs", "src/M01/A01.cs" },
            ConfirmedUnits: 1);

        Func<Task> act = () => coordinator.RunAsync(request, CancellationToken.None);

        LaunchMismatchException ex = (await act.Should().ThrowAsync<LaunchMismatchException>()).Which;
        ex.Confirmed.Should().Be(1);
        ex.Actual.Should().Be(3);
        ex.Message.Should().Contain("ni se ha gastado nada").And.Contain("Lanzamiento abortado");

        _hub.Store.ListSessions("app").Should().BeEmpty("una sesión abortada no es una sesión");
        _hub.Store.ListClaims("app").Should().BeEmpty("ni se llegó a reclamar nada");
    }

    /// <summary>Lo confirmado exacto pasa. La salvaguarda protege del exceso, no del uso normal.</summary>
    [Fact]
    public async Task Lo_confirmado_exacto_se_audita_sin_estorbo()
    {
        var coordinator = _provider.GetRequiredService<SessionCoordinator>();
        var request = new SessionRequest(
            "app", AuditMode.Lotes, new[] { "src/M00/A00.cs" }, ConfirmedUnits: 1);

        SessionResult result = await coordinator.RunAsync(request, CancellationToken.None);

        result.SessionId.Should().NotBe(Ulid.Empty);
        _hub.Store.ListSessions("app").Single().Units.Should().ContainSingle();
    }

    /// <summary>
    /// Sin N declarado no hay nada que comprobar: los caminos que no vienen de la barra de
    /// selección siguen funcionando igual.
    /// </summary>
    [Fact]
    public async Task Sin_N_confirmado_la_salvaguarda_no_estorba()
    {
        var coordinator = _provider.GetRequiredService<SessionCoordinator>();
        var request = new SessionRequest("app", AuditMode.Lotes, new[] { "src/M00/A00.cs", "src/M00/B00.cs" });

        SessionResult result = await coordinator.RunAsync(request, CancellationToken.None);

        result.SessionId.Should().NotBe(Ulid.Empty);
        _hub.Store.ListSessions("app").Single().Units.Should().HaveCount(2);
    }

    /// <summary>
    /// El camino de verdad ARMA la salvaguarda: la peticion que sale del Inventario lleva el N que
    /// el usuario acaba de ver. Se lee del fuente porque es lo unico que un test puede interrogar
    /// —el coordinador lo construye LiveSessionService por dentro— y porque el fallo que hay que
    /// impedir es justo que alguien retire el argumento sin enterarse.
    /// </summary>
    [Fact]
    public void El_lanzamiento_desde_la_barra_declara_el_N_confirmado()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        string source = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Atalaya.App", "ViewModels", "InventoryViewModel.cs"));

        source.Should().Contain(
            "new SessionRequest(Slug, mode, paths, paths.Count)",
            "sin el N confirmado la salvaguarda del coordinador queda desarmada");
    }
}
