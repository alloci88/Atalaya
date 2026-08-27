using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

public sealed class GovernanceTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly GovernanceService _gov;

    public GovernanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-gov", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        var paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        _hub = TestFactory.Hub(paths, settings);
        _machines = new MachineConfigStore(paths.MachinesJson);
        _machines.SetClonePath("app", _clone);
        _gov = new GovernanceService(_hub, _ulids);

        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
    }

    private Finding SeedFinding(string line = "var conn = Open();")
    {
        File.WriteAllText(Path.Combine(_clone, "A.cs"), $"class A {{\n{line}\n}}");
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.recursos.no-liberado",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = "leak",
            Locations = { new Location("A.cs", 2, CodeAnchor.ComputeSnippetHash(line)) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    [Fact]
    public void Silence_then_unsilence_toggles_status_and_silence_file()
    {
        Finding f = SeedFinding();
        _gov.Silence("app", f.Id, SilenceReason.FalsoPositivo, "no aplica", expiresUtc: null);

        _hub.Store.TryReadFinding("app", f.Id.ToString())!.Status.Should().Be(FindingStatus.Silenciado);
        _hub.Store.TryReadSilence("app", f.Id).Should().NotBeNull();

        _gov.Unsilence("app", f.Id);
        _hub.Store.TryReadFinding("app", f.Id.ToString())!.Status.Should().Be(FindingStatus.Activo);
        _hub.Store.TryReadSilence("app", f.Id).Should().BeNull();
    }

    [Fact]
    public void Assign_severity_comment_and_manual_resolution_are_recorded()
    {
        Finding f = SeedFinding();

        _gov.Assign("app", f.Id, "maria");
        _gov.ChangeSeverity("app", f.Id, Severity.Critica);
        _gov.AddComment("app", f.Id, "revisando");
        _gov.ResolveManually("app", f.Id, "arreglado en PR #12", "deadbee");

        Finding after = _hub.Store.TryReadFinding("app", f.Id.ToString())!;
        after.Assignee.Should().Be("maria");
        after.Severity.Should().Be(Severity.Critica);
        after.Status.Should().Be(FindingStatus.Resuelto);
        after.Resolved!.Via.Should().Be(ResolutionVia.Manual);
        _hub.Store.ListComments("app", f.Id.ToString()).Should().ContainSingle();
        after.History.Select(h => h.Event).Should().Contain(new[]
        {
            FindingEvent.Assigned, FindingEvent.SeverityChanged, FindingEvent.Resolved,
        });
    }

    [Fact]
    public void FixPrompt_contains_acceptance_criteria_and_is_saved_as_comment()
    {
        Finding f = SeedFinding();
        string prompt = FixPromptBuilder.Build(f);

        // F6.8: los criterios de §5.7 pasaron a ser las «reglas del arreglo», endurecidas alrededor
        // del contrato observable. Lo que exigían sigue exigiéndose.
        prompt.Should().Contain("Reglas del arreglo");
        prompt.Should().Contain("Arregla SOLO este hallazgo");
        prompt.Should().Contain(f.RuleId);

        _gov.AddComment("app", f.Id, prompt, kind: "fix-prompt");
        _hub.Store.ListComments("app", f.Id.ToString()).Single().Kind.Should().Be("fix-prompt");
    }

    [Fact]
    public async Task Verify_applies_verdicts_confirmado_and_resuelto()
    {
        Finding f = SeedFinding();
        var agent = new FakeCopilotAgent(verdictScript: _ => "resuelto");
        var verify = new VerifyCoordinator(_hub, _machines, _ulids, agent);

        VerifyOutcome outcome = await verify.RunAsync("app", new[] { f.Id }, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        _hub.Store.TryReadFinding("app", f.Id.ToString())!.Status.Should().Be(FindingStatus.Resuelto);
        _hub.Store.TryReadFinding("app", f.Id.ToString())!.Resolved!.Via.Should().Be(ResolutionVia.Verify);
    }

    /// <summary>
    /// F6.6: el ancla perdida ya NO termina el verify — pero cuando además no hay símbolo ni la
    /// unidad ha cambiado desde el último avistamiento, no queda nada que juzgar y sigue siendo
    /// «no localizado». Lo que nunca puede ser es «resuelto», y eso es lo que este test vigila.
    /// </summary>
    [Fact]
    public async Task Verify_lost_anchor_with_nothing_to_judge_sets_needsReview_not_resolved()
    {
        Finding f = SeedFinding("var conn = Open();");

        // El fichero es OTRO, y el hallazgo se vio por última vez contra este mismo contenido: no
        // hay ancla, no hay símbolo («leak» no nombra ningún miembro) y nada ha cambiado desde
        // entonces, así que no queda ninguna pregunta honrada que hacerle al auditor.
        string file = Path.Combine(_clone, "A.cs");
        File.WriteAllText(file, "class A {\n// completely different\n}");
        f.LastConfirmed = f.LastConfirmed with { UnitContentHash = HashUtil.Sha256Hex(File.ReadAllBytes(file)) };
        _hub.Store.WriteFinding("app", f);

        var agent = new FakeCopilotAgent(verdictScript: _ => "resuelto");
        var verify = new VerifyCoordinator(_hub, _machines, _ulids, agent);

        await verify.RunAsync("app", new[] { f.Id }, CancellationToken.None);

        Finding after = _hub.Store.TryReadFinding("app", f.Id.ToString())!;
        after.NeedsReview.Should().BeTrue();
        after.Status.Should().Be(FindingStatus.Activo); // "no localizado" ≠ "resuelto"
        after.History[^1].Event.Should().Be(FindingEvent.NotLocated, "un hallazgo activo no se reabre");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
