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
/// M2 — <b>el brazo `--hilo`: la unidad como una conversación</b>.
/// <para>
/// Lo que estos tests fijan es lo que hace que la medida SEA una medida: que con la palanca apagada
/// no cambie absolutamente nada, que el turno 1 sea <b>byte a byte</b> el prompt de producción, y
/// que los turnos 2..N no reenvíen ni reglas, ni código, ni la lista de existentes — que es el
/// ahorro entero. Un brazo que colara cualquiera de las tres cosas mediría el otro brazo.
/// </para>
/// <para>
/// Y una que no es de forma sino de fondo: la regla de parada y el tope <b>no se tocan</b>. Un turno
/// es una pasada y se juzga con lo mismo (D-755, D-812).
/// </para>
/// </summary>
public sealed class ThreadArmTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly SettingsService _settings;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public ThreadArmTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-m2", Guid.NewGuid().ToString("N"));
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

    private Task<SessionResult> Run(IAuditorProvider agent, bool hilo)
        => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
        {
            Hilo = hilo,
        }.RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

    /// <summary>
    /// <b>La condición de la medida.</b> El turno 1 del hilo tiene que ser el prompt de producción
    /// sin un byte de diferencia — si no, los dos brazos no arrancan del mismo sitio y la
    /// comparación no dice nada. Se compara carácter a carácter contra lo que el brazo de
    /// producción manda en su pasada 1.
    /// </summary>
    [Fact]
    public async Task El_turno_1_es_byte_a_byte_el_prompt_de_produccion()
    {
        var produccion = new RecordingAgent();
        await Run(produccion, hilo: false);

        var hilo = new RecordingAgent();
        await Run(hilo, hilo: true);

        hilo.Turns.Should().NotBeEmpty();
        produccion.Turns.Should().NotBeEmpty();
        hilo.Turns[0].Should().Be(produccion.Turns[0]);
    }

    /// <summary>
    /// <b>El ahorro entero, y lo único que lo produce.</b> A partir del segundo turno no viaja ni
    /// una palabra de las reglas, ni una línea del código de la unidad, ni un ULID de la lista de
    /// existentes: todo eso sigue delante del modelo porque es la misma conversación.
    /// </summary>
    [Fact]
    public async Task Los_turnos_siguientes_no_reenvian_reglas_ni_codigo_ni_lista()
    {
        var agent = new RecordingAgent();
        await Run(agent, hilo: true);

        agent.Turns.Should().HaveCountGreaterThan(1, "el barrido tiene que dar más de una pasada");

        foreach (string turn in agent.Turns.Skip(1))
        {
            turn.Should().Be(PromptComposer.ContinuationTurn);

            // Las tres cosas que NO pueden viajar, nombradas una a una para que el rojo diga cuál.
            turn.Should().NotContain("MÉTODO DE BARRIDO", "las reglas ya están en la conversación");
            turn.Should().NotContain("CONTENIDO ÍNTEGRO DE LA UNIDAD", "el código ya está");
            turn.Should().NotContain("HALLAZGOS YA EXISTENTES", "la lista ya está");
            turn.Should().NotContain("class A", "ni un byte del código de la unidad");
        }
    }

    /// <summary>
    /// <b>Una versión del texto de continuación, y fija.</b> El anti-objetivo de la medida es
    /// retocarlo hasta que el brazo gane; un texto que se compone al vuelo es exactamente eso con
    /// otro nombre. Es una constante y no depende de la pasada, ni de la unidad, ni de la hora.
    /// </summary>
    [Fact]
    public async Task El_texto_de_continuacion_es_uno_y_el_mismo_en_todos_los_turnos()
    {
        var agent = new RecordingAgent();
        await Run(agent, hilo: true);

        agent.Turns.Skip(1).Distinct(StringComparer.Ordinal).Should().HaveCount(1);
        PromptComposer.ContinuationTurn.Should().NotContain("{", "no es una plantilla");
    }

    /// <summary>
    /// <b>Y pide lo mismo que una pasada de hoy.</b> No es una redacción cualquiera: si dejara de
    /// pedir cualquiera de las tres entregas, el brazo estaría comprando su ahorro con cobertura y
    /// la comparación sería tramposa.
    /// </summary>
    [Fact]
    public void La_continuacion_pide_reconciliar_reportar_y_cerrar()
    {
        string t = PromptComposer.ContinuationTurn;

        t.Should().Contain("report_verdicts");
        t.Should().Contain("submit_findings");
        t.Should().Contain("unit_done");
        t.Should().Contain("NO está cerrada", "la lección de D-874: cierra la PASADA, no la unidad");
    }

    /// <summary>
    /// <b>Apagada, no existe.</b> El proveedor recibe sus pasadas por <c>AuditUnitAsync</c> como
    /// siempre y nadie abre ningún hilo. Es la misma exigencia que M1 le puso a su palanca.
    /// </summary>
    [Fact]
    public async Task Apagada_el_hilo_no_se_abre_y_cada_pasada_es_una_peticion()
    {
        var agent = new RecordingAgent();
        await Run(agent, hilo: false);

        agent.ThreadsOpened.Should().Be(0);
        agent.UnitCalls.Should().Be(agent.Turns.Count);
    }

    /// <summary>
    /// <b>Un hilo por unidad, no uno por pasada.</b> Es lo que se está midiendo: si se abriera uno
    /// por pasada, el prefijo se volvería a escribir igual y el brazo no ahorraría nada.
    /// </summary>
    [Fact]
    public async Task Se_abre_un_solo_hilo_para_toda_la_unidad_y_se_cierra_al_acabar()
    {
        var agent = new RecordingAgent();
        await Run(agent, hilo: true);

        agent.ThreadsOpened.Should().Be(1);
        agent.ThreadsDisposed.Should().Be(1, "se abre una conversación y se cierra una vez");
        agent.UnitCalls.Should().Be(0, "con hilo no se pasa por AuditUnitAsync");
    }

    /// <summary>
    /// <b>La regla de parada y el tope no se tocan.</b> Un turno es una pasada: dos secas seguidas
    /// terminan el barrido igual que sin hilo, y con el mismo número de pasadas.
    /// </summary>
    [Fact]
    public async Task La_regla_de_parada_manda_igual_en_los_dos_brazos()
    {
        var produccion = new RecordingAgent();
        await Run(produccion, hilo: false);

        var conHilo = new RecordingAgent();
        await Run(conHilo, hilo: true);

        produccion.Turns.Should().HaveCount(2, "dos secas seguidas terminan (F12 §E)");
        conHilo.Turns.Should().HaveCount(produccion.Turns.Count, "un turno es una pasada");
        _settings.Current.MaxPassesPerUnit.Should().Be(4, "y el tope tampoco se ha tocado");
    }

    /// <summary>
    /// <b>Un proveedor que no sabe hilar no se simula.</b> La unidad corre como siempre y queda
    /// dicho, en la sesión y por evento: una tanda apuntada como «hilo» que no lo fue mediría otra
    /// cosa, y la tabla no lo delataría.
    /// </summary>
    [Fact]
    public async Task Un_proveedor_que_no_sabe_hilar_lo_dice_en_vez_de_fingirlo()
    {
        var avisos = new List<string>();
        var coordinator = new SessionCoordinator(
            _hub, _ingestion, _reconciliation, _machines, _ulids, new FakeCopilotAgent(), _settings)
        {
            Hilo = true,
        };
        coordinator.ThreadUnavailable += avisos.Add;

        await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        avisos.Should().NotBeEmpty();
        _hub.Store.ListSessions("app").Single().Notes
            .Should().Contain(n => n.Contains("no lo implementa"));
    }

    /// <summary>
    /// <b>El censo dice lo que SALIÓ, no lo que se compuso.</b> Con el hilo, la pasada 2 manda ~150
    /// tokens y no el prompt entero; un informe de una medida que declarara 30.000 donde viajaron
    /// 150 no valdría para nada.
    /// </summary>
    [Fact]
    public async Task La_composicion_apuntada_es_la_del_texto_que_de_verdad_viajo()
    {
        await Run(new RecordingAgent(), hilo: true);

        List<PassUsage> passes = _hub.Store.ListSessions("app").Single().UsageBreakdown.Single().Passes;
        passes.Should().HaveCountGreaterThan(1);

        int primera = passes[0].Composition!.Total;
        int siguiente = passes[1].Composition!.Total;

        siguiente.Should().BeLessThan(primera / 4, "la continuación es una fracción del prompt");
        passes[1].Composition!.Unidad.Should().Be(0, "el código no viaja en un turno de continuación");
        passes[1].Composition!.Existentes.Should().Be(0, "ni la lista de existentes");
    }

    /// <summary>
    /// <b>Lo que el proveedor declara AL CERRAR entra en las cuentas de la unidad.</b> El evento
    /// final del CLI es el único sitio donde aparece lo que gastó por su cuenta (D-879), y llega
    /// cuando la conversación se cierra. Si el hilo se cerrara después de sellar el desglose, ese
    /// consumo se perdería y la medida declararía de menos — que es exactamente el fallo que D-865
    /// costó descubrir, por otro camino.
    /// </summary>
    [Fact]
    public async Task El_consumo_que_llega_al_cerrar_el_hilo_entra_en_el_desglose_de_la_unidad()
    {
        var agent = new RecordingAgent { UsageOnClose = 4_321 };
        await Run(agent, hilo: true);

        UnitUsageBreakdown breakdown = _hub.Store.ListSessions("app").Single().UsageBreakdown.Single();

        breakdown.CacheReadTokens.Should().BeGreaterOrEqualTo(4_321,
            "el cuadre del final se cuenta antes de cerrar el desglose de la unidad");
    }

    /// <summary>
    /// Un auditor que apunta lo que se le manda y cierra la unidad sin reportar nada, y que además
    /// sabe hilar. Cierra en seco para que el barrido converja por la regla de siempre.
    /// </summary>
    private sealed class RecordingAgent : IAuditorProvider, IThreadedAuditor
    {
        public List<string> Turns { get; } = new();

        public int UnitCalls { get; private set; }

        public int ThreadsOpened { get; private set; }

        public int ThreadsDisposed { get; private set; }

        /// <summary>Lo que el proveedor declara al cerrar la conversación, como hace el CLI.</summary>
        public long UsageOnClose { get; init; }

        public string? ModelName => "grabadora";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            UnitCalls++;
            Deliver(request.Prompt, toolbox);
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IUnitThread> OpenUnitThreadAsync(IAuditToolbox toolbox, CancellationToken ct)
        {
            ThreadsOpened++;
            return Task.FromResult<IUnitThread>(new Thread(this, toolbox));
        }

        internal void Report(UsageSample sample) => UsageReported?.Invoke(sample);

        private void Deliver(string prompt, IAuditToolbox toolbox)
        {
            Turns.Add(prompt);
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(10, 10, null, ModelName));
            toolbox.UnitDone("A.cs", "nada nuevo");
        }

        private sealed class Thread : IUnitThread
        {
            private readonly RecordingAgent _agent;
            private readonly IAuditToolbox _toolbox;
            private bool _closed;

            public Thread(RecordingAgent agent, IAuditToolbox toolbox)
            {
                _agent = agent;
                _toolbox = toolbox;
            }

            public Task TurnAsync(string prompt, CancellationToken ct)
            {
                _agent.Deliver(prompt, _toolbox);
                return Task.CompletedTask;
            }

            /// <summary>
            /// Idempotente, como el hilo de verdad: el coordinador lo cierra a mano en cuanto
            /// termina el barrido —para que el cuadre del final entre en las cuentas de la unidad—
            /// y el `await using` vuelve a pedirlo al salir del ámbito. Se cierra UNA vez.
            /// </summary>
            public ValueTask DisposeAsync()
            {
                if (_closed)
                {
                    return ValueTask.CompletedTask;
                }

                _closed = true;
                _agent.ThreadsDisposed++;
                if (_agent.UsageOnClose > 0)
                {
                    // El cuadre del final: `calls: 0`, porque no es una llamada nueva.
                    _agent.Report(new UsageSample(
                        0, 0, null, _agent.ModelName,
                        CacheReadTokens: _agent.UsageOnClose, Calls: 0, Reconciliation: true));
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
