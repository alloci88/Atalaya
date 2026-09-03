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
/// F17 §3 — <b>la reconciliación en un ciclo temático</b>, que es la consistencia difícil.
/// <para>
/// Un ciclo de Rendimiento encontrará unidades con hallazgos previos de Seguridad. La regla: el
/// auditor temático reconcilia SOLO los de su temática; los de otras no se tocan — ni se
/// confirman, ni se resuelven, ni se disputan —, y si el modelo lo intenta la aplicación lo
/// rechaza con error tipado. Con General todo se reconcilia, exactamente como antes de F17.
/// </para>
/// </summary>
public sealed class ThematicReconciliationTests : IDisposable
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

    public ThematicReconciliationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-theme", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);

        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A { void M() { } }");
        _machines.SetClonePath("app", _clone);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Un temporal que no se deja borrar no invalida el test.
        }
    }

    // ---------------------------------------------------------------- utillaje

    private void Cycle(AuditTheme theme)
        => _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Theme = theme,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

    private Finding Seed(string title, AuditTheme theme, string ruleId = "errores.null.desreferencia")
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
            Theme = theme,
            Locations = { new Location("A.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    private static SubmitFindingArgs NewFinding(string title)
        => new("optimizacion.coleccion.ineficiente", "optimizacion", "media", title, "desc", "impact", "reco",
            new[] { new SubmitLocation("A.cs", 1, "snippet") }, "A.M");

    private Task<SessionResult> Run(IAuditorProvider agent)
        => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

    private Finding Read(Finding f) => _hub.Store.TryReadFinding("app", f.Id.ToString())!;

    // ---------------------------------------------------------------- §3

    /// <summary>
    /// El caso de aceptación: ciclo de Rendimiento, hallazgo previo de Seguridad en la unidad. La
    /// pasada no lo lista para reconciliar, el auditor no se pronuncia, y el hallazgo queda
    /// INTACTO — sin que la unidad se cierre como incompleta por él.
    /// </summary>
    [Fact]
    public async Task Un_hallazgo_de_otra_tematica_queda_intacto_tras_la_pasada()
    {
        Cycle(AuditTheme.Rendimiento);
        Finding security = Seed("Credencial en claro", AuditTheme.Seguridad);
        IReadOnlyList<ExistingFinding>? listed = null;
        var agent = new FakeCopilotAgent(auditScript: r =>
        {
            listed = r.Existing;
            return new[] { NewFinding("Doble recorrido") };
        });

        SessionResult result = await Run(agent);

        listed.Should().NotBeNull().And.BeEmpty("el de Seguridad no es reconciliable en un ciclo de Rendimiento");
        result.IncompleteUnits.Should().Be(0, "no se le pidió veredicto, así que no falta ninguno");
        result.Counters.Confirmed.Should().Be(0);
        result.Counters.Resolved.Should().Be(0);

        Finding untouched = Read(security);
        untouched.Status.Should().Be(FindingStatus.Activo);
        untouched.TimesConfirmed.Should().Be(1);
        untouched.LastConfirmed.Utc.Should().Be(security.LastConfirmed.Utc, "nadie lo miró: envejece");
        untouched.Theme.Should().Be(AuditTheme.Seguridad);
    }

    [Fact]
    public async Task El_hallazgo_nuevo_nace_con_la_tematica_del_ciclo()
    {
        Cycle(AuditTheme.Rendimiento);

        SessionResult result = await Run(new FakeCopilotAgent(auditScript: _ => new[] { NewFinding("Doble recorrido") }));

        result.Counters.New.Should().Be(1);
        _hub.Store.ListFindings("app").Single().Theme.Should().Be(AuditTheme.Rendimiento);
    }

    /// <summary>De la misma temática se reconcilia con normalidad: aquí, un «arreglado» que resuelve.</summary>
    [Fact]
    public async Task Un_hallazgo_de_la_misma_tematica_se_reconcilia_normal()
    {
        Cycle(AuditTheme.Rendimiento);
        Finding perf = Seed("Recorrido doble", AuditTheme.Rendimiento, "optimizacion.coleccion.ineficiente");
        Finding security = Seed("Credencial en claro", AuditTheme.Seguridad);
        var agent = new FakeCopilotAgent(reconcileScript: r =>
        {
            r.Existing.Should().ContainSingle(e => e.FindingId == perf.Id.ToString());
            return new[] { new VerdictArgs(perf.Id.ToString(), "arreglado", "ahora recorre una vez") };
        });

        SessionResult result = await Run(agent);

        result.Counters.Resolved.Should().Be(1);
        result.IncompleteUnits.Should().Be(0);
        Read(perf).Status.Should().Be(FindingStatus.Resuelto);
        Read(security).Status.Should().Be(FindingStatus.Activo);
        Read(security).TimesConfirmed.Should().Be(1);
    }

    /// <summary>
    /// El modelo insiste en juzgar el de Seguridad desde una pasada de Rendimiento: error tipado,
    /// nada se toca, y el rechazo queda escrito en la sesión con su motivo.
    /// </summary>
    [Fact]
    public async Task Un_veredicto_sobre_un_hallazgo_de_otra_tematica_se_rechaza_y_no_toca_nada()
    {
        Cycle(AuditTheme.Rendimiento);
        Finding security = Seed("Credencial en claro", AuditTheme.Seguridad);
        var agent = new FakeCopilotAgent(reconcileScript: _ => new[]
        {
            new VerdictArgs(security.Id.ToString(), "arreglado", "ya no está la contraseña"),
        });

        SessionResult result = await Run(agent);

        result.Counters.Rejected.Should().Be(1);
        result.Counters.Resolved.Should().Be(0);
        result.IncompleteUnits.Should().Be(0);
        Read(security).Status.Should().Be(FindingStatus.Activo);
        Read(security).TimesConfirmed.Should().Be(1);

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Notes.Should().Contain(n => n.Contains("otra temática") && n.Contains("Seguridad") && n.Contains("Rendimiento"));
    }

    /// <summary>El prompt de la pasada enseña el de otra temática como «no lo juzgues», y solo ahí.</summary>
    [Fact]
    public async Task El_prompt_ensena_el_de_otra_tematica_aparte_para_que_no_se_re_reporte()
    {
        Cycle(AuditTheme.Rendimiento);
        Finding security = Seed("Credencial en claro", AuditTheme.Seguridad);
        string? prompt = null;
        var agent = new FakeCopilotAgent(auditScript: r =>
        {
            prompt = r.Prompt;
            return Array.Empty<SubmitFindingArgs>();
        });

        await Run(agent);

        prompt.Should().NotBeNull();
        prompt.Should().Contain($"{ThemeSection.Heading}: RENDIMIENTO.");
        prompt.Should().Contain("HALLAZGOS DE OTRAS TEMÁTICAS EN A.cs");
        prompt.Should().Contain(security.Id.ToString());
        prompt.Should().NotContain($"findingId: {security.Id}");
    }

    /// <summary>La sesión y su informe registran con qué lupa se auditó.</summary>
    [Fact]
    public async Task La_sesion_y_el_informe_registran_la_tematica_del_ciclo()
    {
        Cycle(AuditTheme.Fiabilidad);

        SessionResult result = await Run(new FakeCopilotAgent());

        _hub.Store.ListSessions("app").Single().Theme.Should().Be(AuditTheme.Fiabilidad);
        File.ReadAllText(_hub.HubPaths.ReportFile("app", result.SessionId.ToString()))
            .Should().Contain("**Temática**: Fiabilidad");
    }

    /// <summary>
    /// El anti-objetivo: con General TODO se reconcilia, sea de la temática que sea. Es el
    /// comportamiento de antes de F17, sin un solo cambio.
    /// </summary>
    [Fact]
    public async Task Con_General_se_reconcilian_los_hallazgos_de_todas_las_tematicas()
    {
        Cycle(AuditTheme.General);
        Finding security = Seed("Credencial en claro", AuditTheme.Seguridad);
        Finding perf = Seed("Recorrido doble", AuditTheme.Rendimiento, "optimizacion.coleccion.ineficiente");
        Finding legacy = Seed("Nulo sin comprobar", AuditTheme.General);
        string? prompt = null;
        var agent = new FakeCopilotAgent(auditScript: r =>
        {
            prompt = r.Prompt;
            return Array.Empty<SubmitFindingArgs>();
        });

        SessionResult result = await Run(agent);

        result.Counters.Confirmed.Should().Be(3);
        result.IncompleteUnits.Should().Be(0);
        foreach (Finding f in new[] { security, perf, legacy })
        {
            Read(f).TimesConfirmed.Should().Be(2);
        }

        prompt.Should().NotContain(ThemeSection.Heading).And.NotContain("HALLAZGOS DE OTRAS TEMÁTICAS");
        prompt.Should().Contain("HALLAZGOS YA EXISTENTES EN A.cs (reconcilia TODOS con report_verdicts):");
    }

    /// <summary>Un ciclo escrito antes de F17 no lleva temática: es General y reconcilia todo.</summary>
    [Fact]
    public async Task Un_ciclo_anterior_a_F17_reconcilia_como_General()
    {
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });
        Finding perf = Seed("Recorrido doble", AuditTheme.Rendimiento, "optimizacion.coleccion.ineficiente");

        SessionResult result = await Run(new FakeCopilotAgent());

        result.Counters.Confirmed.Should().Be(1);
        Read(perf).TimesConfirmed.Should().Be(2);
        _hub.Store.ListSessions("app").Single().Theme.Should().Be(AuditTheme.General);
    }

    [Fact]
    public void El_alcance_es_una_sola_pregunta()
    {
        ThemeScope.Reconciles(AuditTheme.General, AuditTheme.Seguridad).Should().BeTrue();
        ThemeScope.Reconciles(AuditTheme.Rendimiento, AuditTheme.Rendimiento).Should().BeTrue();
        ThemeScope.Reconciles(AuditTheme.Rendimiento, AuditTheme.Seguridad).Should().BeFalse();
        ThemeScope.Reconciles(AuditTheme.Rendimiento, AuditTheme.General).Should().BeFalse(
            "un hallazgo General se vio con la mirada completa; una lupa concreta no lo juzga");
    }
}
