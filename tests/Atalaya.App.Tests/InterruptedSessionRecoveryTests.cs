using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.2 Hito 3 — cierre de D-110: la sesión que muere con el proceso se cierra al arrancar.
/// <para>
/// La parada ordenada de F5.1b cubre el cierre cooperativo (botón «Detener», cerrar la ventana).
/// No cubre un <c>taskkill</c>, un cuelgue ni un corte de luz: ahí no corre ningún <c>finally</c>.
/// Como la ingesta persiste los hallazgos EN VIVO, ese corte dejaba el hub mutado sin registro de
/// sesión y con las unidades reclamadas hasta que caducara el TTL.
/// </para>
/// </summary>
public sealed class InterruptedSessionRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly OpenSessionStore _store;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public InterruptedSessionRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-recovery", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _store = new OpenSessionStore(_paths);

        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
    }

    private Ulid WriteMarker(int processId, DateTimeOffset? processStart, params string[] units)
    {
        Ulid id = _ulids.NewUlid();
        var marker = new OpenSessionMarker
        {
            SessionId = id.ToString(),
            Slug = "app",
            Mode = nameof(AuditMode.Lotes),
            Commit = "abc1234",
            By = "alvaro",
            Machine = "PC",
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            Units = units.ToList(),
            UnitsDone = 2,
        };
        _store.Write(marker);

        // Write() sella SIEMPRE el proceso actual; para simular otro hay que reescribir el fichero.
        OpenSessionMarker written = _store.TryRead()!;
        written.ProcessId = processId;
        written.ProcessStartedUtc = processStart;
        File.WriteAllText(_store.Path_, System.Text.Json.JsonSerializer.Serialize(written,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        return id;
    }

    private void SeedClaim(string unit) => _hub.Store.WriteClaim("app", new Claim
    {
        Unit = unit,
        Module = "M",
        By = "alvaro",
        Machine = "PC",
        Utc = DateTimeOffset.UtcNow.AddMinutes(-10),
    });

    private InterruptedSessionRecovery Recovery(bool processAlive)
        => new(_hub, _store, (_, _) => processAlive);

    [Fact]
    public void An_orphan_marker_is_recovered_recorded_and_reported()
    {
        Ulid id = WriteMarker(processId: 999_999, processStart: null, "A.cs", "B.cs");
        SeedClaim("A.cs");
        SeedClaim("B.cs");

        RecoveredSession? recovered = Recovery(processAlive: false).RecoverIfNeeded();

        recovered.Should().NotBeNull();
        recovered!.UnitsDone.Should().Be(2);
        recovered.ClaimsReleased.Should().Be(2);
        recovered.Message.Should().Contain("Se recuperó una sesión interrumpida");
        recovered.Message.Should().Contain("2 unidad(es) procesadas antes del corte");

        // (a) queda registro de sesión, marcado como interrumpido
        AuditSession session = _hub.Store.ListSessions("app").Should().ContainSingle().Subject;
        session.Id.Should().Be(id);
        session.Interrupted.Should().BeTrue();
        session.EndedUtc.Should().NotBeNull();
        session.Notes.Should().Contain(n => n.Contains("cerró de golpe"));

        // (b) los claims huérfanos se liberan: si no, bloqueaban a los demás hasta el TTL
        _hub.Store.ListClaims("app").Should().BeEmpty();

        // (c) y la marca desaparece: recuperar dos veces sería inventarse una sesión
        _store.TryRead().Should().BeNull();
    }

    [Fact]
    public void A_marker_whose_process_is_alive_is_left_completely_alone()
    {
        WriteMarker(processId: 4242, processStart: DateTimeOffset.UtcNow, "A.cs");
        SeedClaim("A.cs");

        RecoveredSession? recovered = Recovery(processAlive: true).RecoverIfNeeded();

        recovered.Should().BeNull("hay otra instancia auditando ahora mismo");
        _hub.Store.ListSessions("app").Should().BeEmpty("no se inventa un registro de una sesión viva");
        _hub.Store.ListClaims("app").Should().ContainSingle("y no se le quitan los claims de debajo");
        _store.TryRead().Should().NotBeNull("la marca sigue siendo suya");
    }

    [Fact]
    public void With_no_marker_there_is_nothing_to_recover()
        => Recovery(processAlive: false).RecoverIfNeeded().Should().BeNull();

    [Fact]
    public void A_corrupt_marker_is_treated_as_no_marker_instead_of_crashing_the_app()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_store.Path_)!);
        File.WriteAllText(_store.Path_, "{ esto no es json");

        Recovery(processAlive: false).RecoverIfNeeded().Should().BeNull();
    }

    /// <summary>
    /// El PID por sí solo no basta: los sistemas los reciclan. Sin comparar además el instante de
    /// arranque, un proceso ajeno que heredara el PID haría pasar por viva una sesión muerta y la
    /// recuperación no ocurriría nunca.
    /// </summary>
    [Fact]
    public void Liveness_compares_the_process_start_time_not_only_the_pid()
    {
        int mine = Environment.ProcessId;
        DateTimeOffset start = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

        OpenSessionStore.IsProcessAlive(mine, start).Should().BeTrue("es este mismo proceso");
        OpenSessionStore.IsProcessAlive(mine, start.AddHours(-3)).Should().BeFalse(
            "mismo PID pero arrancado en otro momento: es otro proceso");
        OpenSessionStore.IsProcessAlive(0, null).Should().BeFalse();
    }

    /// <summary>
    /// Una sesión que termina con normalidad NO deja marca: si la dejara, el siguiente arranque
    /// "recuperaría" una sesión que ya estaba perfectamente cerrada.
    /// </summary>
    [Fact]
    public async Task A_session_that_ends_normally_leaves_no_marker_behind()
    {
        string clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "A.cs"), "class A { }");
        var machines = new MachineConfigStore(_paths.MachinesJson);
        machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        var agent = new FakeCopilotAgent();
        var live = new LiveSessionService(
            () => new SessionCoordinator(
                _hub, new FindingIngestionService(_hub, _ulids), new ReconciliationService(_hub),
                machines, _ulids, agent, _settings),
            agent, _store);

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), new[] { "A.cs" });

        _store.TryRead().Should().BeNull("la sesión se cerró bien: no hay nada que recuperar");
        Recovery(processAlive: false).RecoverIfNeeded().Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
