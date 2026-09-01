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
/// BUGFIX-CUOTA — la cuota que se agota a MITAD del barrido no puede tirar el trabajo ya pagado.
/// <para>
/// El barrido solo capturaba <see cref="OperationCanceledException"/>. Cualquier otra excepción
/// —y la de cuota lo es— subía entera y se llevaba por delante el cierre ordenado: el registro de
/// la sesión, las marcas del inventario, la liberación de los claims y el informe. Las unidades ya
/// auditadas quedaban pagadas y sin rastro, y los claims bloqueando al resto del equipo hasta que
/// caducara el TTL.
/// </para>
/// </summary>
public sealed class QuotaMidSweepTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";

    /// <summary>El error real del log del 2026-08-31, literal.</summary>
    private const string RealQuotaError =
        "Session error: You have exceeded your monthly quota "
        + "(Request ID: FA81:2498A4:244E7F9:2DAED35:6A951C79)";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public QuotaMidSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-quota", Guid.NewGuid().ToString("N"));
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
        foreach (string name in new[] { "A", "B", "C" })
        {
            File.WriteAllText(Path.Combine(clone, "src", $"{name}.cs"), $"class {name} {{ }}");
        }

        _machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                new InventoryUnit { Path = "src/A.cs", Module = "M", State = UnitState.Pendiente },
                new InventoryUnit { Path = "src/B.cs", Module = "M", State = UnitState.Pendiente },
                new InventoryUnit { Path = "src/C.cs", Module = "M", State = UnitState.Pendiente },
            },
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Audita bien las primeras <c>okUnits</c> unidades y a partir de ahí devuelve lo que devolvió
    /// el runtime aquella mañana. Es exactamente la forma del fallo: el grifo se cierra a mitad.
    /// </summary>
    private sealed class QuotaAtUnitAgent : IAuditorProvider
    {
        private readonly int _okUnits;
        private readonly HashSet<string> _units = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        public QuotaAtUnitAgent(int okUnits) => _okUnits = okUnits;

        /// <summary>Unidades DISTINTAS que el agente ha llegado a ver. El barrido hace varias pasadas.</summary>
        public int UnitsSeen => _units.Count;

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
            UsageReported?.Invoke(new UsageSample(10, 5, null, ModelName));
            _units.Add(request.UnitPath);
            if (_units.Count > _okUnits)
            {
                // Lo que produce RealCopilotAgent.Translate ante el error real del proveedor.
                var raw = new InvalidOperationException(RealQuotaError);
                throw new AuditorProviderException(
                    CopilotHelp.QuotaExhausted("mensual"), AgentProblem.QuotaExhausted,
                    CopilotFailure.Raw(raw), raw);
            }

            TextStreamed?.Invoke("revisado");

            // Un hallazgo en la PRIMERA pasada de cada unidad; la segunda sale seca y la unidad se
            // cierra. Es el barrido hasta agotar de F4.1, y sin respetarlo la unidad nunca llega a
            // registrarse — que es justo la frontera que este parte tiene que probar.
            if (_reported.Add(request.UnitPath))
            {
                toolbox.SubmitFinding(new SubmitFindingArgs(
                    "errores.recursos.no-liberado", "errores", "alta",
                    $"defecto en {request.UnitPath}", "d", "i", "r",
                    new[] { new SubmitLocation(request.UnitPath, 1, null) }, "S"));
            }

            toolbox.UnitDone(request.UnitPath, "cubierta");
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }

    private SessionCoordinator Coordinator(IAuditorProvider agent)
        => new(_hub, new FindingIngestionService(_hub, _ulids), new ReconciliationService(_hub),
            _machines, _ulids, agent, _settings);

    private LiveSessionService Live(IAuditorProvider agent)
        => new(() => Coordinator(agent), agent, new OpenSessionStore(_paths), _hub);

    private static async Task Wait(LiveSessionService live)
    {
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        live.IsRunning.Should().BeFalse();
    }

    private static readonly string[] AllUnits = { "src/A.cs", "src/B.cs", "src/C.cs" };

    // ================================================================ el trabajo pagado se conserva

    [Fact]
    public async Task La_cuota_a_mitad_conserva_lo_auditado_y_lo_registra()
    {
        SessionResult result = await Coordinator(new QuotaAtUnitAgent(okUnits: 1))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, AllUnits), CancellationToken.None);

        result.Failure.Should().NotBeNull("la sesión no cubrió lo que decía cubrir");
        result.Failure!.Problem.Should().Be(AgentProblem.QuotaExhausted);
        result.Failure.UnitsDone.Should().Be(1);
        result.Failure.UnitsTotal.Should().Be(3);

        _hub.Store.ListFindings("app").Should().ContainSingle(
            "el hallazgo de la unidad que sí se auditó estaba pagado y sigue en el hub");

        AuditSession saved = _hub.Store.ListSessions("app").Should().ContainSingle().Subject;
        saved.Units.Should().ContainSingle("la sesión registra la unidad que llegó a cubrirse");
        saved.Notes.Should().Contain(n => n.Contains("cortada por el proveedor", StringComparison.OrdinalIgnoreCase),
            "dentro de un mes, quien mire por qué esta sesión cubrió una de tres tiene que leerlo aquí");
        saved.Notes.Should().Contain(n => n.Contains("Request ID", StringComparison.Ordinal),
            "y el crudo con él: es lo único que sirve para reclamar");
    }

    [Fact]
    public async Task La_unidad_auditada_queda_marcada_y_las_otras_siguen_pendientes()
    {
        await Coordinator(new QuotaAtUnitAgent(okUnits: 1))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, AllUnits), CancellationToken.None);

        InventoryCycle inv = _hub.Store.TryReadInventory("app", 1)!;
        inv.Units.Single(u => u.Path == "src/A.cs").State.Should().Be(UnitState.Auditada);
        inv.Units.Where(u => u.Path != "src/A.cs").Should()
            .OnlyContain(u => u.State == UnitState.Pendiente, "lo que no se miró sigue por mirar");
    }

    [Fact]
    public async Task No_se_prueba_ni_una_unidad_mas_contra_una_cuota_agotada()
    {
        var agent = new QuotaAtUnitAgent(okUnits: 1);

        await Coordinator(agent).RunAsync(
            new SessionRequest("app", AuditMode.Lotes, AllUnits), CancellationToken.None);

        agent.UnitsSeen.Should().Be(2,
            "una que fue bien y la que topó con la cuota. Seguir con la tercera sería quemar "
            + "llamadas del reset siguiente contra un grifo cerrado");
    }

    [Fact]
    public async Task Un_barrido_cortado_por_cuota_no_cierra_ciclo()
    {
        SessionResult result = await Coordinator(new QuotaAtUnitAgent(okUnits: 1))
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, AllUnits), CancellationToken.None);

        result.CycleClosed.Should().BeFalse();
        _hub.Store.TryReadApp("app")!.CurrentCycle.Should().Be(1);
    }

    // ================================================================ y la sesión en vivo lo dice

    [Fact]
    public async Task La_sesion_en_vivo_falla_alto_con_la_causa_y_para_el_reloj()
    {
        LiveSessionService live = Live(new QuotaAtUnitAgent(okUnits: 1));

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, AllUnits), AllUnits);
        await Wait(live);

        live.HasFailed.Should().BeTrue("es un estado terminal, no la ausencia de estado");
        live.IsRunning.Should().BeFalse("sin zombis: nada sigue corriendo");
        live.EndedUtc.Should().NotBeNull("el temporizador para, no se queda contando");

        live.FailureMessage.Should().Contain("AI credits");
        live.FailureMessage.Should().NotContain("no tiene asiento",
            "ÉSTE es el fallo del parte: el asiento está, lo que falta son peticiones");
        live.FailureMessage.Should().Contain("1 de 3", "y dice qué se salvó");
        live.FailureDetail.Should().Contain("FA81:2498A4:244E7F9:2DAED35:6A951C79",
            "el crudo, copiable, para quien administre la organización");
    }

    [Fact]
    public async Task Y_el_resumen_de_cierre_cuenta_lo_auditado_y_lo_que_se_quedo_sin_mirar()
    {
        LiveSessionService live = Live(new QuotaAtUnitAgent(okUnits: 1));
        var vm = new SessionViewModel(live);

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, AllUnits), AllUnits);
        await Wait(live);

        vm.ShowFailure.Should().BeTrue("el banner dice por qué se paró");
        vm.ShowSummary.Should().BeTrue(
            "y debajo sigue el resumen: tirar la única prueba del trabajo pagado sería el mismo error");

        SummaryLine cut = live.Summary.Should().ContainSingle(l => l.Label.Contains("proveedor")).Subject;
        cut.Count.Should().Be(2, "las que se quedaron sin mirar");
        cut.IsWarning.Should().BeTrue();
        cut.Explanation.Should().Contain("AI credits");

        live.Summary.Should().Contain(l => l.Label == "Nuevos" && l.Count == 1,
            "el hallazgo de la unidad auditada cuenta igual: estaba pagado");
        live.StatusMessage.Should().Contain("cortada por el proveedor", Exactly.Once());
    }

    [Fact]
    public async Task El_crudo_se_puede_desplegar_y_empieza_plegado()
    {
        LiveSessionService live = Live(new QuotaAtUnitAgent(okUnits: 1));
        var vm = new SessionViewModel(live);

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, AllUnits), AllUnits);
        await Wait(live);

        vm.HasFailureDetail.Should().BeTrue();
        vm.IsFailureDetailExpanded.Should().BeFalse("plegado: un error largo no puede empujar la vista");
        vm.DetailToggleLabel.Should().Be("Ver detalle");

        vm.ToggleFailureDetailCommand.Execute(null);

        vm.IsFailureDetailExpanded.Should().BeTrue();
        vm.DetailToggleLabel.Should().Be("Ocultar detalle");
        vm.FailureDetail.Should().Contain("InvalidOperationException").And.Contain("monthly quota");
    }

    /// <summary>
    /// Y si el grifo se cierra ANTES de cerrar la primera unidad no hay nada que salvar: sube tal
    /// cual, sin sesión en el hub y sin informe. Es el comportamiento que F5.15 dejó probado.
    /// </summary>
    [Fact]
    public async Task Sin_ninguna_unidad_cubierta_no_se_registra_una_sesion_vacia()
    {
        LiveSessionService live = Live(new QuotaAtUnitAgent(okUnits: 0));

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, AllUnits), AllUnits);
        await Wait(live);

        live.HasFailed.Should().BeTrue();
        live.HasFinished.Should().BeFalse("no terminó: murió antes de cubrir nada");
        live.FailureMessage.Should().Contain("AI credits");
        _hub.Store.ListSessions("app").Should().BeEmpty("no hay trabajo que registrar");
    }
}
