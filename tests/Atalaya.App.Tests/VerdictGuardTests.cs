using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Tests;
using FluentAssertions;
using LibGit2Sharp;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.1b — «arreglado» exige EVIDENCIA DE CAMBIO, y discrepar tiene su propia casilla.
/// <para>
/// El 2026-08-25, con el baseline de <c>CommonStatics.cs</c>, un modelo declaró «arreglado» un
/// hallazgo detectado y confirmado sobre el MISMO commit (<c>f86a301</c> en los tres sellos) y la
/// app lo cerró. No era un arreglo: era una discrepancia de criterio con el modelo anterior sobre
/// la semántica de <c>using</c>. El clon es aquí un repositorio git de verdad porque la guarda se
/// apoya en commits reales.
/// </para>
/// </summary>
public sealed class VerdictGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly GovernanceService _governance;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    private const string OriginalUnit = "class A { void M() { using var s = new S(); } }";

    public VerdictGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-verdict", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;   // aquí se prueban los veredictos, no el barrido
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);
        _governance = new GovernanceService(_hub, _ulids);

        // Un clon git REAL: la capa 1 de la guarda compara commits, así que necesitan ser de verdad.
        TestGit.Init(_clone);
        Write("A.cs", OriginalUnit);
        Write("B.cs", "class B { }");
        Commit("inicial");

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

    private void Write(string name, string content)
        => File.WriteAllText(Path.Combine(_clone, name), content);

    private void Commit(string message)
    {
        using var repo = new Repository(_clone);
        Commands.Stage(repo, "*");
        var who = new Signature("Test", "test@example.com", DateTimeOffset.Now);
        repo.Commit(message, who, who);
    }

    private string HeadSha() => GitInfo.HeadSha(_clone);

    private string HashOf(string name)
        => HashUtil.Sha256Hex(File.ReadAllBytes(Path.Combine(_clone, name)));

    /// <summary>Siembra un hallazgo activo visto por última vez con estos sellos.</summary>
    private Finding SeedExisting(string commit, string? unitHash, string model = "modelo-anterior", string slug = "app")
    {
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, commit, "alvaro", unitHash, model);
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.recursos.idisposable-no-liberado",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Checklist,
            Severity = Severity.Media,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = "El recurso no se dispone si el constructor lanza",
            Locations = { new Location("A.cs", 1, null) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            TimesConfirmed = 1,
        };
        _hub.Store.WriteFinding(slug, f);
        return f;
    }

    private Task<SessionResult> RunWithVerdict(Finding target, string verdict, string? model = null)
    {
        var agent = new FakeCopilotAgent(
            reconcileScript: _ => new[] { new VerdictArgs(target.Id.ToString(), verdict, "razonamiento del auditor") },
            modelName: model ?? "modelo-nuevo");

        var coordinator = new SessionCoordinator(
            _hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings);

        return coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);
    }

    private string Report(SessionResult result)
        => File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));

    // ---------- 1. Guarda de evidencia de cambio ----------

    [Fact]
    public async Task Fixed_on_the_same_commit_is_degraded_instead_of_resolved()
    {
        Finding seeded = SeedExisting(HeadSha(), HashOf("A.cs"));

        SessionResult result = await RunWithVerdict(seeded, "arreglado");

        Finding after = _hub.Store.TryReadFinding("app", seeded.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Activo, "sin cambio de código no puede haberse arreglado");
        after.Resolved.Should().BeNull();
        result.Counters.Resolved.Should().Be(0);
        result.Counters.ResolutionsRefused.Should().Be(1);
        result.Counters.Confirmed.Should().Be(1, "degradar es confirmar, no descartar el veredicto");
    }

    [Fact]
    public async Task Fixed_with_a_new_commit_but_an_unchanged_unit_is_degraded_too()
    {
        Finding seeded = SeedExisting(HeadSha(), HashOf("A.cs"));

        // El repositorio avanza, pero la unidad auditada NO cambia.
        Write("B.cs", "class B { int otro; }");
        Commit("cambia otro fichero");
        HeadSha().Should().NotBe(seeded.LastConfirmed.Commit, "el commit sí ha cambiado");

        SessionResult result = await RunWithVerdict(seeded, "arreglado");

        _hub.Store.TryReadFinding("app", seeded.Id.ToString())!.Status.Should().Be(FindingStatus.Activo);
        result.Counters.Resolved.Should().Be(0);
        result.Counters.ResolutionsRefused.Should().Be(1,
            "sin la segunda capa, un commit en cualquier otra parte del repo colaría la resolución");
    }

    [Fact]
    public async Task Fixed_after_the_unit_really_changed_resolves_normally()
    {
        Finding seeded = SeedExisting(HeadSha(), HashOf("A.cs"));

        // Ahora sí: la unidad cambia y se commitea. Ésta es la vía legítima.
        Write("A.cs", "class A { void M() { using (var s = new S()) { } } }");
        Commit("arregla el using");

        SessionResult result = await RunWithVerdict(seeded, "arreglado");

        Finding after = _hub.Store.TryReadFinding("app", seeded.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Resuelto);
        after.Resolved!.Via.Should().Be(ResolutionVia.Auditor);
        result.Counters.Resolved.Should().Be(1);
        result.Counters.ResolutionsRefused.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_commit_never_blocks_a_legitimate_resolution()
    {
        // GitInfo devuelve "unknown" cuando el clon NO es un repositorio git. Dos "unknown" no
        // prueban que el código sea el mismo: prueban que no lo sabemos. Tratarlos como prueba
        // bloquearía toda resolución legítima en un clon sin git — un falso positivo de la guarda.
        // Hace falta un clon sin git de verdad para que ambos lados valgan "unknown".
        string plain = Path.Combine(_root, "sin-git");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "A.cs"), OriginalUnit);
        _machines.SetClonePath("nogit", plain);
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "nogit", Name = "Sin git", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        _hub.Store.WriteInventory("nogit", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        GitInfo.HeadSha(plain).Should().Be("unknown", "el arnés depende de este centinela");
        Finding seeded = SeedExisting("unknown", unitHash: null, slug: "nogit");

        var agent = new FakeCopilotAgent(
            reconcileScript: _ => new[] { new VerdictArgs(seeded.Id.ToString(), "arreglado", "ya no está") });
        SessionResult result = await new SessionCoordinator(
                _hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest("nogit", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        result.Counters.Resolved.Should().Be(1, "la duda favorece al auditor: la guarda nunca acusa en falso");
        result.Counters.ResolutionsRefused.Should().Be(0);
    }

    [Fact]
    public async Task A_degraded_resolution_is_named_in_the_session_and_the_report()
    {
        Finding seeded = SeedExisting(HeadSha(), HashOf("A.cs"));

        SessionResult result = await RunWithVerdict(seeded, "arreglado");

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Notes.Should().Contain(n => n.Contains("veredicto degradado")
                                            && n.Contains(seeded.Id.ToString()));

        string report = Report(result);
        report.Should().Contain("Veredictos que la app no aplicó tal cual");
        report.Should().Contain("mismo commit");
        report.Should().Contain(seeded.Id.ToString(), "un número sin causa no vale (D-060)");
    }

    // ---------- 2. no-es-defecto ----------

    [Fact]
    public async Task Not_a_defect_disputes_without_resolving_or_deactivating()
    {
        Finding seeded = SeedExisting(HeadSha(), HashOf("A.cs"));
        int confirmedBefore = seeded.TimesConfirmed;

        SessionResult result = await RunWithVerdict(seeded, "no-es-defecto", model: "modelo-nuevo");

        Finding after = _hub.Store.TryReadFinding("app", seeded.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Activo, "discrepar no desactiva");
        after.Resolved.Should().BeNull("discrepar no resuelve");
        after.NeedsReview.Should().BeFalse("disputado no es lo mismo que no-verificable");
        after.TimesConfirmed.Should().Be(confirmedBefore, "quien discrepa no está confirmando nada");
        after.Confidence.Should().Be(Confidence.Media, "la confianza no se toca");

        after.Disputes.Should().ContainSingle();
        DisputeEntry dispute = after.Disputes.Single();
        dispute.Model.Should().Be("modelo-nuevo", "hay que saber QUIÉN discrepa");
        dispute.Justification.Should().Be("razonamiento del auditor");

        result.Counters.Disputed.Should().Be(1);
        result.Counters.Resolved.Should().Be(0);
        Report(result).Should().Contain("Disputados");
    }

    [Fact]
    public async Task Disputes_accumulate_so_several_models_disagreeing_is_visible()
    {
        Finding seeded = SeedExisting(HeadSha(), HashOf("A.cs"));

        await RunWithVerdict(seeded, "no-es-defecto", model: "modelo-a");
        await RunWithVerdict(seeded, "no-es-defecto", model: "modelo-b");

        Finding after = _hub.Store.TryReadFinding("app", seeded.Id.ToString())!;
        after.Disputes.Should().HaveCount(2);
        after.Disputes.Select(d => d.Model).Should().Equal("modelo-a", "modelo-b");
        after.History.Count(h => h.Event == FindingEvent.Disputed).Should().Be(2);
    }

    [Fact]
    public void The_verdict_vocabulary_accepts_the_new_word_and_still_rejects_invented_ones()
    {
        ReconciliationService.TryParseVerdict("no-es-defecto", out ReconcileVerdict v).Should().BeTrue();
        v.Should().Be(ReconcileVerdict.NoEsDefecto);
        ReconciliationService.TryParseVerdict("no es defecto", out _).Should().BeTrue();
        ReconciliationService.TryParseVerdict("falso-positivo", out _).Should().BeFalse(
            "falso-positivo es una decisión humana de gobernanza, no un veredicto del auditor");
    }

    // ---------- 3. La salida de la disputa es humana, en las dos direcciones ----------

    [Fact]
    public async Task Governance_can_accept_the_dispute_as_a_false_positive()
    {
        Finding seeded = SeedExisting(HeadSha(), HashOf("A.cs"));
        await RunWithVerdict(seeded, "no-es-defecto");

        _governance.ResolveDisputeAsFalsePositive("app", seeded.Id, "revisado a mano: el using cubre el caso");

        Finding after = _hub.Store.TryReadFinding("app", seeded.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Silenciado, "falso positivo se silencia, no se resuelve");
        after.Resolved.Should().BeNull("nunca hubo nada que arreglar");
        after.Disputes.Should().BeEmpty("la disputa ya está decidida");

        Silence silence = _hub.Store.TryReadSilence("app", seeded.Id)!;
        silence.Reason.Should().Be(SilenceReason.FalsoPositivo);
        silence.By.Should().NotBeNullOrWhiteSpace("toda decisión humana lleva autor");
        after.History.Should().Contain(h => h.Event == FindingEvent.DisputeCleared);
    }

    [Fact]
    public async Task Governance_can_dismiss_the_dispute_and_keep_the_finding()
    {
        Finding seeded = SeedExisting(HeadSha(), HashOf("A.cs"));
        await RunWithVerdict(seeded, "no-es-defecto");

        _governance.DismissDispute("app", seeded.Id, "el riesgo es real en el camino de excepción");

        Finding after = _hub.Store.TryReadFinding("app", seeded.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Activo, "sigue siendo un defecto");
        after.Disputes.Should().BeEmpty("la marca se retira");
        after.History.Should().Contain(h => h.Event == FindingEvent.DisputeCleared);
        after.History.Should().Contain(h => h.Event == FindingEvent.Disputed,
            "el historial conserva que alguien discrepó, aunque la marca se haya quitado");
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
