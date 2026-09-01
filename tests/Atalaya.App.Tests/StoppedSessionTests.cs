using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.1b — detener una sesión es un final ORDENADO, no un aborto.
/// <para>
/// El 2026-08-25 a las 12:13 (local) un hallazgo del hub quedó confirmado sin que existiera
/// fichero de sesión ni informe para esa hora. La causa está en el código, no en el misterio: la
/// ingesta persiste los hallazgos EN VIVO, pero al cancelar, <c>RunAsync</c> lanzaba
/// <c>OperationCanceledException</c> y se saltaba todo lo posterior al bucle de unidades — el
/// registro de sesión, el informe, la liberación de claims y el commit+push. Resultado: el hub
/// mutado sin traza de quién lo hizo, claims retenidos, y nada publicado.
/// </para>
/// </summary>
public sealed class StoppedSessionTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public StoppedSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-stop", Guid.NewGuid().ToString("N"));
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
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);

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
                new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente },
                new InventoryUnit { Path = "B.cs", Module = "M", State = UnitState.Pendiente },
            },
        });
    }

    /// <summary>
    /// Agente que reporta un hallazgo en la primera unidad y, justo después, cancela: reproduce a
    /// alguien pulsando «Detener» con la sesión a medias.
    /// </summary>
    private sealed class StopsAfterFirstUnit : ICopilotAgent
    {
        private readonly CancellationTokenSource _cts;
        private readonly SubmitFindingArgs _finding;

        public StopsAfterFirstUnit(CancellationTokenSource cts, SubmitFindingArgs finding)
        {
            _cts = cts;
            _finding = finding;
        }

        public int UnitsSeen { get; private set; }

        public string? ModelName => "stop-test";
        public event Action<string>? TextStreamed { add { } remove { } }
        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));
        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            UnitsSeen++;
            toolbox.SubmitFindings(new[] { _finding with { Locations = new[] { new SubmitLocation(request.UnitPath, 1, null) } } });
            UsageReported?.Invoke(new UsageSample(10, 5, null, ModelName));
            toolbox.UnitDone(request.UnitPath, "revisado (parcial)");

            // El usuario pulsa Detener justo cuando termina la primera unidad.
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static SubmitFindingArgs Sample()
        => new("errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation("A.cs", 1, null) }, "A.M");

    private async Task<(SessionResult Result, StopsAfterFirstUnit Agent)> RunAndStop()
    {
        var cts = new CancellationTokenSource();
        var agent = new StopsAfterFirstUnit(cts, Sample());
        var coordinator = new SessionCoordinator(
            _hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings);

        SessionResult result = await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs", "B.cs" }), cts.Token);
        return (result, agent);
    }

    [Fact]
    public async Task Stopping_still_writes_the_session_record()
    {
        (SessionResult result, _) = await RunAndStop();

        result.Interrupted.Should().BeTrue();
        AuditSession session = _hub.Store.ListSessions("app").Should().ContainSingle(
            "una sesión detenida que mutó el hub NO puede quedarse sin registro").Subject;
        session.Interrupted.Should().BeTrue();
        session.EndedUtc.Should().NotBeNull();
        session.Units.Should().ContainSingle().Which.Unit.Should().Be("A.cs");
        session.Notes.Should().Contain(n => n.Contains("detenida por el usuario"));
    }

    [Fact]
    public async Task Stopping_still_writes_the_report_and_says_it_was_stopped()
    {
        (SessionResult result, _) = await RunAndStop();

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));
        report.Should().Contain("Sesión detenida por el usuario");
        report.Should().Contain("A.cs");
    }

    [Fact]
    public async Task What_was_audited_before_the_stop_is_kept_and_counted()
    {
        (SessionResult result, StopsAfterFirstUnit agent) = await RunAndStop();

        agent.UnitsSeen.Should().Be(1, "la segunda unidad no llegó a auditarse");
        result.Counters.New.Should().Be(1);
        _hub.Store.ListFindings("app").Should().ContainSingle(
            "la ingesta persiste en vivo: ese hallazgo ya estaba escrito antes de la parada");
    }

    [Fact]
    public async Task Stopping_releases_the_claims_instead_of_holding_the_units_hostage()
    {
        await RunAndStop();

        _hub.Store.ListClaims("app").Should().BeEmpty(
            "sin liberarlos, la unidad quedaba reclamada hasta que caducara el TTL y bloqueaba a los demás");
    }

    [Fact]
    public async Task Only_the_units_actually_audited_are_marked_as_audited()
    {
        await RunAndStop();

        InventoryCycle inventory = _hub.Store.TryReadInventory("app", 1)!;
        inventory.Units.Single(u => u.Path == "A.cs").State.Should().Be(UnitState.Auditada);
        inventory.Units.Single(u => u.Path == "B.cs").State.Should().Be(UnitState.Pendiente,
            "no se auditó: marcarla sería mentir sobre la cobertura");
    }

    /// <summary>
    /// Una parada que llega cuando YA se han procesado todas las unidades pedidas no es una
    /// interrupción: la cobertura prometida se cumplió. Lo que marca una sesión es haber dejado
    /// unidades sin tocar, no el botón.
    /// </summary>
    [Fact]
    public async Task A_stop_after_the_last_unit_is_not_an_interruption()
    {
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        var cts = new CancellationTokenSource();
        var agent = new StopsAfterFirstUnit(cts, Sample());
        SessionResult result = await new SessionCoordinator(
                _hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), cts.Token);

        result.Interrupted.Should().BeFalse();
        _hub.Store.ListSessions("app").Single().Interrupted.Should().BeFalse();
    }

    [Fact]
    public async Task A_stopped_session_never_closes_the_cycle()
    {
        // A.cs pendiente y B.cs grande: al auditar A y detenerse antes de B, el ciclo se queda con
        // CERO pendientes (las grandes no cuentan) pero la sesión sí dejó una unidad sin procesar.
        // Es el único caso en el que la guarda de cierre importa, y por eso se prueba así.
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente },
                new InventoryUnit { Path = "B.cs", Module = "M", State = UnitState.Grande },
            },
        });

        var cts = new CancellationTokenSource();
        var agent = new StopsAfterFirstUnit(cts, Sample());
        var coordinator = new SessionCoordinator(
            _hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings,
            new CycleService(_hub, _ulids, _settings));

        SessionResult result = await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs", "B.cs" }), cts.Token);

        result.Interrupted.Should().BeTrue();
        result.ReachedZeroPending.Should().BeTrue("las unidades grandes no cuentan como pendientes");
        result.CycleClosed.Should().BeFalse("una sesión detenida no ha cubierto lo que decía cubrir");
        _hub.Store.TryReadApp("app")!.CurrentCycle.Should().Be(1);
    }

    [Fact]
    public async Task Stopping_before_any_unit_is_audited_is_still_a_clean_close()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();   // detenido antes de empezar
        var coordinator = new SessionCoordinator(
            _hub, _ingestion, _reconciliation, _machines, _ulids, new FakeCopilotAgent(), _settings);

        SessionResult result = await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), cts.Token);

        result.Interrupted.Should().BeTrue();
        result.Counters.New.Should().Be(0);
        _hub.Store.ListSessions("app").Should().ContainSingle().Which.Units.Should().BeEmpty();
        _hub.Store.ListClaims("app").Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
