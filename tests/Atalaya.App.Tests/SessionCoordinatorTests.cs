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
    private readonly SettingsService _settings;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public SessionCoordinatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-sess", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));

        _settings = new SettingsService(_paths);
        _settings.Load(); // hub not configured → Sync is null → CommitAndPush is skipped (offline)
        _hub = TestFactory.Hub(_paths, _settings);
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
    /// <para>
    /// F5.1: el tope vive en los ajustes de la máquina, no en <c>app.json</c>, así que aquí se
    /// escribe donde el coordinador lo lee de verdad.
    /// </para>
    /// </summary>
    private void SetMaxPasses(int max)
    {
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = max;
        _settings.Save(s);
    }

    private static SubmitFindingArgs SampleFinding(string path = "A.cs")
        => new("errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation(path, 1, "snippet") }, "A.M");

    private SessionCoordinator NewCoordinator(IAuditorProvider agent)
        => new(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings);

    private Task<SessionResult> RunLotes(IAuditorProvider agent, params string[] units)
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
    /// El barrido repite la pasada hasta que CONVERGE: <b>dos pasadas secas seguidas</b> (F12 §E).
    /// Era una sola, y con un modelo no determinista «esta pasada no vio nada nuevo» no es «no
    /// queda nada» — en el banco de pruebas el barrido paró en 3 de 5 porque la tercera vino seca,
    /// y una segunda auditoría encontró después un hallazgo que la primera no había visto.
    /// </summary>
    [Fact]
    public async Task Sweep_repeats_until_two_consecutive_dry_passes()
    {
        SetMaxPasses(5);

        // Pasada 1: dos hallazgos. Pasada 2: uno más. Pasadas 3 y 4: nada → dos secas seguidas.
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

        pass.Should().Be(4, "para con la SEGUNDA seca seguida: ni antes ni después");
        result.Counters.New.Should().Be(3);
        result.Counters.Resolved.Should().Be(0);
        _hub.Store.ListFindings("app").Should().HaveCount(3, "las pasadas reconcilian, no duplican");

        AuditSession session = _hub.Store.ListSessions("app").Single();
        UnitVerdictRecord unit = session.Units.Single();
        unit.Verdict.Should().Be("auditada");
        unit.CoverageIncomplete.Should().BeFalse();
        unit.Passes.Should().HaveCount(4);
        unit.Passes![0].New.Should().Be(2);
        unit.Passes[1].New.Should().Be(1);
        unit.Passes[2].Dry.Should().BeTrue();
        unit.Passes[3].Dry.Should().BeTrue();

        // Una sola sesión y un solo desglose de tokens: las pasadas son internas.
        session.UsageBreakdown.Should().ContainSingle();
    }

    /// <summary>
    /// Y una seca SUELTA no cierra nada: si la siguiente aporta, la cuenta se reinicia y el barrido
    /// sigue. Es exactamente el caso del banco — la tercera vino seca y todavía quedaba un hallazgo.
    /// </summary>
    [Fact]
    public async Task Una_seca_seguida_de_una_pasada_con_hallazgos_reinicia_la_cuenta()
    {
        SetMaxPasses(6);

        // 1: uno. 2: seca. 3: uno más (lo que la seca no vio). 4 y 5: secas seguidas → para.
        int pass = 0;
        var agent = new FakeCopilotAgent(auditScript: _ =>
        {
            pass++;
            return pass switch
            {
                1 => new[] { SampleFinding() with { Title = "El que vio la primera" } },
                3 => new[] { SampleFinding() with { Title = "El que la seca no vio" } },
                _ => Array.Empty<SubmitFindingArgs>(),
            };
        });

        SessionResult result = await RunLotes(agent);

        pass.Should().Be(5, "la pasada 3 reinicia la cuenta; hacen falta la 4 y la 5");
        result.Counters.New.Should().Be(2, "el segundo hallazgo se habría perdido con una sola seca");

        UnitVerdictRecord unit = _hub.Store.ListSessions("app").Single().Units.Single();
        unit.Passes.Should().HaveCount(5);
        unit.Passes![1].Dry.Should().BeTrue();
        unit.Passes[2].Dry.Should().BeFalse("aportó, así que la racha se rompe");
        unit.Passes[3].Dry.Should().BeTrue();
        unit.Passes[4].Dry.Should().BeTrue();
        unit.CoverageIncomplete.Should().BeFalse();
    }

    /// <summary>
    /// EL TECHO SIGUE MANDANDO. Con un tope de 1 no caben dos secas, así que se pide lo que cabe:
    /// exigir dos convertiría cada unidad en «cobertura posiblemente incompleta» por una condición
    /// que el propio tope hace inalcanzable. Quien fija el tope decide cuánto paga; la regla de las
    /// dos secas decide cuándo se para dentro de él.
    /// </summary>
    [Fact]
    public async Task Con_un_tope_de_una_pasada_la_unica_que_cabe_cierra_la_unidad()
    {
        SetMaxPasses(1);

        int pass = 0;
        var agent = new FakeCopilotAgent(auditScript: _ =>
        {
            pass++;
            return Array.Empty<SubmitFindingArgs>();
        });

        await RunLotes(agent);

        pass.Should().Be(1);
        UnitVerdictRecord unit = _hub.Store.ListSessions("app").Single().Units.Single();
        unit.Verdict.Should().Be("auditada");
        unit.CoverageIncomplete.Should().BeFalse();
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
        unit.Summary.Should().Contain("sin llegar a 2 pasadas secas seguidas");
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

    /// <summary>
    /// El barrido no juzga su propia salida: lo que la pasada 2 «confirma» de la pasada 1 es
    /// contabilidad interna, no evidencia. Con baseline VACÍO el usuario debe ver confirmados 0,
    /// y <c>TimesConfirmed</c> no puede inflarse tres veces en una sola sesión — eso ascendería la
    /// confianza sin que haya habido una segunda auditoría de verdad.
    /// </summary>
    [Fact]
    public async Task Intra_sweep_confirmations_are_internal_and_never_reach_the_user()
    {
        SetMaxPasses(3);

        int pass = 0;
        var agent = new FakeCopilotAgent(auditScript: _ =>
        {
            pass++;
            return pass == 1 ? new[] { SampleFinding() } : Array.Empty<SubmitFindingArgs>();
        });

        SessionResult result = await RunLotes(agent);

        result.Counters.New.Should().Be(1);
        result.Counters.Confirmed.Should().Be(0, "no habia nada previo que confirmar");

        Finding f = _hub.Store.ListFindings("app").Should().ContainSingle().Subject;
        f.TimesConfirmed.Should().Be(1, "las pasadas del propio barrido no son evidencia independiente");
        f.Confidence.Should().Be(Confidence.Media, "lotes nace en media y el barrido no la asciende");
    }

    /// <summary>
    /// Y lo previo SÍ cuenta: un hallazgo de una sesión anterior declarado «presente» es una
    /// confirmación de verdad, y se cuenta una sola vez por muchas pasadas que dé el barrido.
    /// </summary>
    [Fact]
    public async Task Confirmations_of_earlier_findings_count_exactly_once_per_sweep()
    {
        SetMaxPasses(3);
        Finding previous = SeedExisting("De una sesion anterior");

        // Fuerza DOS pasadas: la 1 aporta algo nuevo, la 2 ya no. Sin esto el barrido se seca en
        // la primera y el test pasaria sin comprobar nada de lo que promete su nombre.
        int pass = 0;
        var agent = new FakeCopilotAgent(auditScript: _ =>
        {
            pass++;
            return pass == 1 ? new[] { SampleFinding() with { Title = "Algo nuevo" } } : Array.Empty<SubmitFindingArgs>();
        });

        SessionResult result = await RunLotes(agent);

        pass.Should().BeGreaterThan(1, "el test necesita mas de una pasada para tener sentido");
        result.Counters.Confirmed.Should().Be(1, "una auditoria, una confirmacion");
        _hub.Store.TryReadFinding("app", previous.Id.ToString())!.TimesConfirmed.Should().Be(2);
    }

    // ---------- F4.1 · consolidación por ubicaciones ----------

    /// <summary>
    /// Un defecto sistémico es UN hallazgo con N ubicaciones. <c>add_locations</c> extiende el que
    /// ya existe en vez de crear otro — es la pieza que faltaba: sin ella, decir "esto también
    /// pasa en la línea 105" obligaba a duplicar, y por eso el barrido no convergía (D-090).
    /// </summary>
    [Fact]
    public async Task Add_locations_extends_the_finding_instead_of_duplicating_it()
    {
        SetMaxPasses(3);

        int pass = 0;
        var agent = new FakeCopilotAgent(
            auditScript: _ =>
            {
                pass++;
                return pass == 1
                    ? new[] { SampleFinding() with { Title = "No valida argumentos nulos" } }
                    : Array.Empty<SubmitFindingArgs>();
            },
            // A partir de la 2ª pasada el auditor amplía el mismo hallazgo a otros dos puntos.
            extendScript: r => pass == 2
                ? r.Existing.Select(e => new AddLocationsArgs(
                    e.FindingId, new[] { new SubmitLocation("A.cs", 42, null), new SubmitLocation("A.cs", 77, null) }))
                : Array.Empty<AddLocationsArgs>());

        SessionResult result = await RunLotes(agent);

        result.Counters.New.Should().Be(1);
        result.Counters.LocationsAdded.Should().Be(2);

        Finding f = _hub.Store.ListFindings("app").Should().ContainSingle().Subject;
        f.Locations.Select(l => l.Line).Should().BeEquivalentTo(new[] { 1, 42, 77 });
    }

    /// <summary>
    /// Una pasada que SOLO extiende ubicaciones no está seca: extender es cobertura real, así que
    /// el barrido debe continuar para ver si aún queda más.
    /// </summary>
    [Fact]
    public async Task A_pass_that_only_adds_locations_is_not_dry()
    {
        SetMaxPasses(5);

        int pass = 0;
        var agent = new FakeCopilotAgent(
            auditScript: _ =>
            {
                pass++;
                return pass == 1 ? new[] { SampleFinding() } : Array.Empty<SubmitFindingArgs>();
            },
            extendScript: r => pass == 2
                ? r.Existing.Select(e => new AddLocationsArgs(e.FindingId, new[] { new SubmitLocation("A.cs", 9, null) }))
                : Array.Empty<AddLocationsArgs>());

        await RunLotes(agent);

        UnitVerdictRecord unit = _hub.Store.ListSessions("app").Single().Units.Single();
        unit.Passes.Should().HaveCount(4, "la 3 y la 4 son las dos secas seguidas que cierran");
        unit.Passes![1].LocationsAdded.Should().Be(1);
        unit.Passes[1].Dry.Should().BeFalse("extender ubicaciones es rendimiento de la pasada");
        unit.Passes[2].Dry.Should().BeTrue();
        unit.Passes[3].Dry.Should().BeTrue();
        unit.CoverageIncomplete.Should().BeFalse();
    }

    /// <summary>Extender un ULID que no está a la vista no toca nada y se le devuelve el error.</summary>
    [Fact]
    public async Task Add_locations_on_an_unknown_id_is_rejected()
    {
        SetMaxPasses(1);
        string ghost = _ulids.NewUlid().ToString();

        var agent = new FakeCopilotAgent(
            extendScript: _ => new[] { new AddLocationsArgs(ghost, new[] { new SubmitLocation("A.cs", 5, null) }) });

        SessionResult result = await RunLotes(agent);

        result.Counters.Rejected.Should().Be(1);
        result.Counters.LocationsAdded.Should().Be(0);
        _hub.Store.ListSessions("app").Single().Notes
            .Should().Contain(n => n.Contains("add_locations rechazado") && n.Contains(ghost));
    }

    /// <summary>
    /// Una ubicación fuera de la unidad se rechaza: el auditor solo ha visto esta unidad, así que
    /// no puede afirmar nada sobre otro fichero.
    /// </summary>
    [Fact]
    public async Task Add_locations_outside_the_audited_unit_is_rejected()
    {
        SetMaxPasses(1);
        Finding existing = SeedExisting("Ya existia");

        var agent = new FakeCopilotAgent(
            extendScript: _ => new[]
            {
                new AddLocationsArgs(existing.Id.ToString(), new[] { new SubmitLocation("B.cs", 3, null) }),
            });

        SessionResult result = await RunLotes(agent);

        result.Counters.LocationsAdded.Should().Be(0);
        _hub.Store.TryReadFinding("app", existing.Id.ToString())!.Locations.Should().ContainSingle();
        _hub.Store.ListSessions("app").Single().Notes
            .Should().Contain(n => n.Contains("fuera de la unidad"));
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

    /// <summary>
    /// F5.1b: un lote vacío es como el auditor dice «no hay nada nuevo», que es la respuesta normal
    /// de un barrido que converge. Contarlo como payload rechazado pintaba un ⚠ en el informe donde
    /// no había ningún problema. La llamada sigue registrada en la traza de tools: no se traga nada.
    /// </summary>
    [Fact]
    public async Task An_empty_batch_means_nothing_new_not_a_rejected_payload()
    {
        SetMaxPasses(1);

        // El agente falso se salta la tool cuando no tiene nada que enviar, así que hace falta uno
        // que la llame de verdad con el array vacío — que es lo que hizo gpt-5.5 el 2026-08-25.
        SessionResult result = await RunLotes(new SubmitsAnEmptyBatch());

        result.Counters.Rejected.Should().Be(0);
        result.Counters.New.Should().Be(0);

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Notes.Should().NotContain(n => n.Contains("rechazo"));
        session.Units.Single().RejectedPayloads.Should().Be(0);

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));
        report.Should().NotContain("Payloads rechazados");
    }

    // ---------- F5.1 — configuración visible en la sesión y en el informe ----------

    /// <summary>
    /// El tope de pasadas es un ajuste de la máquina (Ajustes) y se aplica de verdad. Sin
    /// registrarlo, leer una sesión vieja marcada «cobertura posiblemente incompleta» no permitiría
    /// distinguir «el modelo no convergió» de «el tope estaba en 1».
    /// </summary>
    [Fact]
    public async Task The_sweep_cap_from_settings_is_applied_recorded_and_reported()
    {
        SetMaxPasses(2);
        // Un agente que siempre reporta algo nuevo: nunca se seca, asi que agota el tope.
        int n = 0;
        var agent = new FakeCopilotAgent(_ => new[]
        {
            SampleFinding() with { Title = $"Hallazgo {++n}", Symbol = $"A.M{n}" },
        });

        SessionResult result = await RunLotes(agent);

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.MaxPassesPerUnit.Should().Be(2);
        session.Units.Single().Passes.Should().HaveCount(2, "el tope de Ajustes es el que manda");

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));
        report.Should().Contain("Pasadas del barrido (tope)**: 2");
    }

    /// <summary>Cambiar el tope en Ajustes cambia el barrido de la siguiente sesión, sin más.</summary>
    [Fact]
    public async Task Changing_the_cap_changes_the_next_sweep()
    {
        SetMaxPasses(3);
        int n = 0;
        SubmitFindingArgs[] Script(AuditUnitRequest _) => new[]
        {
            SampleFinding() with { Title = $"Hallazgo {++n}", Symbol = $"A.M{n}" },
        };

        await RunLotes(new FakeCopilotAgent(Script));
        _hub.Store.ListSessions("app").Single().Units.Single().Passes.Should().HaveCount(3);

        SetMaxPasses(1);
        await RunLotes(new FakeCopilotAgent(Script));

        AuditSession second = _hub.Store.ListSessions("app").OrderBy(s => s.StartedUtc).Last();
        second.MaxPassesPerUnit.Should().Be(1);
        second.Units.Single().Passes.Should().HaveCount(1);
    }

    /// <summary>
    /// El modelo con el que corre el agente queda en la sesión y en el informe. Antes el informe
    /// decía siempre «Modelo: n/d» porque nadie le pasaba el modelo al agente.
    /// </summary>
    [Fact]
    public async Task The_model_in_use_reaches_the_session_and_the_report()
    {
        SetMaxPasses(1);
        var agent = new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>(), modelName: "claude-sonnet-4.5");

        SessionResult result = await RunLotes(agent);

        _hub.Store.ListSessions("app").Single().Model.Should().Be("claude-sonnet-4.5");

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()));
        report.Should().Contain("Modelo**: claude-sonnet-4.5");
        report.Should().NotContain("n/d");
    }

    // ---------- F5.1 — re-auditar una unidad ya auditada ----------

    /// <summary>
    /// Re-auditar es una sesión normal (caso real: volver sobre una unidad tras arreglar sus
    /// hallazgos). Ni la selección ni el coordinador filtran por estado: la unidad ya auditada se
    /// audita otra vez y la reconciliación hace el resto.
    /// </summary>
    [Fact]
    public async Task An_already_audited_unit_can_be_audited_again()
    {
        SetMaxPasses(1);
        Finding existing = SeedExisting("Sigue ahi");
        MarkAudited("A.cs");

        SessionResult result = await RunLotes(new FakeCopilotAgent());

        result.Counters.Confirmed.Should().Be(1, "la re-auditoría reconcilia lo que ya había");
        result.Counters.New.Should().Be(0);
        _hub.Store.ListFindings("app").Should().ContainSingle("re-auditar no duplica");
        _hub.Store.TryReadFinding("app", existing.Id.ToString())!.TimesConfirmed.Should().Be(2);
        _hub.Store.ListSessions("app").Single().Units.Single().Verdict.Should().Be("auditada");
    }

    /// <summary>
    /// Y si el hallazgo se arregló de verdad entre una sesión y la siguiente, la re-auditoría es
    /// justo la vía por la que se resuelve.
    /// </summary>
    [Fact]
    public async Task Re_auditing_after_a_fix_resolves_the_finding()
    {
        SetMaxPasses(1);
        Finding existing = SeedExisting("Ya arreglado");
        MarkAudited("A.cs");

        var agent = new FakeCopilotAgent(reconcileScript: _ => new[]
        {
            new VerdictArgs(existing.Id.ToString(), "arreglado", "el código ya valida el argumento"),
        });

        SessionResult result = await RunLotes(agent);

        result.Counters.Resolved.Should().Be(1);
        _hub.Store.TryReadFinding("app", existing.Id.ToString())!.Status.Should().Be(FindingStatus.Resuelto);
    }

    /// <summary>Marca una unidad como ya auditada, como la dejaría una sesión anterior.</summary>
    private void MarkAudited(string path)
    {
        InventoryCycle inv = _hub.Store.TryReadInventory("app", 1)!;
        inv.Units.Single(u => u.Path == path).State = UnitState.Auditada;
        _hub.Store.WriteInventory("app", inv);
    }

    /// <summary>Agente que invoca <c>submit_findings</c> con un array VACÍO: "no hay nada nuevo".</summary>
    private sealed class SubmitsAnEmptyBatch : IAuditorProvider
    {
        public string? ModelName => "empty-batch";
        public event Action<string>? TextStreamed { add { } remove { } }
        public event Action<UsageSample>? UsageReported { add { } remove { } }

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));
        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            toolbox.SubmitFindings(Array.Empty<SubmitFindingArgs>());
            toolbox.UnitDone(request.UnitPath, "Revisados: todo. Nada nuevo.");
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Agente de test que emite un <c>UsageSample</c> lo bastante grande para disparar el
    /// presupuesto por unidad y, opcionalmente, empuja N payloads inválidos por el toolbox
    /// (<c>severity</c> desconocida) para probar el conteo y la moda de motivo de rechazo.</summary>
    private sealed class BudgetTrippingAgent : IAuditorProvider
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

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(new[] { new AgentModel("budget-trip", "budget-trip") });

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

    // ---------------------------------------------------------------- F7 · directivas

    /// <summary>
    /// F7: el área de criterio con la que se reporta que el código CONTRADICE una convención del
    /// propio proyecto. Tiene que atravesar la validación de payloads igual que cualquier otra —si
    /// el auditor la lee en el prompt y la app se la rechaza, la regla (b) de la sección de
    /// directivas sería una instrucción imposible de cumplir— y salir etiquetada como criterio.
    /// </summary>
    [Fact]
    public async Task Criterio_directivas_se_acepta_y_se_etiqueta_como_criterio()
    {
        SubmitFindingArgs contradiction = SampleFinding("A.cs") with
        {
            RuleId = "criterio.directivas",
            Symbol = "A.M",
            Title = "Usa una clase mutable donde AGENTS.md manda records",
        };

        SessionResult result = await RunLotes(new FakeCopilotAgent(_ => new[] { contradiction }));

        result.Counters.New.Should().Be(1);
        Finding stored = _hub.Store.ListFindings("app").Single();
        stored.RuleId.Should().Be("criterio.directivas");
        stored.Tag.Should().Be(FindingTag.Criterio);
    }

    /// <summary>El coordinador con las directivas del proyecto conectadas (F7).</summary>
    private SessionCoordinator CoordinatorWithDirectives(IAuditorProvider agent, out DirectiveService directives)
    {
        directives = new DirectiveService(_hub, new DirectiveScanner(), _ulids);
        return new SessionCoordinator(
            _hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings,
            directives: directives);
    }

    [Fact]
    public async Task Las_directivas_activas_viajan_en_el_prompt_del_auditor_y_quedan_en_la_sesion()
    {
        File.WriteAllText(Path.Combine(_clone, "AGENTS.md"), "En este proyecto usamos records.");

        var prompts = new List<string>();
        var agent = new FakeCopilotAgent(r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        });

        SessionCoordinator coordinator = CoordinatorWithDirectives(agent, out DirectiveService directives);
        directives.SetScope("app", "AGENTS.md", "agents", DirectiveScope.Auditoria);

        await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        prompts.Should().ContainSingle();
        prompts[0].Should().Contain("DIRECTIVAS DEL PROYECTO");
        prompts[0].Should().Contain("En este proyecto usamos records.");
        prompts[0].Should().Contain("MÉTODO DE BARRIDO", "las reglas de operación siguen mandando");

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Directives.Should().ContainSingle()
            .Which.Path.Should().Be("AGENTS.md");
        session.Directives[0].ContentHash.Should().StartWith("sha256:");
    }

    /// <summary>
    /// Una directiva de ámbito Arreglo NO informa al auditor. El ámbito es lo único que decide en
    /// qué prompt viaja cada fichero: si aquí se colara, «Arreglo» no significaría nada.
    /// </summary>
    [Fact]
    public async Task Una_directiva_de_ambito_arreglo_no_viaja_en_la_auditoria()
    {
        File.WriteAllText(Path.Combine(_clone, "AGENTS.md"), "solo-para-el-arreglo");

        var prompts = new List<string>();
        var agent = new FakeCopilotAgent(r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        });

        SessionCoordinator coordinator = CoordinatorWithDirectives(agent, out DirectiveService directives);
        directives.SetScope("app", "AGENTS.md", "agents", DirectiveScope.Arreglo);

        await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        prompts[0].Should().NotContain("solo-para-el-arreglo");
        _hub.Store.ListSessions("app").Single().Directives.Should().BeEmpty();
    }

    /// <summary>Un candidato detectado y NO curado no informa a nadie: detectar no es activar.</summary>
    [Fact]
    public async Task Un_candidato_sin_activar_no_viaja_en_ningun_prompt()
    {
        File.WriteAllText(Path.Combine(_clone, "AGENTS.md"), "todavia-nadie-lo-ha-decidido");

        var prompts = new List<string>();
        var agent = new FakeCopilotAgent(r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        });

        await CoordinatorWithDirectives(agent, out _).RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        prompts[0].Should().NotContain("todavia-nadie-lo-ha-decidido");
        prompts[0].Should().NotContain("DIRECTIVAS DEL PROYECTO");
    }
}
