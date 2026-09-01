using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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
/// BUGFIX-CIERRE y BUGFIX-ACTIVIDAD — lo que pasa DESPUÉS de que una sesión falle.
/// <para>
/// El parte: tras agotarse la cuota, «Sesión fallida» y «Arreglo fallido» se quedaron fijos en el
/// rail sin forma de cerrarse, y la tarjeta de XBLAST siguió diciendo «auditando ahora» incluso
/// tras reiniciar la aplicación. Lo segundo no era un residuo en memoria: los claims —que son la
/// ÚNICA fuente de «alguien está auditando esto»— solo los soltaba el cierre ordenado del
/// coordinador, y una excepción se lo saltaba entero.
/// </para>
/// </summary>
public sealed class TerminalScreenTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";
    private const string UnitPath = "src/A.cs";

    private const string RealQuotaError =
        "Session error: You have exceeded your monthly quota "
        + "(Request ID: FA81:2498A4:244E7F9:2DAED35:6A951C79)";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public TerminalScreenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-terminal", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        string clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(clone, RepoUrl);
        Directory.CreateDirectory(Path.Combine(clone, "src"));
        File.WriteAllText(Path.Combine(clone, "src", "A.cs"), "class A { }");
        _machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = UnitPath, Module = "M", State = UnitState.Pendiente } },
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Un agente que revienta con el error real de cuota en la primera unidad.</summary>
    private sealed class QuotaAgent : IAuditorProvider
    {
        public string? ModelName => "gpt-5";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(new[] { new AgentModel("gpt-5", "GPT-5") });

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(10, 5, null, ModelName));
            var inner = new InvalidOperationException(RealQuotaError);
            throw new AuditorProviderException(
                CopilotHelp.QuotaExhausted("mensual"), AgentProblem.QuotaExhausted,
                CopilotFailure.Raw(inner), inner);
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Un agente que audita bien, para comprobar que se puede relanzar tras el fallo.</summary>
    private sealed class GoodAgent : IAuditorProvider
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public string? ModelName => "gpt-5";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(new[] { new AgentModel("gpt-5", "GPT-5") });

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke("revisando");
            UsageReported?.Invoke(new UsageSample(10, 5, null, ModelName));
            if (_seen.Add(request.UnitPath))
            {
                toolbox.SubmitFinding(new SubmitFindingArgs(
                    "errores.recursos.no-liberado", "errores", "alta", "algo", "d", "i", "r",
                    new[] { new SubmitLocation(request.UnitPath, 1, null) }, "S"));
            }

            toolbox.UnitDone(request.UnitPath, "cubierta");
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }

    private LiveSessionService Live(IAuditorProvider agent)
        => new(
            () => new SessionCoordinator(
                _hub, new FindingIngestionService(_hub, _ulids), new ReconciliationService(_hub),
                _machines, _ulids, agent, _settings),
            agent, new OpenSessionStore(_paths), _hub);

    private static async Task Wait(LiveSessionService live)
    {
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        live.IsRunning.Should().BeFalse();
    }

    private bool AuditingNow() => new PortfolioQuery(_hub.Store).Build("app")!.AuditingNow;

    // ================================================================ el estado «en curso» muere

    [Fact]
    public async Task Una_sesion_que_falla_suelta_lo_que_anunciaba()
    {
        LiveSessionService live = Live(new QuotaAgent());

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        live.HasFailed.Should().BeTrue();
        _hub.Store.ListClaims("app").Should().BeEmpty(
            "los claims son la ÚNICA fuente de «auditando ahora»: si el fallo no los suelta, la "
            + "tarjeta miente hasta que caduque el TTL");
        AuditingNow().Should().BeFalse("y el Portafolio deja de anunciar actividad en el acto");
    }

    [Fact]
    public async Task Y_la_marca_de_sesion_abierta_no_se_queda_colgada()
    {
        LiveSessionService live = Live(new QuotaAgent());

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        new OpenSessionStore(_paths).TryRead().Should().BeNull();
    }

    [Fact]
    public async Task Una_sesion_detenida_tambien_deja_de_anunciarse()
    {
        LiveSessionService live = Live(new GoodAgent());

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        live.Stop();
        await Wait(live);

        _hub.Store.ListClaims("app").Should().BeEmpty();
        AuditingNow().Should().BeFalse();
    }

    [Fact]
    public async Task Se_puede_relanzar_tras_un_fallo_sin_reiniciar_la_aplicacion()
    {
        LiveSessionService live = Live(new QuotaAgent());
        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        // Misma instancia del servicio, agente que ahora sí responde.
        LiveSessionService second = Live(new GoodAgent());
        await second.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(second);

        second.HasFinished.Should().BeTrue("relanzar tras un fallo no puede pedir reiniciar la app");
        second.HasFailed.Should().BeFalse();
        _hub.Store.ListClaims("app").Should().BeEmpty("y la segunda también cierra lo que anunció");
    }

    // ================================================================ autocuración al arrancar

    /// <summary>
    /// El caso REAL de XBLAST: un claim de esta máquina, sin ninguna marca detrás —porque el
    /// <c>finally</c> la borró— y por tanto invisible para la recuperación de D-110. Al arrancar
    /// no hay ningún proceso nuestro auditando, así que es basura por definición.
    /// </summary>
    [Fact]
    public void Un_claim_propio_sin_marca_se_suelta_al_arrancar()
    {
        WriteClaim(machine: Environment.MachineName, minutesAgo: 5);

        StartupCleanup cleanup = Recovery().CleanUpAtStartup();

        cleanup.OrphanClaims.Should().Be(1);
        cleanup.Message.Should().Contain("no llegó a cerrarse");
        _hub.Store.ListClaims("app").Should().BeEmpty();
        AuditingNow().Should().BeFalse("es exactamente lo que dejaba a XBLAST diciendo «auditando ahora»");
    }

    [Fact]
    public void Un_claim_de_otra_maquina_no_se_toca()
    {
        WriteClaim(machine: "EL-PORTATIL-DE-OTRO", minutesAgo: 5);

        StartupCleanup cleanup = Recovery().CleanUpAtStartup();

        cleanup.OrphanClaims.Should().Be(0);
        _hub.Store.ListClaims("app").Should().ContainSingle(
            "no es nuestro: borrarlo mientras un compañero audita es el fallo que los claims evitan");
        AuditingNow().Should().BeTrue("y reciente, así que su actividad es creíble");
    }

    [Fact]
    public void Un_claim_ajeno_y_callado_deja_de_anunciarse_sin_borrarse()
    {
        WriteClaim(machine: "EL-PORTATIL-DE-OTRO", minutesAgo: 31);

        Recovery().CleanUpAtStartup();

        _hub.Store.ListClaims("app").Should().ContainSingle("sigue sin ser nuestro");
        AuditingNow().Should().BeFalse(
            "a un compañero se le cerró el portátil: mejor no decir nada que mentir al equipo");
    }

    [Fact]
    public void Con_otra_instancia_viva_en_esta_maquina_no_se_toca_nada()
    {
        WriteClaim(machine: Environment.MachineName, minutesAgo: 1);
        new OpenSessionStore(_paths).Write(new OpenSessionMarker
        {
            SessionId = _ulids.NewUlid().ToString(),
            Slug = "app",
            Units = { UnitPath },
            StartedUtc = DateTimeOffset.UtcNow,
        });

        // Un proceso que sigue vivo: es otra ventana de Atalaya auditando ahora mismo.
        StartupCleanup cleanup = new InterruptedSessionRecovery(
            _hub, new OpenSessionStore(_paths), (_, _) => true).CleanUpAtStartup();

        cleanup.DidSomething.Should().BeFalse();
        _hub.Store.ListClaims("app").Should().ContainSingle(
            "son los claims que esa instancia está usando ahora mismo");
    }

    [Fact]
    public void Una_marca_de_proceso_muerto_se_cierra_como_interrumpida_y_suelta_sus_claims()
    {
        Ulid id = _ulids.NewUlid();
        WriteClaim(machine: Environment.MachineName, minutesAgo: 2);
        new OpenSessionStore(_paths).Write(new OpenSessionMarker
        {
            SessionId = id.ToString(),
            Slug = "app",
            Units = { UnitPath },
            UnitsDone = 0,
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        });

        StartupCleanup cleanup = Recovery().CleanUpAtStartup();

        cleanup.Session.Should().NotBeNull();
        _hub.Store.ListSessions("app").Should().ContainSingle(s => s.Interrupted);
        _hub.Store.ListClaims("app").Should().BeEmpty();
        AuditingNow().Should().BeFalse();
    }

    [Fact]
    public void Sin_nada_colgado_el_arranque_no_dice_nada()
        => Recovery().CleanUpAtStartup().Message.Should().BeNull(
            "un aviso que informa de que no hay nada que informar es ruido");

    // ================================================================ cerrar la pantalla terminal

    [Fact]
    public async Task Una_sesion_fallida_se_puede_cerrar_y_desaparece_del_rail()
    {
        LiveSessionService live = Live(new QuotaAgent());
        var vm = new SessionViewModel(live);

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        vm.CanClose.Should().BeTrue("está en un estado terminal: hay que poder quitarla de en medio");
        live.HasSession.Should().BeTrue();

        live.Close().Should().BeTrue();

        live.HasSession.Should().BeFalse("de esto cuelga la entrada del rail");
        live.HasFailed.Should().BeFalse();
        vm.ShowFailure.Should().BeFalse();
    }

    [Fact]
    public async Task Cerrar_no_borra_el_informe_ni_la_sesion()
    {
        LiveSessionService live = Live(new GoodAgent());
        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        string sessionId = live.SessionId;
        string report = live.ReportPath;
        File.Exists(report).Should().BeTrue();

        live.Close().Should().BeTrue();

        File.Exists(report).Should().BeTrue("cerrar quita la pantalla de en medio, no borra historia");
        _hub.Store.ListSessions("app").Should().Contain(s => s.Id.ToString() == sessionId);
        _hub.Store.ListFindings("app").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Mientras_corre_no_hay_cerrar()
    {
        LiveSessionService live = Live(new GoodAgent());
        var vm = new SessionViewModel(live);
        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });

        if (live.IsRunning)
        {
            vm.CanClose.Should().BeFalse("mientras corre lo que hay es «Detener», que es otra cosa");
            live.Close().Should().BeFalse();
        }

        await Wait(live);
    }

    [Fact]
    public void Sin_sesion_no_hay_nada_que_cerrar()
        => Live(new GoodAgent()).Close().Should().BeFalse();

    // ================================================================ andamiaje

    private InterruptedSessionRecovery Recovery()
        => new(_hub, new OpenSessionStore(_paths), (_, _) => false);

    private void WriteClaim(string machine, int minutesAgo)
        => _hub.Store.WriteClaim("app", new Claim
        {
            Unit = UnitPath,
            Module = "M",
            By = "alguien",
            Machine = machine,
            Utc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        });
}
