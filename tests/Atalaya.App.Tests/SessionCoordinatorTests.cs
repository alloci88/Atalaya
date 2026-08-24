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
        => new("errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation(path, 1, "snippet") }, "A.M");

    private SessionCoordinator NewCoordinator(FakeCopilotAgent agent)
        => new(_hub, _ingestion, _machines, _ulids, agent);

    private SessionCoordinator NewCoordinator(ICopilotAgent agent)
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

    // ---------- F3.1 · Bloque 1 — matching de 2ª pasada ----------

    /// <summary>
    /// Réplica del piloto 2026-08-24 CommonStatics.cs: un hallazgo v4 (fingerprint por título)
    /// resuelto en la sesión anterior, y un payload nuevo (mismo problema, ruleId → fingerprint
    /// distinto). Sin la 2ª pasada esto sería New. Con la 2ª pasada debe reabrir el resuelto,
    /// migrar el fingerprint y preservar el antiguo en <c>PreviousFingerprints</c>.
    /// </summary>
    [Fact]
    public async Task Second_pass_match_reopens_falsely_resolved_and_migrates_fingerprint()
    {
        // Sembrar el "resuelto v4": fingerprint arbitrario (título-based), mismo símbolo/ruta.
        var oldStamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-7), AuditMode.Lotes, "old", "alvaro");
        string oldFp = "sha256:" + new string('a', 64);
        var legacy = new Finding
        {
            Id = _ulids.NewUlid(),
            Fingerprint = oldFp,
            RuleId = "errores.recursos.no-liberado",
            Pillar = Pillar.Errores,
            Severity = Severity.Critica,
            Title = "Conn leaked in acquire path",
            Locations = { new Location("A.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = oldStamp,
            LastConfirmed = oldStamp,
        };
        legacy.Resolve(new ResolutionStamp(DateTimeOffset.UtcNow.AddDays(-1),
            ResolutionVia.Implicita, AuditMode.Lotes, "c", "alvaro", "cubierta"));
        _hub.Store.WriteFinding("app", legacy);

        // Payload nuevo: mismo problema, título muy parecido.
        var payload = new SubmitFindingArgs(
            "errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked in the acquire path", "d", "i", "r",
            new[] { new SubmitLocation("A.cs", 1, "s") }, "A.M");

        SessionResult result = await NewCoordinator(new FakeCopilotAgent(_ => new[] { payload }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        // Un solo hallazgo (el reabierto), status activo, fingerprint migrado.
        var findings = _hub.Store.ListFindings("app");
        findings.Should().ContainSingle();
        Finding f = findings.Single();
        f.Id.Should().Be(legacy.Id);
        f.Status.Should().Be(FindingStatus.Activo);
        f.Fingerprint.Should().NotBe(oldFp);
        f.PreviousFingerprints.Should().Contain(oldFp);
        result.Counters.New.Should().Be(0);
        result.Counters.Confirmed.Should().Be(1);
    }

    /// <summary>
    /// Un silencio registrado con el fingerprint VIEJO debe seguir suprimiendo la detección
    /// cuando entra por el nuevo hash (via 2ª pasada). Sin esto la migración rompería silencios
    /// legítimos del v4.
    /// </summary>
    [Fact]
    public async Task Silence_registered_with_old_fingerprint_survives_migration()
    {
        string oldFp = "sha256:" + new string('b', 64);
        var oldStamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-7), AuditMode.Lotes, "old", "alvaro");
        var legacy = new Finding
        {
            Id = _ulids.NewUlid(),
            Fingerprint = oldFp,
            RuleId = "errores.recursos.no-liberado",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Status = FindingStatus.Silenciado,
            Title = "Conn leaked in acquire path",
            Locations = { new Location("A.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = oldStamp,
            LastConfirmed = oldStamp,
        };
        _hub.Store.WriteFinding("app", legacy);
        _hub.Store.WriteSilence("app", new Silence
        {
            Fingerprint = oldFp,
            By = "maria",
            Utc = DateTimeOffset.UtcNow.AddDays(-3),
            Reason = SilenceReason.DeudaAceptada,
            FindingUlids = { legacy.Id },
        });

        var payload = new SubmitFindingArgs(
            "errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked in the acquire path", "d", "i", "r",
            new[] { new SubmitLocation("A.cs", 1, "s") }, "A.M");

        SessionResult result = await NewCoordinator(new FakeCopilotAgent(_ => new[] { payload }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        result.Counters.SilencedRespected.Should().Be(1);
        result.Counters.New.Should().Be(0);

        // El hallazgo NO se migra en este camino (el silencio corta antes de tocarlo); sigue
        // silenciado con su fingerprint original. Comportamiento intencional: silencios v4
        // siguen aplicando SIN reescribir hashes.
        Finding f = _hub.Store.ListFindings("app").Single();
        f.Status.Should().Be(FindingStatus.Silenciado);
        f.Fingerprint.Should().Be(oldFp);
    }

    /// <summary>
    /// El matching NO debe emparejar dos problemas distintos aunque compartan ruta y símbolo:
    /// un hallazgo de <c>Encoding.ASCII</c> no es el mismo que un desbordamiento de longitud
    /// aunque ambos vivan en <c>StringToByteArray</c> (piloto real, caso #5).
    /// </summary>
    [Fact]
    public async Task Second_pass_does_not_match_semantically_different_problems_in_same_symbol()
    {
        var oldStamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-7), AuditMode.Lotes, "old", "alvaro");
        var legacy = new Finding
        {
            Id = _ulids.NewUlid(),
            Fingerprint = "sha256:" + new string('c', 64),
            RuleId = "errores.null.desreferencia",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Title = "StringToByteArray puede lanzar excepción si la cadena excede la longitud destino",
            Locations = { new Location("A.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = oldStamp,
            LastConfirmed = oldStamp,
        };
        legacy.Resolve(new ResolutionStamp(DateTimeOffset.UtcNow.AddDays(-1),
            ResolutionVia.Implicita, AuditMode.Lotes, "c", "alvaro", "cubierta"));
        _hub.Store.WriteFinding("app", legacy);

        var payload = new SubmitFindingArgs(
            "criterio.seguridad", "errores", "baja",
            "Uso de Encoding.ASCII en StringToByteArray puede perder datos silenciosamente",
            "d", "i", "r",
            new[] { new SubmitLocation("A.cs", 1, "s") }, "A.M");

        SessionResult result = await NewCoordinator(new FakeCopilotAgent(_ => new[] { payload }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        result.Counters.New.Should().Be(1);
        _hub.Store.ListFindings("app").Should().HaveCount(2);
    }

    /// <summary>
    /// F3.1 Bloque 0: cuando una unidad se corta por presupuesto (<c>MaxTokensPerUnit</c>) la sesión
    /// debe (a) cerrar esa unidad con veredicto <c>presupuesto-superado</c> y summary explícito con
    /// gasto y motivo dominante de rechazos si los hubo, (b) contabilizar los rechazos en
    /// <c>SessionCounters.Rejected</c>, (c) narrar el corte en el informe (sección "Incidencias por
    /// unidad") y (d) escribir siempre el fichero de sesión — nunca más un "Nuevos 0" mudo.
    /// </summary>
    [Fact]
    public async Task Unit_over_budget_is_narrated_in_session_report_and_verdict()
    {
        var agent = new BudgetTrippingAgent(inputTokens: 400_000, outputTokens: 100_000, rejectPayloads: 3);

        SessionResult result = await NewCoordinator((ICopilotAgent)agent)
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        AuditSession session = _hub.Store.ListSessions("app").Single();
        UnitVerdictRecord unit = session.Units.Single();
        unit.Verdict.Should().Be("presupuesto-superado");
        unit.Summary.Should().Contain("Cortada por presupuesto");
        unit.Summary.Should().Contain("500000/300000"); // spent/max
        unit.RejectedPayloads.Should().Be(3);
        unit.DominantRejectionReason.Should().NotBeNullOrEmpty();

        result.Counters.Rejected.Should().Be(3);
        result.Counters.New.Should().Be(0);

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));
        report.Should().Contain("Incidencias por unidad");
        report.Should().Contain("presupuesto-superado");
        report.Should().Contain("Payloads rechazados por validación: 3");
    }

    /// <summary>Agente de test que emite un <c>UsageSample</c> lo bastante grande para disparar el
    /// presupuesto por unidad y, opcionalmente, empuja N payloads inválidos por el toolbox
    /// (<c>severity</c> desconocida) para probar el conteo y la moda de motivo de rechazo.</summary>
    private sealed class BudgetTrippingAgent : ICopilotAgent
    {
        private readonly long _in;
        private readonly long _out;
        private readonly int _reject;

        public BudgetTrippingAgent(long inputTokens, long outputTokens, int rejectPayloads)
        {
            _in = inputTokens;
            _out = outputTokens;
            _reject = rejectPayloads;
        }

        public string? ModelName => "budget-trip";
        public event Action<string>? TextStreamed;
        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke("[budget-trip] start\n");

            // Rechazos previos al corte: severidad inventada → motivo dominante estable.
            if (_reject > 0)
            {
                var bad = new SubmitFindingArgs[_reject];
                for (int i = 0; i < _reject; i++)
                {
                    bad[i] = new SubmitFindingArgs(
                        "errores.recursos.no-liberado", "errores", "urgentísima",
                        $"bad-{i}", "d", "i", "r",
                        new[] { new SubmitLocation(request.UnitPath, 1, null) }, $"S{i}");
                }

                toolbox.SubmitFindings(bad);
            }

            // Emitimos el gasto en un único sample: el handler del coordinador cancelará el CT
            // vinculado a esta unidad y esperamos a que se propague como OperationCanceledException.
            UsageReported?.Invoke(new UsageSample(_in, _out, null, ModelName));
            ct.ThrowIfCancellationRequested();
            toolbox.UnitDone(request.UnitPath, "no debería llegarse");
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---------- F3 · Hito 1c — batched submit_findings ----------

    /// <summary>
    /// Regresión detectada 2026-08-24: el batching contaba tool calls pero no ingería nada
    /// ("9 llamadas, 0 hallazgos"). Este test blinda el camino: N hallazgos en UN lote → N
    /// eventos, N ingeridos, resumen correcto y sin rechazos silenciosos.
    /// </summary>
    [Fact]
    public async Task Batched_submit_findings_ingests_every_item_in_a_single_call()
    {
        SubmitFindingArgs a = SampleFinding("A.cs") with { Title = "Uno", Symbol = "A.M1" };
        SubmitFindingArgs b = SampleFinding("A.cs") with { Title = "Dos", Symbol = "A.M2" };
        SubmitFindingArgs c = SampleFinding("A.cs") with { Title = "Tres", Symbol = "A.M3" };

        var coordinator = NewCoordinator(new FakeCopilotAgent(_ => new[] { a, b, c }));
        int events = 0;
        coordinator.FindingReported += (_, _) => events++;

        SessionResult result = await coordinator
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        result.Counters.New.Should().Be(3);
        events.Should().Be(3);
        _hub.Store.ListFindings("app").Should().HaveCount(3);

        // No rejection notes: everything was ingested cleanly.
        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Notes.Should().NotContain(n => n.Contains("rechazo"));
    }

    /// <summary>
    /// Un payload inválido dentro del lote NO debe tumbar el resto y DEBE devolver un error
    /// concreto al agente + quedar registrado en las notas de la sesión (nunca se traga).
    /// </summary>
    [Fact]
    public async Task Batched_invalid_payload_reports_error_back_to_agent_and_logs_it()
    {
        SubmitFindingArgs good = SampleFinding("A.cs");
        SubmitFindingArgs bad = good with { RuleId = "esto.no.existe" };
        SubmitFindingArgs badSev = good with { Severity = "urgentísima", Symbol = "A.X" };

        SessionResult result = await NewCoordinator(new FakeCopilotAgent(_ => new[] { good, bad, badSev }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        result.Counters.New.Should().Be(1);
        _hub.Store.ListFindings("app").Should().ContainSingle();

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Notes.Should().Contain(n => n.Contains("rechazo", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// F3.1 Bloque 0: <c>tag</c> ya no forma parte de la tool <c>submit_findings</c>. La app lo
    /// infiere del <c>ruleId</c> (<c>criterio.*</c> → Criterio; resto → Checklist). Este test
    /// blinda la invariante para ambos casos y garantiza que no reaparezca la vía de rechazo que
    /// tumbó la sesión piloto 2026-08-24 (25 rechazos por variantes inventadas de tag).
    /// </summary>
    [Fact]
    public async Task Tag_is_always_inferred_from_ruleId()
    {
        SubmitFindingArgs checklistItem = SampleFinding("A.cs");
        SubmitFindingArgs criterioItem = SampleFinding("A.cs") with
        {
            RuleId = "criterio.recursos",
            Symbol = "A.Other",
            Title = "Otro hallazgo",
        };

        await NewCoordinator(new FakeCopilotAgent(_ => new[] { checklistItem, criterioItem }))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        var findings = _hub.Store.ListFindings("app");
        findings.Should().HaveCount(2);
        findings.Single(f => f.RuleId == "errores.recursos.no-liberado").Tag.Should().Be(FindingTag.Checklist);
        findings.Single(f => f.RuleId == "criterio.recursos").Tag.Should().Be(FindingTag.Criterio);
    }
}
