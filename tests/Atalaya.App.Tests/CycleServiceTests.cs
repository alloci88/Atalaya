using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

public sealed class CycleServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public CycleServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-cycle", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        var paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        _hub = new HubContext(paths, settings, NullLoggerFactory.Instance);
        _machines = new MachineConfigStore(paths.MachinesJson);
        _machines.SetClonePath("app", _clone);
        _ingestion = new FindingIngestionService(_hub, _ulids);

        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A {}");
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1 });
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Auditada } },
        });
    }

    private Finding SeedMediaFinding()
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            Fingerprint = Fingerprint.Compute("errores.x", "A.cs", "s", "t"),
            RuleId = "errores.x",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = "t",
            Locations = { new Location("A.cs", 1, null) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    [Fact]
    public void Close_promotes_media_to_alta_and_opens_next_cycle()
    {
        Finding f = SeedMediaFinding();
        var cycles = new CycleService(_hub, _ulids);

        bool closed = cycles.TryCloseCycle("app", 1);

        closed.Should().BeTrue();
        _hub.Store.TryReadFinding("app", f.Id.ToString())!.Confidence.Should().Be(Confidence.Alta);
        _hub.Store.TryReadApp("app")!.CurrentCycle.Should().Be(2);
        InventoryCycle next = _hub.Store.TryReadInventory("app", 2)!;
        next.Units.Should().OnlyContain(u => u.State == UnitState.Pendiente);
        _hub.Store.ListSessions("app").Should().Contain(s => s.Mode == AuditMode.Cierre);
    }

    [Fact]
    public void Close_desists_when_cycle_already_advanced()
    {
        SeedMediaFinding();
        var cycles = new CycleService(_hub, _ulids);
        cycles.TryCloseCycle("app", 1).Should().BeTrue();

        // A second attempt to close cycle 1 must desist (already at cycle 2).
        cycles.TryCloseCycle("app", 1).Should().BeFalse();
    }

    [Fact]
    public void Close_desists_when_pending_remains()
    {
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        new CycleService(_hub, _ulids).TryCloseCycle("app", 1).Should().BeFalse();
    }

    [Fact]
    public async Task Integral_session_auditing_everything_closes_the_cycle()
    {
        // Fresh app with one pending unit; integral audits it → 0 pending → close.
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        var coordinator = new SessionCoordinator(
            _hub, _ingestion, _machines, _ulids,
            new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>()),
            new CycleService(_hub, _ulids), new StatusExporter(_hub, _machines));

        SessionResult result = await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Integral, Array.Empty<string>()), CancellationToken.None);

        result.ReachedZeroPending.Should().BeTrue();
        result.CycleClosed.Should().BeTrue();
        _hub.Store.TryReadApp("app")!.CurrentCycle.Should().Be(2);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
