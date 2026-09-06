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
/// R2 §1 — <b>el modo exhaustivo</b>: el camino de respaldo de F25 con un interruptor delante.
/// <para>
/// Lo que estos tests fijan, en este orden: que encendido el barrido sea EXACTAMENTE el de respaldo
/// —una petición por pasada, con el prompt recompuesto, byte a byte como la línea base— y que
/// apagado siga siendo el hilo; que no sea un tercer camino, sino el mismo <c>threadless</c> que ya
/// existía; y que quede dicho en los tres sitios donde se puede leer una sesión: la cabecera del
/// informe, el pie en vivo y el registro del hub.
/// </para>
/// <para>
/// Y el que impide el error caro: <b>cambiar el interruptor no toca la sesión en curso</b>. La forma
/// de barrer se lee UNA vez, al arrancar, y desde ahí manda lo que la sesión escribió.
/// </para>
/// </summary>
public sealed class ExhaustiveModeTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly SettingsService _settings;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public ExhaustiveModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-r2", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 4;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        string clone = Path.Combine(_root, "app");
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "A.cs"), "class A { void Metodo() { int x = 1 / 0; } }");
        _machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SetExhaustive(bool on)
    {
        AppSettings s = _settings.Current;
        s.ExhaustiveSweep = on;
        _settings.Save(s);
    }

    private SessionCoordinator Coordinator(IAuditorProvider agent)
        => new(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings);

    private Task<SessionResult> Run(IAuditorProvider agent)
        => Coordinator(agent).RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

    private AuditSession Stored() => _hub.Store.ListSessions("app").Single();

    // ================================================================ el barrido

    /// <summary>
    /// <b>Encendido, es el camino de respaldo y nada más.</b> No se abre ninguna conversación, cada
    /// pasada pasa por <c>AuditUnitAsync</c> —que es lo que hacía el barrido antes de F25— y ninguna
    /// manda el turno de continuación. Y lo que viaja es, carácter a carácter, lo que recibe un
    /// proveedor que no sabe hilar: si no lo fuera, esto sería un tercer camino, no el respaldo.
    /// </summary>
    [Fact]
    public async Task Encendido_cada_pasada_es_una_peticion_nueva_con_el_prompt_recompuesto()
    {
        var linea = new UnitThreadTests.RecordingAgent();
        await Run(linea);

        SetExhaustive(true);
        var agent = new UnitThreadTests.ThreadingAgent();
        await Run(agent);

        agent.ThreadsOpened.Should().Be(0, "en exhaustivo no se abre conversación ninguna");
        agent.UnitCalls.Should().Be(agent.Turns.Count, "una petición por pasada");
        agent.Turns.Should().OnlyContain(t => t != PromptComposer.ContinuationTurn);
        agent.Turns.Should().BeEquivalentTo(linea.Turns, o => o.WithStrictOrdering(),
            "es el barrido de respaldo, no una tercera forma de barrer");
    }

    /// <summary>
    /// <b>Apagado, sigue siendo el hilo</b> (F25, D-921). Es la mitad que impide que el interruptor
    /// se cuele encendido: un test que solo comprobara el «sí» pasaría igual con el modo puesto de
    /// serie.
    /// </summary>
    [Fact]
    public async Task Apagado_la_unidad_se_audita_como_una_conversacion()
    {
        var agent = new UnitThreadTests.ThreadingAgent();
        await Run(agent);

        agent.ThreadsOpened.Should().Be(1);
        agent.UnitCalls.Should().Be(0);
        agent.Turns.Skip(1).Should().OnlyContain(t => t == PromptComposer.ContinuationTurn);
    }

    /// <summary>De fábrica está apagado: el hilo es lo que se entrega, y esto es la excepción.</summary>
    [Fact]
    public void De_fabrica_esta_apagado()
    {
        new AppSettings().ExhaustiveSweep.Should().BeFalse();
        _settings.Current.ExhaustiveSweep.Should().BeFalse();
    }

    /// <summary>
    /// <b>La regla de parada y el tope no se tocan</b> (D-755, D-812). El interruptor cambia cómo
    /// viaja una pasada, no cuántas hay ni cuándo se para: dos secas seguidas terminan igual por los
    /// dos caminos.
    /// </summary>
    [Fact]
    public async Task La_regla_de_parada_y_el_tope_mandan_igual_con_el_modo_encendido()
    {
        var hilo = new UnitThreadTests.ThreadingAgent();
        await Run(hilo);

        SetExhaustive(true);
        var exhaustivo = new UnitThreadTests.ThreadingAgent();
        await Run(exhaustivo);

        exhaustivo.Turns.Should().HaveCount(hilo.Turns.Count, "un turno es una pasada, y al revés");
        _settings.Current.MaxPassesPerUnit.Should().Be(4);
    }

    /// <summary>
    /// <b>Cambiarlo no afecta a la sesión en curso.</b> Se enciende a mitad del barrido —desde el
    /// evento de la pasada, que es exactamente cuando el usuario podría tocarlo— y la sesión termina
    /// como empezó: por el hilo, y registrada como no exhaustiva.
    /// </summary>
    [Fact]
    public async Task Encenderlo_a_mitad_de_un_barrido_no_cambia_la_sesion_en_curso()
    {
        var agent = new UnitThreadTests.ThreadingAgent();
        SessionCoordinator coordinator = Coordinator(agent);
        coordinator.PassStarted += (_, _) => SetExhaustive(true);

        await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        agent.ThreadsOpened.Should().Be(1, "la sesión sigue por donde empezó");
        agent.UnitCalls.Should().Be(0);
        Stored().Exhaustive.Should().BeFalse("y queda escrita como lo que fue");
        _settings.Current.ExhaustiveSweep.Should().BeTrue("el ajuste sí cambió: para la siguiente");
    }

    // ================================================================ que se sepa con qué se auditó

    /// <summary>
    /// <b>El modo queda escrito en la sesión del hub.</b> Es lo que permite a Métricas separar el
    /// coste de las dos formas de barrer: sin este campo, una sesión que cuesta el triple es
    /// indistinguible de un modelo que se portó mal.
    /// </summary>
    [Fact]
    public async Task El_modo_queda_registrado_en_la_sesion_del_hub()
    {
        SetExhaustive(true);
        await Run(new UnitThreadTests.ThreadingAgent());

        Stored().Exhaustive.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_el_modo_la_sesion_no_dice_que_fue_exhaustiva()
    {
        await Run(new UnitThreadTests.ThreadingAgent());

        Stored().Exhaustive.Should().BeFalse();
    }

    /// <summary>
    /// <b>La cabecera del informe lo dice</b>: «Modo: Lotes · exhaustivo». Va en la cabecera y no en
    /// el anexo porque cambia lo que el informe costó y lo que puede duplicar, y eso se lee antes de
    /// cualquier otra cosa.
    /// </summary>
    [Fact]
    public void La_cabecera_del_informe_dice_que_se_audito_en_exhaustivo()
    {
        Report(exhaustive: true).Should().Contain("**Modo**: Lotes · exhaustivo");
        Report(exhaustive: false).Should().Contain("**Modo**: Lotes")
            .And.NotContain("exhaustivo");
    }

    /// <summary>
    /// <b>Y la tira de la carcasa, con la palabra.</b> Quien deja una sesión corriendo mientras
    /// mira otra pantalla tiene que poder ver con qué se está pagando: el modo exhaustivo cuesta el
    /// triple y puede duplicar hallazgos, así que viaja con la tira y no solo con la vista.
    /// <para>
    /// <b>Y la tira NO repite el progreso</b> (UI-0053). La unidad y la pasada son de la barra de
    /// Sesión en vivo, que está a 44 px y ya las dice con más detalle. Si vuelven aquí, la carcasa
    /// gasta su único renglón contando dos veces lo mismo.
    /// </para>
    /// </summary>
    [Fact]
    public void La_tira_de_la_carcasa_dice_la_palabra_y_no_repite_el_progreso()
    {
        LiveSessionService live = Live(exhaustive: true);

        // Con el NOMBRE de la aplicación, no con su identificador (R12): la tira es un rótulo.
        live.ProgressLine.Should().Be("Auditando App · exhaustivo");
        live.ProgressLine.Should().NotContain("unidad").And.NotContain("pasada",
            "el progreso lo dice la barra de la vista, y esta tira está a 44 px de ella");
        new SessionViewModel(live).Footer.Select(f => f.Full)
            .Should().ContainInOrder("Unidad 2 de 5", "exhaustivo");

        LiveSessionService normal = Live(exhaustive: false);
        normal.ProgressLine.Should().Be("Auditando App");
        new SessionViewModel(normal).Footer.Select(f => f.Full).Should().NotContain("exhaustivo");
    }

    /// <summary>La palabra se escribe en un solo sitio: tres copias son dos sitios donde olvidarse.</summary>
    [Fact]
    public void La_palabra_del_modo_vive_en_un_solo_sitio()
    {
        AuditModes.Describe(AuditMode.Lotes, exhaustive: true).Should().Be("Lotes · exhaustivo");
        AuditModes.Describe(AuditMode.Lotes, exhaustive: false).Should().Be("Lotes");
        SettingsViewModel.ExhaustiveWarning.Should().Contain("×3 por unidad");
    }

    private static LiveSessionService Live(bool exhaustive)
        => new(
            () => throw new NotSupportedException("no se lanza ninguna sesión en este test"),
            new FakeCopilotAgent(),
            new OpenSessionStore(new AppPaths(Path.Combine(
                Path.GetTempPath(), "atalaya-r2-live", Guid.NewGuid().ToString("N")))))
        {
            IsRunning = true,
            AppSlug = "app",
            AppName = "App",
            UnitIndex = 2,
            UnitCount = 5,
            CurrentPassNumber = 3,
            Exhaustive = exhaustive,
        };

    private static string Report(bool exhaustive)
    {
        var session = new AuditSession
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alguien",
            Machine = "maquina",
            StartedUtc = DateTimeOffset.UtcNow,
            Provider = RealCopilotAgent.Id,
            Model = "gpt-5",
            CycleN = 1,
            Exhaustive = exhaustive,
        };
        session.Usage.Add(1_000, 500, 0, 0, null, calls: 4);

        return ReportBuilder.BuildSessionReport(
            new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 },
            session, Array.Empty<Finding>(), 0, 0, "Org", TestRates.Table());
    }
}
