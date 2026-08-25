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

/// <summary>End-to-end lotes pipeline with the injectable fake agent (§11).</summary>
public sealed class SessionCoordinatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
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
        _reconciliation = new ReconciliationService(_hub);

        Seed();
    }

    private void Seed()
    {
        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A { void M() { } }");
        File.WriteAllText(Path.Combine(_clone, "B.cs"), "class B { void N() { } }");
        _machines.SetClonePath("app", _clone);

        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        SetMaxPasses(1);
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
    /// Fija el tope de pasadas del barrido. Los tests de reconciliación usan 1 para aislar la
    /// semántica de una pasada; los del barrido suben el tope a propósito.
    /// </summary>
    private void SetMaxPasses(int max)
        => _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
            Thresholds = new Thresholds { MaxPassesPerUnit = max },
        });

    private static SubmitFindingArgs SampleFinding(string path = "A.cs")
        => new("errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation(path, 1, "snippet") }, "A.M");

    private SessionCoordinator NewCoordinator(ICopilotAgent agent)
        => new(_hub, _ingestion, _reconciliation, _machines, _ulids, agent);

    private Task<SessionResult> RunLotes(ICopilotAgent agent, params string[] units)
        => NewCoordinator(agent).RunAsync(
            new SessionRequest("app", AuditMode.Lotes, units.Length == 0 ? new[] { "A.cs" } : units),
            CancellationToken.None);

    /// <summary>Siembra un hallazgo activo ya existente en el hub, anclado a <paramref name="path"/>.</summary>
    private Finding SeedExisting(string title, string path = "A.cs", string ruleId = "errores.null.desreferencia")
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-7), AuditMode.Lotes, "old", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = ruleId,
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = title,
            Locations = { new Location(path, 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    [Fact]
    public async Task Lotes_session_ingests_findings_marks_audited_and_writes_session_and_report()
    {
        var agent = new FakeCopilotAgent(auditScript: r =>
            r.UnitPath == "A.cs" ? new[] { SampleFinding() } : Array.Empty<SubmitFindingArgs>());

        SessionResult result = await RunLotes(agent);

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

    // ---------- F4 · reconciliación por el auditor ----------

    /// <summary>
    /// El escenario canónico del pivote (D-077): 3 hallazgos existentes en la unidad, el auditor
    /// declara 2 "presente", 1 "arreglado" y aporta 1 nuevo. Estado final exacto y CERO implícitos:
    /// nada se resuelve por omisión, ningún duplicado aparece.
    /// </summary>
    [Fact]
    public async Task Auditor_reconciles_existing_and_adds_new_with_no_implicit_effects()
    {
        Finding a = SeedExisting("Fuga de conexión en la ruta de error");
        Finding b = SeedExisting("Parseo sin manejo de errores", ruleId: "errores.calculo.negocio");
        Finding c = SeedExisting("Asignaciones repetidas de arrays", ruleId: "optimizacion.alloc.bucle");

        var agent = new FakeCopilotAgent(
            auditScript: _ => new[] { SampleFinding() with { Title = "Encoding.ASCII pierde datos" } },
            reconcileScript: r => new[]
            {
                new VerdictArgs(a.Id.ToString(), "presente", "sigue en la línea 12"),
                new VerdictArgs(b.Id.ToString(), "presente", "el try/catch no cubre el parse"),
                new VerdictArgs(c.Id.ToString(), "arreglado", "ahora se reutiliza el buffer"),
            });

        SessionResult result = await RunLotes(agent);

        result.Counters.Confirmed.Should().Be(2);
        result.Counters.Resolved.Should().Be(1);
        result.Counters.New.Should().Be(1);
        result.Counters.NoVerificables.Should().Be(0);
        result.IncompleteUnits.Should().Be(0);

        var findings = _hub.Store.ListFindings("app");
        findings.Should().HaveCount(4); // 3 previos + 1 nuevo: ningún duplicado
        findings.Single(f => f.Id == a.Id).Status.Should().Be(FindingStatus.Activo);
        findings.Single(f => f.Id == a.Id).TimesConfirmed.Should().Be(2);
        findings.Single(f => f.Id == b.Id).Status.Should().Be(FindingStatus.Activo);

        Finding resolved = findings.Single(f => f.Id == c.Id);
        resolved.Status.Should().Be(FindingStatus.Resuelto);
        resolved.Resolved!.Via.Should().Be(ResolutionVia.Auditor);
        resolved.Resolved.Justification.Should().Be("ahora se reutiliza el buffer");
    }

    /// <summary>
    /// La propiedad que nunca se cumplió con los fingerprints: dos sesiones consecutivas sobre la
    /// misma unidad sin cambios en el código → la segunda es TODO confirmaciones, 0 nuevos,
    /// 0 resueltos. El agente falso, como el real, ve la lista de existentes y responde "presente".
    /// </summary>
    [Fact]
    public async Task Two_consecutive_sessions_are_stable_zero_new_zero_resolved()
    {
        var findings = new[]
        {
            SampleFinding() with { Title = "Uno", Symbol = "A.M1" },
            SampleFinding() with { Title = "Dos", Symbol = "A.M2" },
            SampleFinding() with { Title = "Tres", Symbol = "A.M3" },
        };

        // 1ª sesión: baseline vacío → 3 nuevos.
        SessionResult first = await RunLotes(new FakeCopilotAgent(_ => findings));
        first.Counters.New.Should().Be(3);

        // 2ª sesión: el auditor ve los 3 en la lista, los declara presentes y no reporta nada nuevo.
        SessionResult second = await RunLotes(new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>()));

        second.Counters.New.Should().Be(0);
        second.Counters.Resolved.Should().Be(0);
        second.Counters.Confirmed.Should().Be(3);
        second.IncompleteUnits.Should().Be(0);
        _hub.Store.ListFindings("app").Should().HaveCount(3)
            .And.OnlyContain(f => f.Status == FindingStatus.Activo);
    }

    /// <summary>
    /// El auditor omite un veredicto → la unidad queda INCOMPLETA (visible en veredicto, informe y
    /// resultado) y el hallazgo huérfano queda INTACTO. Esto es lo que sustituye a la resolución
    /// implícita: el silencio del auditor ya no cierra nada.
    /// </summary>
    [Fact]
    public async Task Missing_verdict_marks_the_unit_incomplete_and_leaves_the_finding_untouched()
    {
        Finding a = SeedExisting("Con veredicto");
        Finding b = SeedExisting("Sin veredicto", ruleId: "errores.calculo.negocio");

        var agent = new FakeCopilotAgent(
            reconcileScript: _ => new[] { new VerdictArgs(a.Id.ToString(), "presente", "sigue ahí") });

        SessionResult result = await RunLotes(agent);

        result.IncompleteUnits.Should().Be(1);
        result.Counters.Resolved.Should().Be(0);

        Finding untouched = _hub.Store.TryReadFinding("app", b.Id.ToString())!;
        untouched.Status.Should().Be(FindingStatus.Activo);
        untouched.TimesConfirmed.Should().Be(1);         // ni confirmado
        untouched.LastConfirmed.Utc.Should().Be(b.LastConfirmed.Utc); // ni tocado

        AuditSession session = _hub.Store.ListSessions("app").Single();
        UnitVerdictRecord unit = session.Units.Single();
        unit.Verdict.Should().Be("incompleta");
        unit.MissingVerdicts.Should().Be(1);
        session.Notes.Should().Contain(n => n.Contains("sin veredicto") && n.Contains(b.Id.ToString()));

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));
        report.Should().Contain("Unidades incompletas: 1");
        report.Should().Contain("Hallazgos sin veredicto");
        report.Should().Contain(b.Id.ToString());
    }

    /// <summary>
    /// Un hallazgo silenciado se le muestra al auditor con estado <c>silenciado</c>. Si dice
    /// "presente", la detección se registra pero el hallazgo NO se reactiva: el silencio es una
    /// decisión humana y el auditor no la revoca.
    /// </summary>
    [Fact]
    public async Task Silenced_finding_detected_present_stays_silenced_and_records_the_detection()
    {
        Finding f = SeedExisting("Deuda aceptada a propósito");
        f.MarkSilenced(DateTimeOffset.UtcNow.AddDays(-1), "maria", "deuda aceptada");
        _hub.Store.WriteFinding("app", f);
        _hub.Store.WriteSilence("app", new Silence
        {
            FindingUlid = f.Id,
            By = "maria",
            Utc = DateTimeOffset.UtcNow.AddDays(-1),
            Reason = SilenceReason.DeudaAceptada,
        });

        var agent = new FakeCopilotAgent(
            reconcileScript: _ => new[] { new VerdictArgs(f.Id.ToString(), "presente", "sigue en la línea 3") });

        SessionResult result = await RunLotes(agent);

        result.Counters.SilencedRespected.Should().Be(1);
        result.Counters.Confirmed.Should().Be(0);
        result.Counters.New.Should().Be(0);
        result.IncompleteUnits.Should().Be(0);

        Finding after = _hub.Store.TryReadFinding("app", f.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Silenciado);
        after.Confidence.Should().Be(Confidence.Media);        // la confianza no se toca
        after.History.Should().Contain(h => h.Detail!.Contains("detectado presente durante el silencio"));
    }

    /// <summary>Un silencio CADUCADO no suprime: la detección lo levanta y el hallazgo vuelve a activo (§2).</summary>
    [Fact]
    public async Task Expired_silence_lets_the_finding_reappear_on_detection()
    {
        Finding f = SeedExisting("Silencio con fecha");
        f.MarkSilenced(DateTimeOffset.UtcNow.AddDays(-3), "maria", "temporal");
        _hub.Store.WriteFinding("app", f);
        _hub.Store.WriteSilence("app", new Silence
        {
            FindingUlid = f.Id,
            By = "maria",
            Utc = DateTimeOffset.UtcNow.AddDays(-3),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1),
            Reason = SilenceReason.DeudaAceptada,
        });

        SessionResult result = await RunLotes(new FakeCopilotAgent());

        result.Counters.Confirmed.Should().Be(1);
        _hub.Store.TryReadFinding("app", f.Id.ToString())!.Status.Should().Be(FindingStatus.Activo);
    }

    /// <summary>
    /// Un veredicto sobre un ULID que no está en la lista de la unidad se rechaza con un error
    /// tipado devuelto AL AGENTE, no toca nada, y queda registrado en las notas de la sesión.
    /// </summary>
    [Fact]
    public async Task Verdict_on_unknown_id_is_rejected_with_a_typed_error_and_changes_nothing()
    {
        Finding real = SeedExisting("Existe de verdad");
        string ghost = _ulids.NewUlid().ToString();

        var agent = new FakeCopilotAgent(reconcileScript: _ => new[]
        {
            new VerdictArgs(real.Id.ToString(), "presente", "sigue ahí"),
            new VerdictArgs(ghost, "arreglado", "me lo he inventado"),
        });

        SessionResult result = await RunLotes(agent);

        result.Counters.Rejected.Should().Be(1);
        result.Counters.Resolved.Should().Be(0);
        result.Counters.Confirmed.Should().Be(1);
        result.IncompleteUnits.Should().Be(0);   // el único listado SÍ tuvo veredicto

        _hub.Store.ListFindings("app").Should().ContainSingle()
            .Which.Status.Should().Be(FindingStatus.Activo);

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Notes.Should().Contain(n =>
            n.Contains("findingId desconocido") && n.Contains(ghost));
    }

    /// <summary>Un verdict con vocabulario inválido se rechaza igual: la app no adivina.</summary>
    [Fact]
    public async Task Unknown_verdict_word_is_rejected_and_leaves_the_unit_incomplete()
    {
        Finding f = SeedExisting("Un hallazgo");

        var agent = new FakeCopilotAgent(reconcileScript: _ => new[]
        {
            new VerdictArgs(f.Id.ToString(), "resuelto-creo", "hmm"),
        });

        SessionResult result = await RunLotes(agent);

        result.Counters.Rejected.Should().Be(1);
        result.IncompleteUnits.Should().Be(1);   // sin veredicto válido → incompleta
        _hub.Store.TryReadFinding("app", f.Id.ToString())!.Status.Should().Be(FindingStatus.Activo);
    }

    /// <summary>"no-verificable" no resuelve nada: marca <c>needsReview</c> y se cuenta aparte.</summary>
    [Fact]
    public async Task Non_verifiable_verdict_flags_needs_review_and_is_reported()
    {
        Finding f = SeedExisting("Depende de otro fichero");

        var agent = new FakeCopilotAgent(reconcileScript: _ => new[]
        {
            new VerdictArgs(f.Id.ToString(), "no-verificable", "el estado lo fija Config.cs"),
        });

        SessionResult result = await RunLotes(agent);

        result.Counters.NoVerificables.Should().Be(1);
        result.Counters.Resolved.Should().Be(0);

        Finding after = _hub.Store.TryReadFinding("app", f.Id.ToString())!;
        after.NeedsReview.Should().BeTrue();
        after.Status.Should().Be(FindingStatus.Activo);

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));
        report.Should().Contain("No verificables (marcados para revisión): 1");
    }

    /// <summary>
    /// La lista de existentes está acotada POR UNIDAD: un hallazgo de B.cs no se le ofrece al
    /// auditor de A.cs, así que no puede pronunciarse sobre él ni dejarlo incompleto.
    /// </summary>
    [Fact]
    public async Task Existing_list_is_scoped_to_the_audited_unit()
    {
        SeedExisting("Vive en B", path: "B.cs");

        SessionResult result = await RunLotes(new FakeCopilotAgent(), "A.cs");

        result.IncompleteUnits.Should().Be(0);
        result.Counters.Confirmed.Should().Be(0);
    }

    /// <summary>
    /// Única salvaguarda de dedupe superviviente (F4): el mismo título y la misma ubicación dos
    /// veces en la MISMA sesión entra una sola vez. Contra el histórico no se compara nada.
    /// </summary>
    [Fact]
    public async Task Exact_duplicate_within_the_same_session_is_rejected_once()
    {
        SubmitFindingArgs one = SampleFinding();
        SubmitFindingArgs twin = SampleFinding() with { Symbol = "A.Otro" }; // mismo título y ubicación

        SessionResult result = await RunLotes(new FakeCopilotAgent(_ => new[] { one, twin }));

        result.Counters.New.Should().Be(1);
        result.Counters.Rejected.Should().Be(1);
        _hub.Store.ListFindings("app").Should().ContainSingle();
    }

    /// <summary>
    /// Y el reverso: el MISMO payload en dos sesiones distintas NO se deduplica en la ingestión —
    /// pero tampoco duplica, porque el auditor lo ve en la lista y lo reconcilia. Sin la lista
    /// (agente que ignora la reconciliación) sí se crearía un duplicado: es el precio explícito y
    /// autocorregible del modelo (ver D-077).
    /// </summary>
    [Fact]
    public async Task Agent_ignoring_the_existing_list_creates_a_duplicate_but_resolves_nothing()
    {
        await RunLotes(new FakeCopilotAgent(_ => new[] { SampleFinding() }));

        // Agente "malo": re-reporta el mismo problema como nuevo y no emite veredictos.
        SessionResult second = await RunLotes(new FakeCopilotAgent(
            auditScript: _ => new[] { SampleFinding() },
            reconcileScript: _ => Array.Empty<VerdictArgs>()));

        second.Counters.New.Should().Be(1);
        second.Counters.Resolved.Should().Be(0);           // lo que importa: NADA se resolvió
        second.IncompleteUnits.Should().Be(1);             // y el fallo es visible
        _hub.Store.ListFindings("app").Should().HaveCount(2)
            .And.OnlyContain(f => f.Status == FindingStatus.Activo);
    }

    // ---------- F4.1 · barrido hasta agotar ----------

    /// <summary>
    /// El barrido repite la pasada hasta que una queda SECA (0 nuevos y todos los veredictos
    /// «presente»). Es lo que convierte "una auditoría" en "una unidad completa" pese a que el
    /// auditor no cubra la unidad de una sola pasada.
    /// </summary>
    [Fact]
    public async Task Sweep_repeats_until_a_pass_comes_up_dry()
    {
        SetMaxPasses(3);

        // Pasada 1: dos hallazgos. Pasada 2: uno más. Pasada 3: nada → seca.
        int pass = 0;
        var agent = new FakeCopilotAgent(auditScript: _ =>
        {
            pass++;
            return pass switch
            {
                1 => new[] { SampleFinding() with { Title = "Uno" }, SampleFinding() with { Title = "Dos" } },
                2 => new[] { SampleFinding() with { Title = "Tres" } },
                _ => Array.Empty<SubmitFindingArgs>(),
            };
        });

        SessionResult result = await RunLotes(agent);

        pass.Should().Be(3, "debe parar en cuanto una pasada queda seca, no antes ni después");
        result.Counters.New.Should().Be(3);
        result.Counters.Resolved.Should().Be(0);
        _hub.Store.ListFindings("app").Should().HaveCount(3, "las pasadas reconcilian, no duplican");

        AuditSession session = _hub.Store.ListSessions("app").Single();
        UnitVerdictRecord unit = session.Units.Single();
        unit.Verdict.Should().Be("auditada");
        unit.CoverageIncomplete.Should().BeFalse();
        unit.Passes.Should().HaveCount(3);
        unit.Passes![0].New.Should().Be(2);
        unit.Passes[1].New.Should().Be(1);
        unit.Passes[2].Dry.Should().BeTrue();

        // Una sola sesión y un solo desglose de tokens: las pasadas son internas.
        session.UsageBreakdown.Should().ContainSingle();
    }

    /// <summary>
    /// Si se agota el tope sin secarse, la unidad se marca «cobertura posiblemente incompleta».
    /// Visible, nunca silencioso: es exactamente el fallo que motivó el barrido.
    /// </summary>
    [Fact]
    public async Task Sweep_hitting_the_cap_marks_the_unit_as_possibly_incomplete()
    {
        SetMaxPasses(2);

        int n = 0;
        var agent = new FakeCopilotAgent(auditScript: _ =>
            new[] { SampleFinding() with { Title = $"Hallazgo {++n}" } });   // nunca se seca

        SessionResult result = await RunLotes(agent);

        AuditSession session = _hub.Store.ListSessions("app").Single();
        UnitVerdictRecord unit = session.Units.Single();
        unit.Verdict.Should().Be("cobertura posiblemente incompleta");
        unit.CoverageIncomplete.Should().BeTrue();
        unit.Passes.Should().HaveCount(2);
        unit.Summary.Should().Contain("sin llegar a seca");
        session.Notes.Should().Contain(nn => nn.Contains("Cobertura posiblemente incompleta"));

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));
        report.Should().Contain("cobertura posiblemente incompleta");
    }

    /// <summary>
    /// Guarda de coherencia: entre pasadas del mismo barrido el código NO cambia, así que un
    /// «arreglado» sobre un hallazgo que el propio barrido acaba de crear es una contradicción del
    /// modelo. Se degrada a «presente» y se registra — nunca resuelve.
    /// </summary>
    [Fact]
    public async Task Fixed_verdict_on_a_finding_from_the_same_sweep_is_ignored()
    {
        SetMaxPasses(3);

        int pass = 0;
        var agent = new FakeCopilotAgent(
            auditScript: _ =>
            {
                pass++;
                return pass == 1 ? new[] { SampleFinding() with { Title = "Recien nacido" } } : Array.Empty<SubmitFindingArgs>();
            },
            // En la pasada 2 el modelo se contradice: dice que lo que acaba de reportar ya está arreglado.
            reconcileScript: r => r.Existing.Select(e => new VerdictArgs(e.FindingId, "arreglado", "ya no está")));

        SessionResult result = await RunLotes(agent);

        result.Counters.Resolved.Should().Be(0, "el codigo no cambia entre pasadas: no puede arreglarse nada");
        _hub.Store.ListFindings("app").Should().ContainSingle()
            .Which.Status.Should().Be(FindingStatus.Activo);

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Notes.Should().Contain(n => n.Contains("'arreglado' ignorado"));
    }

    /// <summary>
    /// Y el reverso: un «arreglado» sobre un hallazgo de una sesión ANTERIOR sí resuelve. La
    /// guarda es solo para el mismo barrido, no para auditorías distintas.
    /// </summary>
    [Fact]
    public async Task Fixed_verdict_on_a_finding_from_a_previous_session_still_resolves()
    {
        SetMaxPasses(3);
        Finding old = SeedExisting("De una sesion anterior");

        var agent = new FakeCopilotAgent(
            reconcileScript: r => r.Existing.Select(e => new VerdictArgs(e.FindingId, "arreglado", "corregido en el commit X")));

        SessionResult result = await RunLotes(agent);

        result.Counters.Resolved.Should().Be(1);
        _hub.Store.TryReadFinding("app", old.Id.ToString())!.Status.Should().Be(FindingStatus.Resuelto);
    }

    // ---------- F3.1 Bloque 0 — presupuesto y rechazos ----------

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

        SessionResult result = await RunLotes(agent);

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
        SubmitFindingArgs bad = good with { RuleId = "esto.no.existe", Title = "Otro" };
        SubmitFindingArgs badSev = good with { Severity = "urgentísima", Title = "Y otro" };

        SessionResult result = await RunLotes(new FakeCopilotAgent(_ => new[] { good, bad, badSev }));

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

        await RunLotes(new FakeCopilotAgent(_ => new[] { checklistItem, criterioItem }));

        var findings = _hub.Store.ListFindings("app");
        findings.Should().HaveCount(2);
        findings.Single(f => f.RuleId == "errores.recursos.no-liberado").Tag.Should().Be(FindingTag.Checklist);
        findings.Single(f => f.RuleId == "criterio.recursos").Tag.Should().Be(FindingTag.Criterio);
    }
}
