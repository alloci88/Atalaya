using Atalaya.Agents;
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
/// F19 §3 — <b>el techo de llamadas por pasada</b>, y que un corte nunca es mudo.
/// <para>
/// El techo de tokens (Hito 1c) mide la consecuencia; éste mide la causa. Cada llamada reenvía el
/// prompt entero, así que las llamadas son el multiplicador del gasto: un agente que da vueltas se
/// nota en el contador de llamadas mucho antes de que los tokens lleguen a su tope.
/// </para>
/// <para>
/// Y con dos techos, <b>hay que decir cuál saltó</b>: «cortada por presupuesto» sin más obliga a
/// adivinar si el agente gastó mucho o dio muchas vueltas, que tienen remedios distintos (N-2).
/// </para>
/// </summary>
public sealed class CallCeilingTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly SettingsService _settings;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public CallCeilingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f19", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });

        string clone = Path.Combine(_root, "app");
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "A.cs"), "class A { void M() { } }");
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

    private void Thresholds(int maxCalls, long maxTokens = 300_000)
        => _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app",
            Name = "App",
            RepoUrl = "u",
            Stack = TechStack.DotNet,
            CurrentCycle = 1,
            Thresholds = { MaxCallsPerPass = maxCalls, MaxTokensPerUnit = maxTokens },
        });

    private Task<SessionResult> Run(IAuditorProvider agent)
        => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

    private AuditSession Written() => _hub.Store.ListSessions("app").Single();

    /// <summary>Un agente en bucle: da vueltas baratas y no cierra nunca la unidad.</summary>
    private sealed class LoopingAgent : IAuditorProvider
    {
        private readonly int _calls;
        private readonly long _tokensPerCall;

        public LoopingAgent(int calls, long tokensPerCall = 10)
        {
            _calls = calls;
            _tokensPerCall = tokensPerCall;
        }

        public string? ModelName => "en-bucle";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public async Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke("[bucle]\n");
            for (int i = 0; i < _calls; i++)
            {
                ct.ThrowIfCancellationRequested();
                UsageReported?.Invoke(new UsageSample(_tokensPerCall, 1, null, ModelName));
                await Task.Yield();
            }

            toolbox.UnitDone(request.UnitPath, "Revisados: M.");
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// El bucle se corta por LLAMADAS mucho antes de que los tokens lleguen a nada, y el motivo se
    /// escribe con esa palabra: quien lea el informe tiene que saber qué remedio buscar.
    /// </summary>
    [Fact]
    public async Task Un_agente_en_bucle_se_corta_por_llamadas_y_se_dice()
    {
        Thresholds(maxCalls: 4);

        await Run(new LoopingAgent(calls: 50));

        AuditSession session = Written();
        UnitVerdictRecord unit = session.Units.Single();

        unit.Verdict.Should().Be("presupuesto-superado");
        unit.Summary.Should().Contain("Cortada por presupuesto: 5/4 llamadas en una pasada");
        unit.Summary.Should().NotContain("tokens", "el techo que saltó fue el de llamadas, no el de tokens");

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("app", session.Id.ToString()));
        report.Should().Contain("llamadas en una pasada");
    }

    /// <summary>
    /// El techo de tokens sigue diciendo «tokens». Los dos existen y no se confunden: si el mismo
    /// texto valiera para los dos, el motivo dejaría de ser un dato.
    /// </summary>
    [Fact]
    public async Task El_techo_de_tokens_sigue_nombrandose_por_su_nombre()
    {
        Thresholds(maxCalls: 0, maxTokens: 1_000);

        await Run(new LoopingAgent(calls: 50, tokensPerCall: 900));

        Written().Units.Single().Summary.Should().Contain("tokens").And.NotContain("llamadas en una pasada");
    }

    /// <summary>
    /// <b>0 lo desactiva.</b> Es el interruptor de quien prefiera solo la red de los tokens, y
    /// tiene que apagar de verdad: un techo que no se puede quitar no es un ajuste.
    /// </summary>
    [Fact]
    public async Task Un_techo_de_cero_no_corta()
    {
        Thresholds(maxCalls: 0);

        await Run(new LoopingAgent(calls: 30));

        Written().Units.Single().Verdict.Should().Be("auditada");
    }

    /// <summary>
    /// Y una pasada sana no lo roza. El defecto por defecto es 12 y lo medido en F19 son 2 —tres
    /// con lectura de firmas—: un techo que cortara trabajo bueno sería peor que no tenerlo.
    /// </summary>
    [Fact]
    public async Task Una_pasada_normal_no_roza_el_techo_por_defecto()
    {
        Thresholds(maxCalls: new Thresholds().MaxCallsPerPass);

        await Run(new LoopingAgent(calls: 3));

        Written().Units.Single().Verdict.Should().Be("auditada");
        Written().UsageBreakdown.Single().Passes.Single().Calls.Should().Be(3);
    }
}

