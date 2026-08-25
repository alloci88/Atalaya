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
/// F5.1 — la selección manual de V2 llega hasta una sesión real sobre una unidad YA auditada
/// (caso real: volver sobre ella tras arreglar sus hallazgos).
/// <para>
/// Se conduce la página entera: marcar la casilla de una unidad «auditada», pulsar «Auditar
/// selección» y comprobar que la sesión corre sobre esa unidad. Es el camino que un filtro por
/// estado —en la vista, en el view-model o al resolver las unidades— rompería en silencio.
/// </para>
/// </summary>
public sealed class ReAuditSelectionTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    /// <summary>F5.7 §4: lo que antes era `StatusMessage` ahora se lee en la cola de avisos.</summary>
    private readonly ToastCenter _toasts = new();

    private readonly ServiceProvider _provider;

    public ReAuditSelectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-reaudit", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;   // aquí se prueba la selección, no el barrido
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);

        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A { void M() { } }");
        File.WriteAllText(Path.Combine(_clone, "B.cs"), "class B { void N() { } }");
        _machines.SetClonePath("app", _clone);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                // Ya auditada: exactamente el caso que hay que poder re-auditar.
                new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Auditada },
                new InventoryUnit { Path = "B.cs", Module = "M", State = UnitState.Pendiente },
            },
        });

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_machines);
        services.AddSingleton(_settings);
        services.AddSingleton<IUlidFactory>(_ulids);
        services.AddSingleton<InventoryScanner>();
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<ICopilotAgent>(new FakeCopilotAgent(_ => new[] { Sample() }));
        services.AddSingleton<NavigationService>();
        services.AddSingleton(_paths);
        services.AddSingleton<OpenSessionStore>();
        services.AddSingleton(sp => new LiveSessionService(
            sp.GetRequiredService<SessionCoordinator>,
            sp.GetRequiredService<ICopilotAgent>(),
            sp.GetRequiredService<OpenSessionStore>()));
        services.AddTransient<SessionCoordinator>();
        services.AddTransient<SessionViewModel>();
        services.AddSingleton<CostEstimator>();
        services.AddSingleton<GroupExpansionMemory>();
        // Aquí se prueba el camino de la selección, no el diálogo: se confirma siempre.
        services.AddSingleton<IAuditLaunchConfirmer>(new AlwaysConfirms());
        services.AddSingleton(_toasts);
        services.AddTransient<InventoryViewModel>();
        _provider = services.BuildServiceProvider();
    }

    private sealed class AlwaysConfirms : IAuditLaunchConfirmer
    {
        public bool Confirm(AuditLaunchConfirmation confirmation) => true;
    }

    private static SubmitFindingArgs Sample()
        => new("errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation("A.cs", 1, "snippet") }, "A.M");

    private async Task<InventoryViewModel> LoadedInventory()
    {
        var vm = _provider.GetRequiredService<InventoryViewModel>();
        vm.SetApp("app");
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task An_audited_unit_is_listed_and_selectable()
    {
        InventoryViewModel vm = await LoadedInventory();

        UnitNode audited = vm.Modules.SelectMany(m => m.Units).Single(u => u.Path == "A.cs");
        audited.State.Should().Be(UnitState.Auditada);
        audited.StateLabel.Should().Be("Auditada");

        audited.IsSelected = true;
        audited.IsSelected.Should().BeTrue("nada debe impedir marcar una unidad ya auditada");
    }

    [Fact]
    public async Task Auditing_a_selected_audited_unit_launches_a_real_session_on_it()
    {
        InventoryViewModel vm = await LoadedInventory();
        var navigation = _provider.GetRequiredService<NavigationService>();

        vm.Modules.SelectMany(m => m.Units).Single(u => u.Path == "A.cs").IsSelected = true;
        await vm.AuditSelectionCommand.ExecuteAsync(null);

        // F5.2: lanzar es explícito y la sesión corre en el servicio; la navegación solo la enseña.
        LiveSessionService live = _provider.GetRequiredService<LiveSessionService>();
        await WaitUntilFinished(live);
        navigation.Current.Should().BeOfType<SessionViewModel>();
        AuditSession session = _hub.Store.ListSessions("app").Should().ContainSingle().Subject;
        session.Units.Should().ContainSingle().Which.Unit.Should().Be("A.cs");
        // El veredicto concreto lo decide el barrido (con tope 1 y hallazgos nuevos, la pasada no
        // llega a secarse); lo que se prueba aquí es que la unidad se auditó, no que se agotara.
        session.Units.Single().Verdict.Should().NotBe("no-localizado");
        _hub.Store.ListFindings("app").Should().ContainSingle(
            "la re-auditoría es una sesión normal: reporta como cualquier otra");
        _toasts.Items.Should().NotContain(t => t.Text.Contains("Selecciona al menos una unidad"));
    }

    [Fact]
    public async Task Clearing_the_selection_still_refuses_to_launch_an_empty_session()
    {
        InventoryViewModel vm = await LoadedInventory();

        await vm.AuditSelectionCommand.ExecuteAsync(null);

        _toasts.Items.Should().Contain(t => t.Text.Contains("Selecciona al menos una unidad"));
        _hub.Store.ListSessions("app").Should().BeEmpty();
    }

    /// <summary>La sesión corre en segundo plano: se espera a que cierre antes de comprobar.</summary>
    private static async Task WaitUntilFinished(LiveSessionService live)
    {
        for (int i = 0; i < 200 && !live.HasFinished; i++)
        {
            await Task.Delay(25);
        }

        live.HasFinished.Should().BeTrue("la sesión debería haber terminado ya");
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
