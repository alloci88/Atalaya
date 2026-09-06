using Atalaya.App.Services;
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
/// F30 §2c — <b>UNA SESIÓN QUE FALLA SE VE; NO SE QUEDA GIRANDO</b>.
/// <para>
/// <b>El agujero, encontrado reproduciendo otra cosa.</b> Buscando el cuelgue de una auditoría real
/// se montó un banco con un <c>Dispatcher</c> de verdad, y lo primero que hizo fue quedarse diez
/// segundos sin decir nada. La causa no era la que se buscaba —era una dependencia sin registrar en
/// el propio banco—, pero el <b>mecanismo</b> sí es del producto: quien lanza una sesión descartaba
/// su tarea (<c>_ = StartAsync(…)</c>), así que un fallo lanzado ANTES del <c>try</c> de
/// <c>RunAsync</c> —construir el coordinador, resolver una dependencia— no lo recogía nadie. La
/// sesión se quedaba con <c>IsRunning</c> en true: el giro puesto, «Detener» sin nada que detener y
/// ni una línea en ninguna parte que dijera qué había pasado.
/// </para>
/// <para>
/// Es exactamente la forma del cuelgue que se reportó, y por eso entra la red aunque su causa
/// concreta esté todavía por confirmar: una sesión colgada sin causa visible es lo único que no
/// puede pasar.
/// </para>
/// </summary>
public sealed class SessionClosesTests : IDisposable
{
    private const string Slug = "app";
    private const string UnitPath = "A.cs";

    /// <summary>
    /// Se provoca el fallo a propósito —la fábrica del coordinador revienta— y se exige lo único
    /// que no puede faltar: que la sesión <b>deje de correr</b> y que el motivo quede escrito.
    /// </summary>
    [Fact]
    public async Task Una_sesion_que_revienta_al_arrancar_no_se_queda_girando()
    {
        var agent = new FakeCopilotAgent();
        var live = new LiveSessionService(
            () => throw new InvalidOperationException("no se pudo montar el coordinador"),
            agent,
            _provider.GetRequiredService<OpenSessionStore>(),
            _hub);

        live.Observe(live.StartAsync(
            new SessionRequest(Slug, AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath }));

        for (int i = 0; i < 200 && live.IsRunning; i++)
        {
            await Task.Delay(10);
        }

        live.IsRunning.Should().BeFalse("una sesión que revienta tiene que dejar de decir que corre");
        live.HasFailed.Should().BeTrue("y decir que falló");
        live.FailureMessage.Should().Contain("coordinador", "con el motivo, no en blanco");
    }

    /// <summary>
    /// Y una sesión normal, con la narración conectada, cierra. Sin esto el test de arriba pasaría
    /// igual con una sesión que no arranca nunca, y no probaría nada.
    /// </summary>
    [Fact]
    public async Task Una_sesion_normal_con_narracion_conectada_cierra()
    {
        var narrado = new List<ActivityNote>();
        var agent = new FakeCopilotAgent(auditScript: _ => new[]
        {
            new SubmitFindingArgs(
                "errores.recursos.no-liberado", "errores", "critica", "Conn leaked",
                "desc", "impact", "reco", new[] { new SubmitLocation(UnitPath, 1, "s") }, "A.M"),
        });

        var live = new LiveSessionService(
            () =>
            {
                var coordinator = new SessionCoordinator(
                    _hub, _provider.GetRequiredService<FindingIngestionService>(),
                    _provider.GetRequiredService<ReconciliationService>(), _machines, _ulids, agent, _settings);
                coordinator.ActivityNoted += narrado.Add;
                return coordinator;
            },
            agent, _provider.GetRequiredService<OpenSessionStore>(), _hub);

        SessionResult? result = null;
        live.Completed += r => result = r;
        live.Observe(live.StartAsync(
            new SessionRequest(Slug, AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath }));

        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        live.IsRunning.Should().BeFalse("la sesión tiene que cerrar");
        result.Should().NotBeNull("y avisar de que cerró");
        live.HasFinished.Should().BeTrue();
        narrado.Should().NotBeEmpty("con la narración conectada, que es lo que esta fase añadió");
    }

    // ---------------------------------------------------------------- andamiaje

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly SettingsService _settings;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly ServiceProvider _provider;

    public SessionClosesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-cierre", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));

        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_ulids);
        services.AddSingleton<IUlidFactory>(_ulids);
        services.AddSingleton(_paths);
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<OpenSessionStore>();
        _provider = services.BuildServiceProvider();

        File.WriteAllText(Path.Combine(_clone, UnitPath), "class A { void M() { } }");
        _machines.SetClonePath(Slug, _clone);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug, Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        _hub.Store.WriteInventory(Slug, new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = UnitPath, Module = "M", State = UnitState.Pendiente } },
        });
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
        }
    }
}
