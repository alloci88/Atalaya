using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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
/// Blindaje del bucle de auto-relanzado (2026-08-25).
/// <para>
/// <c>LoadAsync</c> significa "recarga la vista" en todas las páginas, pero en ésta EJECUTA una
/// auditoría. El tick de polling (§3) llama a <c>Navigation.Current.LoadAsync()</c> cada vez que
/// un pull trae cambios, y una sesión termina haciendo commit+push — así que el poll se traía sus
/// propios cambios y relanzaba la auditoría cada 60 s, indefinidamente, mientras la página
/// siguiera abierta. Produjo 7 sesiones sobre <c>CommonStatics.cs</c> y volvió a contaminar el
/// baseline recién reseteado.
/// </para>
/// </summary>
public sealed class SessionViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly SettingsService _settings;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public SessionViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-vm", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        var paths = new AppPaths(Path.Combine(_root, "local"));

        _settings = new SettingsService(paths);
        _settings.Load();
        // F5.1: el tope del barrido vive en los ajustes de la máquina. Aquí 1, porque lo que se
        // prueba es el disparo de la sesión, no el barrido.
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);
        _hub = TestFactory.Hub(paths, _settings);
        _machines = new MachineConfigStore(paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);

        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A { void M() { } }");
        _machines.SetClonePath("app", _clone);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });
    }

    private static SubmitFindingArgs Sample()
        => new("errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation("A.cs", 1, "snippet") }, "A.M");

    /// <param name="gate">
    /// Si se pasa, <c>CheckAsync</c> se bloquea hasta que el test la abre. Es lo que hace
    /// DETERMINISTA la prueba de la carrera: garantiza que los tres disparos estan dentro de
    /// <c>Start</c> a la vez. Con un <c>Task.Yield</c> el planificador podia serializarlos y el
    /// test pasaba por suerte incluso con el bug presente.
    /// </param>
    private SessionViewModel NewViewModel(GatedAgent? gate = null)
    {
        var auditor = new FakeCopilotAgent(_ => new[] { Sample() });
        return new SessionViewModel(
            new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, auditor, _settings),
            (ICopilotAgent?)gate ?? auditor);
    }

    /// <summary>
    /// F5.1b: el resumen que ve el usuario tiene que nombrar las disputas. La primera sesión con
    /// una disputa real dijo «nuevos 0, confirmados 20, resueltos 0» y se calló que una de esas
    /// veinte era una discrepancia de criterio — un número sin causa, que es justo lo que D-060
    /// prohíbe.
    /// </summary>
    [Fact]
    public async Task The_live_summary_names_disputes_instead_of_hiding_them_among_the_confirmed()
    {
        Finding existing = SeedActiveFinding();
        var auditor = new FakeCopilotAgent(
            reconcileScript: _ => new[]
            {
                new VerdictArgs(existing.Id.ToString(), "no-es-defecto", "nunca fue un defecto"),
            });
        var vm = new SessionViewModel(
            new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, auditor, _settings),
            auditor);
        vm.Configure(Request(), new[] { "A.cs" });

        await vm.LoadAsync();

        vm.StatusMessage.Should().Contain("disputado");
        vm.StatusMessage.Should().Contain("los decides tú");
    }

    /// <summary>Siembra un hallazgo activo anclado a A.cs.</summary>
    private Finding SeedActiveFinding()
    {
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-2), AuditMode.Lotes, "viejo", "alvaro", "sha256:viejo", "modelo-anterior");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.recursos.no-liberado",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Checklist,
            Severity = Severity.Media,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = "Algo que ya estaba",
            Locations = { new Location("A.cs", 1, null) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            TimesConfirmed = 1,
        };
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    /// <summary>Agente cuyo <c>CheckAsync</c> espera a una compuerta que abre el test.</summary>
    private sealed class GatedAgent : ICopilotAgent
    {
        private readonly ICopilotAgent _inner = new FakeCopilotAgent();
        private readonly TaskCompletionSource _gate = new();

        /// <summary>Cuantas llamadas llegaron a la compuerta. Es LA medida del cerrojo.</summary>
        public int Entered;

        public void Open() => _gate.SetResult();

        public string? ModelName => _inner.ModelName;
        public event Action<string>? TextStreamed { add { } remove { } }
        public event Action<UsageSample>? UsageReported { add { } remove { } }

        public async Task<AgentReadiness> CheckAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Entered);
            await _gate.Task;
            return await _inner.CheckAsync(ct);
        }

        public async Task<bool> EnsureReadyAsync(CancellationToken ct) => (await CheckAsync(ct)).Ready;
        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct) => _inner.ListModelsAsync(ct);
        public Task AuditUnitAsync(AuditUnitRequest r, IAuditToolbox t, CancellationToken ct) => _inner.AuditUnitAsync(r, t, ct);
        public Task VerifyAsync(VerifyRequest r, IVerifyToolbox t, CancellationToken ct) => _inner.VerifyAsync(r, t, ct);
    }

    private static SessionRequest Request() => new("app", AuditMode.Lotes, new[] { "A.cs" });

    /// <summary>
    /// El caso exacto del incidente: entrar en la página arranca la sesión, y cada recarga
    /// posterior del tick de polling NO debe arrancar otra. Una configuración, una sesión.
    /// </summary>
    [Fact]
    public async Task Polling_reloads_do_not_relaunch_the_audit()
    {
        SessionViewModel vm = NewViewModel();
        vm.Configure(Request(), new[] { "A.cs" });

        await vm.LoadAsync();                       // navegación: arranca
        _hub.Store.ListSessions("app").Should().ContainSingle();

        for (int tick = 0; tick < 5; tick++)
        {
            await vm.LoadAsync();                   // ticks de polling: NO deben arrancar nada
        }

        _hub.Store.ListSessions("app").Should().ContainSingle(
            "una recarga de la vista no puede ejecutar una auditoría entera");
    }

    /// <summary>
    /// Y no basta con no relanzar: nada puede haberse resuelto por el camino. Es la mitad del
    /// ciclo duplicar→resolver que el bucle reintroducía sobre un baseline limpio.
    /// </summary>
    [Fact]
    public async Task Reloads_do_not_touch_the_findings()
    {
        SessionViewModel vm = NewViewModel();
        vm.Configure(Request(), new[] { "A.cs" });
        await vm.LoadAsync();

        var before = _hub.Store.ListFindings("app")
            .Select(f => (f.Id, f.Status, f.TimesConfirmed)).OrderBy(t => t.Id.ToString()).ToList();
        before.Should().NotBeEmpty("si la sesion no produjo hallazgos el test no comprueba nada");

        await vm.LoadAsync();
        await vm.LoadAsync();

        var after = _hub.Store.ListFindings("app")
            .Select(f => (f.Id, f.Status, f.TimesConfirmed)).OrderBy(t => t.Id.ToString()).ToList();

        after.Should().BeEquivalentTo(before);
    }

    /// <summary>Una configuración NUEVA sí rearma: el usuario puede lanzar otra sesión.</summary>
    [Fact]
    public async Task Configuring_again_allows_a_new_session()
    {
        SessionViewModel vm = NewViewModel();

        vm.Configure(Request(), new[] { "A.cs" });
        await vm.LoadAsync();

        vm.Configure(Request(), new[] { "A.cs" });
        await vm.LoadAsync();

        _hub.Store.ListSessions("app").Should().HaveCount(2);
    }

    /// <summary>
    /// La carrera, medida donde de verdad ocurre: cuantas llamadas atraviesan el guardia.
    /// <para>
    /// Con el cerrojo puesto DESPUES de <c>await CheckAsync</c>, tres disparos casi simultaneos
    /// (tick de polling + navegacion) pasaban los tres. Contamos las entradas al agente en vez de
    /// contar sesiones: contar sesiones no sirve porque dos ejecuciones concurrentes se estorban
    /// entre si y el test pasaria por accidente aunque el cerrojo estuviera mal.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_loads_pass_the_guard_exactly_once()
    {
        var gate = new GatedAgent();
        SessionViewModel vm = NewViewModel(gate);
        vm.Configure(Request(), new[] { "A.cs" });

        // Los tres entran mientras la compuerta sigue cerrada: nadie ha podido avanzar.
        Task a = vm.LoadAsync();
        Task b = vm.LoadAsync();
        Task c = vm.LoadAsync();

        gate.Entered.Should().Be(1, "solo el primer disparo puede atravesar el guardia");

        gate.Open();
        await Task.WhenAll(a, b, c);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