/// <summary>
/// F19 §§1–3 — <b>lo que el prompt le pide al auditor sobre los turnos</b>, y el techo del prefijo.
/// <para>
/// El ahorro de F19 no está en un algoritmo: está en que el auditor entregue todo lo que tiene en
/// un solo turno en vez de gastar una vuelta por cada cosa. Eso vive en el prompt, así que aquí es
/// donde se fija — y con él el guardarraíl del §3, que impide pagar las llamadas de menos con un
/// prompt de más.
/// </para>
/// </summary>
public class TurnEconomyPromptTests
{
    private static ComposedUnitPrompt Compose()
        => PromptComposer.Compose(
            "src/A.cs", "class A {}", PillarBrief.Parts(TechStack.DotNet), AuditMode.Lotes);

    /// <summary>
    /// La regla, con sus cuatro herramientas nombradas y el orden dentro del turno. Sin nombrarlas
    /// una a una, «agrupa» se lee como «agrupa los hallazgos», que ya se pedía y no era el problema.
    /// </summary>
    [Fact]
    public void El_prompt_pide_entregar_todo_en_un_solo_turno()
    {
        string p = Compose().Text;

        p.Should().Contain("ECONOMÍA DE TURNOS");
        p.Should().Contain("ENTREGA TODO EN UN SOLO TURNO");
        p.Should().Contain("submit_findings, add_locations y unit_done");
        p.Should().Contain("las cuatro en la misma vuelta");
        p.Should().Contain("última de las cuatro", "el orden dentro del turno importa");
        p.Should().Contain("Después de unit_done NO digas nada más");
    }

    /// <summary>
    /// Y NO contradice la cobertura, que es lo que no se toca: los turnos de razonamiento siguen
    /// siendo libres. Lo que se recorta son las vueltas de ENTREGA, no las de pensar.
    /// </summary>
    [Fact]
    public void La_economia_de_turnos_no_recorta_el_razonamiento()
    {
        string p = Compose().Text;

        p.Should().Contain("Tómate los turnos de RAZONAMIENTO que necesites");
        p.Should().Contain("TÓMATE LOS TURNOS QUE NECESITES", "la regla de cobertura de F4.1 sigue entera");
        p.Should().Contain("es preferible una auditoría profunda y cara que una barata e incompleta");
    }

    /// <summary>
    /// <b>El guardarraíl del §3.</b> Menos llamadas no puede significar prompts gigantes: el
    /// prefijo estable viaja en TODAS las llamadas de la sesión, así que cada token que se le añade
    /// se paga tantas veces como llamadas haya.
    /// <para>
    /// El techo es holgado a propósito —no es un presupuesto, es una alarma—: lo medido en F19 son
    /// 3.039 tokens y esto salta a partir de 3.500. Quien necesite pasar de ahí tiene que venir a
    /// cambiar el número y explicarlo, que es exactamente lo que se busca.
    /// </para>
    /// </summary>
    [Fact]
    public void El_prefijo_estable_no_puede_engordar_sin_que_salte_un_rojo()
    {
        PromptComposition c = Compose().Composition;

        c.Estable.Should().BeLessThan(3_500,
            "el prefijo se paga en cada llamada; si hace falta más sitio, se decide a conciencia");
        c.Estable.Should().BeGreaterThan(2_000, "y si se desplomara sería que se ha caído un bloque");
    }
}
