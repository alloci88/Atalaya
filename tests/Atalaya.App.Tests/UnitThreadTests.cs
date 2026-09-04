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
/// F25 — <b>el barrido es una conversación</b>: una sesión de proveedor por unidad, y cada pasada un
/// turno suyo.
/// <para>
/// Lo que estos tests fijan es lo que hace que el ahorro sea real y no de mentira: que el turno 1 sea
/// <b>byte a byte</b> el prompt que se mandaría por el camino de respaldo, y que los turnos 2..N no
/// reenvíen ni reglas, ni código, ni la lista de existentes — eso es el ahorro entero. Un hilo que
/// colara cualquiera de las tres cosas costaría lo mismo que no tenerlo.
/// </para>
/// <para>
/// Y lo otro que fijan es que una unidad NO se pierde cuando el hilo se rompe: los tres respaldos
/// —el proveedor no puede continuar, la pasada se corta, el contexto llega al techo— tienen aquí su
/// test, y en los tres la pasada se sirve y el barrido sigue.
/// </para>
/// <para>
/// Una que no es de forma sino de fondo: la regla de parada y el tope <b>no se tocan</b>. Un turno es
/// una pasada y se juzga con lo mismo (D-755, D-812).
/// </para>
/// </summary>
public sealed class UnitThreadTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly SettingsService _settings;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public UnitThreadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f25", Guid.NewGuid().ToString("N"));
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

    private Task<SessionResult> Run(IAuditorProvider agent)
        => Coordinator(agent).RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

    private SessionCoordinator Coordinator(IAuditorProvider agent)
        => new(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings);

    private UnitUsageBreakdown Breakdown()
        => _hub.Store.ListSessions("app").Single().UsageBreakdown.Single();

    /// <summary>
    /// <b>La condición de todo lo demás.</b> El turno 1 del hilo es el prompt de siempre sin un byte
    /// de diferencia: si no lo fuera, el barrido habría cambiado de contenido y no solo de forma de
    /// viajar. Se compara carácter a carácter contra lo que recibe un proveedor que no sabe hilar.
    /// </summary>
    [Fact]
    public async Task El_turno_1_es_byte_a_byte_el_prompt_de_siempre()
    {
        var respaldo = new RecordingAgent();
        await Run(respaldo);

        var hilo = new ThreadingAgent();
        await Run(hilo);

        hilo.Turns.Should().NotBeEmpty();
        respaldo.Turns.Should().NotBeEmpty();
        hilo.Turns[0].Should().Be(respaldo.Turns[0]);
    }

    /// <summary>
    /// <b>El ahorro entero, y lo único que lo produce.</b> A partir del segundo turno no viaja ni una
    /// palabra de las reglas, ni una línea del código de la unidad, ni un ULID de la lista de
    /// existentes: todo eso sigue delante del modelo porque es la misma conversación.
    /// </summary>
    [Fact]
    public async Task Los_turnos_siguientes_no_reenvian_reglas_ni_codigo_ni_lista()
    {
        var agent = new ThreadingAgent();
        await Run(agent);

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
    /// <b>Una versión del texto de continuación, y fija.</b> Un texto que se compone al vuelo es una
    /// invitación a retocarlo hasta que el número salga bien, y lo que se midió fue éste.
    /// </summary>
    [Fact]
    public async Task El_texto_de_continuacion_es_uno_y_el_mismo_en_todos_los_turnos()
    {
        var agent = new ThreadingAgent();
        await Run(agent);

        agent.Turns.Skip(1).Distinct(StringComparer.Ordinal).Should().HaveCount(1);
        PromptComposer.ContinuationTurn.Should().NotContain("{", "no es una plantilla");
    }

    /// <summary>
    /// <b>Y pide lo mismo que una pasada del camino de respaldo.</b> Si dejara de pedir cualquiera de
    /// las tres entregas, el hilo estaría comprando su ahorro con cobertura por la puerta de atrás.
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
    /// <b>Un hilo por unidad, no uno por pasada.</b> Es lo que produce el ahorro: si se abriera uno
    /// por pasada, el prefijo se volvería a escribir igual. Y no hay nada que encender: el hilo es el
    /// camino, no una opción.
    /// </summary>
    [Fact]
    public async Task Se_abre_un_solo_hilo_para_toda_la_unidad_y_se_cierra_al_acabar()
    {
        var agent = new ThreadingAgent();
        await Run(agent);

        agent.ThreadsOpened.Should().Be(1);
        agent.ThreadsDisposed.Should().Be(1, "se abre una conversación y se cierra una vez");
        agent.UnitCalls.Should().Be(0, "con hilo no se pasa por AuditUnitAsync");
    }

    /// <summary>
    /// <b>La regla de parada y el tope no se tocan.</b> Un turno es una pasada: dos secas seguidas
    /// terminan el barrido igual por los dos caminos, y con el mismo número de pasadas.
    /// </summary>
    [Fact]
    public async Task La_regla_de_parada_manda_igual_por_los_dos_caminos()
    {
        var respaldo = new RecordingAgent();
        await Run(respaldo);

        var hilo = new ThreadingAgent();
        await Run(hilo);

        respaldo.Turns.Should().HaveCount(2, "dos secas seguidas terminan (F12 §E)");
        hilo.Turns.Should().HaveCount(respaldo.Turns.Count, "un turno es una pasada");
        _settings.Current.MaxPassesPerUnit.Should().Be(4, "y el tope tampoco se ha tocado");
    }

    /// <summary>
    /// <b>Un proveedor que no sabe hilar no se simula.</b> La unidad se barre con una petición por
    /// pasada —el camino de respaldo, que es el barrido de antes— y queda dicho, en la sesión y por
    /// evento: un barrido que costara el triple sin que nada lo nombrara sería indistinguible de uno
    /// caro por cualquier otro motivo.
    /// </summary>
    [Fact]
    public async Task Un_proveedor_que_no_sabe_hilar_lo_dice_en_vez_de_fingirlo()
    {
        var avisos = new List<string>();
        SessionCoordinator coordinator = Coordinator(new FakeCopilotAgent());
        coordinator.ThreadUnavailable += avisos.Add;

        await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        avisos.Should().NotBeEmpty();
        _hub.Store.ListSessions("app").Single().Notes
            .Should().Contain(n => n.Contains("una petición por pasada"));
        Breakdown().ThreadTurns.Should().Be(0, "no hubo ningún turno: no hubo hilo");
    }

    /// <summary>
    /// <b>El censo dice lo que SALIÓ, no lo que se compuso.</b> Con el hilo, la pasada 2 manda ~150
    /// tokens y no el prompt entero; un informe que declarara 30.000 donde viajaron 150 no valdría
    /// para nada.
    /// </summary>
    [Fact]
    public async Task La_composicion_apuntada_es_la_del_texto_que_de_verdad_viajo()
    {
        await Run(new ThreadingAgent());

        List<PassUsage> passes = Breakdown().Passes;
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
    /// consumo se perdería y la sesión declararía de menos — el fallo que costó descubrir D-865.
    /// </summary>
    [Fact]
    public async Task El_consumo_que_llega_al_cerrar_el_hilo_entra_en_el_desglose_de_la_unidad()
    {
        await Run(new ThreadingAgent { UsageOnClose = 4_321 });

        Breakdown().CacheReadTokens.Should().BeGreaterOrEqualTo(4_321,
            "el cuadre del final se cuenta antes de cerrar el desglose de la unidad");
    }

    /// <summary>
    /// <b>Primer respaldo: el proveedor no puede continuar la sesión.</b> La pasada NO se pierde: se
    /// rehace con una petición nueva —el prompt recompuesto y la lista de existentes, que es el
    /// barrido de antes—, y el censo apunta lo que de verdad viajó, que es el prompt entero.
    /// </summary>
    [Fact]
    public async Task Una_reanudacion_fallida_rehace_la_pasada_con_una_peticion_nueva()
    {
        var agent = new ThreadingAgent { NewPerPass = true, BreakOnTurn = 2 };
        await Run(agent);

        agent.UnitCalls.Should().Be(1, "la pasada rota se sirvió con una petición nueva");
        agent.Turns.Should().HaveCount(4, "y el barrido siguió hasta el tope, sin perder ninguna");

        UnitUsageBreakdown b = Breakdown();
        b.ThreadRestarts.Should().Be(1);
        b.ThreadRestartReasons.Should().ContainSingle()
            .Which.Should().Contain("no se pudo continuar");
        b.Passes[1].Composition!.Unidad.Should().BeGreaterThan(0,
            "en esa pasada viajó el prompt entero, no la continuación");

        _hub.Store.ListSessions("app").Single().Notes
            .Should().Contain(n => n.Contains("se ha hecho con una petición nueva"));
    }

    /// <summary>
    /// <b>Segundo respaldo: la pasada se cortó.</b> Cortar mata la conversación, así que el turno
    /// cortado cuenta como pasada —el auditor ya había entregado su cierre— y la pasada siguiente
    /// abre un hilo NUEVO con el prompt recompuesto. Un hilo cortado no se reanuda nunca.
    /// </summary>
    [Fact]
    public async Task Un_hilo_cortado_no_se_reanuda_y_la_pasada_siguiente_abre_otro()
    {
        var agent = new ThreadingAgent { NewPerPass = true, CloseAfterTurn = 1 };
        await Run(agent);

        agent.Turns.Should().HaveCount(4, "cada pasada se sirvió");
        agent.UnitCalls.Should().Be(0, "y todas por un hilo, aunque no por el mismo");
        agent.ThreadsOpened.Should().Be(4, "un hilo nuevo por pasada, porque cada uno se cortó");

        UnitUsageBreakdown b = Breakdown();
        b.ThreadRestarts.Should().Be(3);
        b.ThreadRestartReasons.Should().OnlyContain(r => r.Contains("se cortó"));
        agent.Turns.Should().OnlyContain(t => t != PromptComposer.ContinuationTurn,
            "sin hilo que herede, cada pasada vuelve a mandar el prompt entero");
    }

    /// <summary>
    /// <b>Tercer respaldo: el techo de contexto.</b> Una conversación que desborda la ventana del
    /// modelo no falla con elegancia: empieza a perder lo de antes sin decirlo, y todo el argumento
    /// del hilo es que el modelo tiene delante lo que ya se dijo. Al alcanzarlo, el turno siguiente
    /// abre hilo nuevo, y el motivo lleva el número escrito.
    /// </summary>
    [Fact]
    public async Task Al_llegar_al_techo_de_contexto_el_turno_siguiente_abre_hilo_nuevo()
    {
        var agent = new ThreadingAgent
        {
            NewPerPass = true,
            CacheReadPerTurn = UnitThreadLimits.TechoContexto,
        };
        await Run(agent);

        UnitUsageBreakdown b = Breakdown();
        b.ThreadRestarts.Should().Be(3, "cada pasada llenó el hilo y la siguiente abrió otro");
        b.ThreadRestartReasons.Should().OnlyContain(
            r => r.Contains("techo de contexto") && r.Contains(UnitThreadLimits.TechoContexto.ToString()));
    }

    /// <summary>
    /// <b>Y un hilo que aguanta no se reinicia.</b> Es la otra mitad del test de arriba: sin ella,
    /// un techo puesto a cero pasaría por bueno.
    /// </summary>
    [Fact]
    public async Task Por_debajo_del_techo_el_hilo_sigue_siendo_el_mismo()
    {
        var agent = new ThreadingAgent
        {
            NewPerPass = true,
            CacheReadPerTurn = UnitThreadLimits.TechoContexto - 100,
        };
        await Run(agent);

        UnitUsageBreakdown b = Breakdown();
        b.ThreadRestarts.Should().Be(0);
        b.ThreadTurns.Should().Be(4, "las cuatro pasadas fueron turnos del mismo hilo");
    }

    /// <summary>
    /// Lo que el anexo necesita para poder decir «hilo: n turnos · m reinicios»: los dos números
    /// quedan escritos en la sesión, y sobreviven a ir y volver del hub.
    /// </summary>
    [Fact]
    public async Task El_desglose_de_la_unidad_cuenta_los_turnos_del_hilo()
    {
        await Run(new ThreadingAgent());

        UnitUsageBreakdown b = Breakdown();
        b.ThreadTurns.Should().Be(2, "dos pasadas, dos turnos");
        b.ThreadRestarts.Should().Be(0);
        b.ThreadRestartReasons.Should().BeEmpty();
    }

    /// <summary>
    /// <b>El ULID vuelve, y sirve para lo que existe</b> (F25 §4, D-916). El auditor crea un
    /// hallazgo y, <b>en el mismo turno</b>, le añade una ubicación con el id que le acaba de
    /// devolver <c>submit_finding</c>.
    /// <para>
    /// Esa rama de <c>add_locations</c> lleva puesta desde F4.1 —«un ULID que hayas reportado en
    /// esta unidad»— y hasta hoy era inalcanzable dentro de la pasada que lo creaba: el modelo no
    /// puede nombrar un identificador que nadie le ha dicho. Se ejercita por el catálogo de
    /// herramientas de verdad y sobre el toolbox de verdad, porque lo que se está comprobando es
    /// justo la costura entre los dos.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_auditor_extiende_en_el_mismo_turno_el_hallazgo_que_acaba_de_crear()
    {
        var agent = new ExtendingAgent();
        await Run(agent);

        agent.Accepted.Should().BeTrue("el ULID devuelto tiene que valer para add_locations");
        agent.Added.Should().Be(1);

        Finding created = _hub.Store.ListFindings("app").Single();
        created.Locations.Should().HaveCount(2, "un defecto sistémico es UN hallazgo con N ubicaciones");
    }

    /// <summary>
    /// Un auditor que habla por el catálogo de herramientas de verdad: crea un hallazgo, se queda
    /// con el id que le contestan y lo extiende sin salir del turno.
    /// </summary>
    private sealed class ExtendingAgent : IAuditorProvider
    {
        private bool _done;

        public bool Accepted { get; private set; }

        public int Added { get; private set; }

        public string? ModelName => "extensor";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(10, 10, null, ModelName));

            IReadOnlyList<Atalaya.ClaudeCode.McpTool> tools =
                Atalaya.ClaudeCode.AuditorTools.ForAudit(toolbox);

            if (!_done)
            {
                _done = true;
                string? id = Call(tools, "submit_finding", """
                    {"ruleId":"errores.recursos.no-liberado","pillar":"errores","severity":"alta",
                     "title":"el mismo defecto en dos sitios","description":"d","impact":"i",
                     "recommendation":"r","symbol":"Metodo",
                     "locations":[{"path":"A.cs","line":1}]}
                    """).GetProperty("Id").GetString();

                id.Should().NotBeNullOrWhiteSpace("submit_finding devuelve el ULID de lo que crea");

                System.Text.Json.JsonElement extended = Call(tools, "add_locations",
                    $$"""
                      {"findingId":"{{id}}","locations":[{"path":"A.cs","line":2}]}
                      """);

                Accepted = extended.GetProperty("Accepted").GetBoolean();
                Added = extended.GetProperty("Added").GetInt32();
            }

            toolbox.UnitDone(request.UnitPath, "cubierta");
            return Task.CompletedTask;
        }

        private static System.Text.Json.JsonElement Call(
            IReadOnlyList<Atalaya.ClaudeCode.McpTool> tools, string name, string args)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(args);
            object? result = tools.Single(t => t.Name == name).Handler(doc.RootElement);
            return System.Text.Json.JsonSerializer.SerializeToElement(result);
        }
    }

    /// <summary>
    /// Un auditor que apunta lo que se le manda. <b>No sabe hilar</b>: es el camino de respaldo, una
    /// petición por pasada, y por eso sirve de línea contra la que comparar.
    /// </summary>
    private class RecordingAgent : IAuditorProvider
    {
        private int _reported;

        public List<string> Turns { get; } = new();

        public int UnitCalls { get; private set; }

        public int ThreadsOpened { get; protected set; }

        public int ThreadsDisposed { get; internal set; }

        /// <summary>Aporta algo en cada pasada, para que el barrido no converja y llegue al tope.</summary>
        public bool NewPerPass { get; init; }

        /// <summary>El turno en el que la conversación deja de poder continuar. 0 = nunca.</summary>
        public int BreakOnTurn { get; init; }

        /// <summary>Tras cuántos turnos el hilo queda cerrado —lo que hace el corte—. 0 = nunca.</summary>
        public int CloseAfterTurn { get; init; }

        /// <summary>Lo que cada turno declara haber leído de caché: con qué se llena el hilo.</summary>
        public long CacheReadPerTurn { get; init; }

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

        internal void Report(UsageSample sample) => UsageReported?.Invoke(sample);

        protected void Deliver(string prompt, IAuditToolbox toolbox)
        {
            Turns.Add(prompt);
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(
                10, 10, null, ModelName, CacheReadTokens: CacheReadPerTurn));

            if (NewPerPass)
            {
                _reported++;
                toolbox.SubmitFinding(new SubmitFindingArgs(
                    "errores.recursos.no-liberado", "errores", "alta", $"defecto {_reported}",
                    "d", "i", "r", new[] { new SubmitLocation("A.cs", _reported, null) }, $"M{_reported}"));
            }

            toolbox.UnitDone("A.cs", "nada nuevo");
        }
    }

    /// <summary>El mismo, pero hilando: cada pasada es un turno de la misma conversación.</summary>
    private sealed class ThreadingAgent : RecordingAgent, IThreadedAuditor
    {
        public Task<IUnitThread> OpenUnitThreadAsync(IAuditToolbox toolbox, CancellationToken ct)
        {
            ThreadsOpened++;
            return Task.FromResult<IUnitThread>(new Thread(this, toolbox));
        }

        private sealed class Thread : IUnitThread
        {
            private readonly ThreadingAgent _agent;
            private readonly IAuditToolbox _toolbox;
            private int _turns;
            private bool _disposed;

            public Thread(ThreadingAgent agent, IAuditToolbox toolbox)
            {
                _agent = agent;
                _toolbox = toolbox;
            }

            public bool Closed { get; private set; }

            public Task TurnAsync(string prompt, CancellationToken ct)
            {
                _turns++;
                if (_agent.BreakOnTurn > 0 && _agent.Turns.Count + 1 == _agent.BreakOnTurn)
                {
                    Closed = true;
                    throw new UnitThreadBrokenException("la sesión ya no existe");
                }

                _agent.Deliver(prompt, _toolbox);
                if (_agent.CloseAfterTurn > 0 && _turns >= _agent.CloseAfterTurn)
                {
                    Closed = true;
                }

                return Task.CompletedTask;
            }

            /// <summary>
            /// Idempotente, como el hilo de verdad: el coordinador lo cierra a mano en cuanto termina
            /// el barrido —para que el cuadre del final entre en las cuentas de la unidad— y la red
            /// vuelve a pedirlo al salir del ámbito. Se cierra UNA vez.
            /// </summary>
            public ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return ValueTask.CompletedTask;
                }

                _disposed = true;
                Closed = true;
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
