using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>End-to-end lotes pipeline with the injectable fake agent (§11).</summary>
public sealed class SessionCoordinatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public SessionCoordinatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-sess", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));

        var settings = new SettingsService(_paths);
        settings.Load(); // hub not configured → Sync is null → CommitAndPush is skipped (offline)
        _hub = TestFactory.Hub(_paths, settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);

        Seed();
    }

    private void Seed()
    {
        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A { void M() { } }");
        File.WriteAllText(Path.Combine(_clone, "B.cs"), "class B { void N() { } }");
        _machines.SetClonePath("app", _clone);

        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1 });
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

    private static SubmitFindingArgs SampleFinding(string path = "A.cs")
        => new("errores.recursos.no-liberado", "errores", "criterio", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation(path, 1, "snippet") }, "A.M");

    private SessionCoordinator NewCoordinator(FakeCopilotAgent agent)
        => new(_hub, _ingestion, _machines, _ulids, agent);

    [Fact]
    public async Task Lotes_session_ingests_findings_marks_audited_and_writes_session_and_report()
    {
        var agent = new FakeCopilotAgent(auditScript: r =>
            r.UnitPath == "A.cs" ? new[] { SampleFinding() } : Array.Empty<SubmitFindingArgs>());

        SessionResult result = await NewCoordinator(agent)
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        result.Counters.New.Should().Be(1);

        var findings = _hub.Store.ListFindings("app");
        findings.Should().ContainSingle();
        findings[0].Confidence.Should().Be(Confidence.Media);  // lotes → media
        findings[0].Severity.Should().Be(Severity.Critica);

        InventoryCycle inv = _hub.Store.TryReadInventory("app", 1)!;
        inv.Units.Single(u => u.Path == "A.cs").State.Should().Be(UnitState.Auditada);
        inv.Units.Single(u => u.Path == "B.cs").State.Should().Be(UnitState.Pendiente);

        _hub.Store.ListSessions("app").Should().ContainSingle();
        File.Exists(_hub.HubPaths.ReportFile("app", result.SessionId.ToString())).Should().BeTrue();

        // Claims were released.
        _hub.Store.ListClaims("app").Should().BeEmpty();
    }

    [Fact]
    public async Task Implicit_resolution_resolves_covered_and_not_rereported_findings()
    {
        // First session on A.cs reports a finding.
        await NewCoordinator(new FakeCopilotAgent(_ => new[] { SampleFinding() }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);
        _hub.Store.ListFindings("app").Single().Status.Should().Be(FindingStatus.Activo);

        // Second session on A.cs reports NOTHING → the finding is covered and not re-reported → resolved.
        await NewCoordinator(new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>()))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        _hub.Store.ListFindings("app").Single().Status.Should().Be(FindingStatus.Resuelto);
    }

    [Fact]
    public async Task Superficial_never_resolves_even_when_covered_and_not_rereported()
    {
        await NewCoordinator(new FakeCopilotAgent(_ => new[] { SampleFinding() }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        await NewCoordinator(new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>()))
            .RunAsync(new SessionRequest("app", AuditMode.Superficial, new[] { "A.cs" }), CancellationToken.None);

        _hub.Store.ListFindings("app").Single().Status.Should().Be(FindingStatus.Activo); // anti-degradation
    }

    [Fact]
    public async Task Silence_flow_suppresses_then_reappears_after_expiry()
    {
        // Detect the finding.
        await NewCoordinator(new FakeCopilotAgent(_ => new[] { SampleFinding() }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);
        Finding finding = _hub.Store.ListFindings("app").Single();

        // Silence it (human action) — no expiry, and mark the finding silenced.
        _hub.Store.WriteSilence("app", new Silence
        {
            Fingerprint = finding.Fingerprint,
            By = "maria",
            Utc = DateTimeOffset.UtcNow,
            Reason = SilenceReason.DeudaAceptada,
            FindingUlids = { finding.Id },
        });
        finding.MarkSilenced(DateTimeOffset.UtcNow, "maria", "silenced");
        _hub.Store.WriteFinding("app", finding);

        // Re-detection while silenced → suppressed, no new finding, stays silenced.
        SessionResult suppressed = await NewCoordinator(new FakeCopilotAgent(_ => new[] { SampleFinding() }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);
        suppressed.Counters.SilencedRespected.Should().Be(1);
        _hub.Store.ListFindings("app").Should().ContainSingle()
            .Which.Status.Should().Be(FindingStatus.Silenciado);

        // Expire the silence, then re-detect → the finding reappears (active).
        _hub.Store.WriteSilence("app", new Silence
        {
            Fingerprint = finding.Fingerprint,
            By = "maria",
            Utc = DateTimeOffset.UtcNow.AddDays(-2),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1),
            Reason = SilenceReason.DeudaAceptada,
        });

        await NewCoordinator(new FakeCopilotAgent(_ => new[] { SampleFinding() }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        _hub.Store.ListFindings("app").Should().ContainSingle()
            .Which.Status.Should().Be(FindingStatus.Activo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
