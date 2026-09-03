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
/// F24 §2 — <b>la puerta de las variantes</b>: reportar dos veces el mismo defecto no es gratis ni
/// silencioso.
/// <para>
/// <b>El problema medido.</b> En el informe de referencia (AtalayaBanco, 2026-09-03) hay 25
/// hallazgos y unos 20 defectos: cinco pares son el mismo defecto dicho dos veces en pasadas
/// distintas. No es fallo de la reconciliación —el modelo marcaba «presente» todo lo que ya había
/// reportado (D-087)— ni fragmentación por miembro —eso lo cerró <c>add_locations</c> (D-090,
/// D-091)—. Es un caso que ninguna de las dos cubría: el modelo reconoce el hallazgo y lo vuelve a
/// reportar cortado por otro sitio.
/// </para>
/// <para>
/// <b>Y no era solo ruido de lectura.</b> Una variante entra como «nuevo», y un nuevo impide que la
/// pasada sea seca (D-087, D-092): cada una mantenía vivo el barrido y pagaba una pasada entera.
/// ClienteRemoto llegó al tope de 6.
/// </para>
/// <para>
/// <b>Lo que la aplicación NO hace, y es el punto.</b> No fusiona nada y no decide que dos
/// hallazgos sean el mismo: la identidad la decide el auditor (D-077). Lo que hace es negarse a
/// aceptarlo a la primera y obligar a que lo diga. Un reintento justificado entra SIEMPRE.
/// </para>
/// </summary>
public sealed class VariantGateTests : IDisposable
{
    private const string Unit = "ClienteRemoto.cs";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly SettingsService _settings;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public VariantGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f24", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);

        // Un fichero con líneas de sobra: el anclaje corrige contra el clon, y sin cuerpo las
        // líneas 23 y 33 no existirían.
        File.WriteAllText(Path.Combine(_clone, Unit), string.Join('\n', Enumerable.Repeat("// linea", 60)));
        _machines.SetClonePath("app", _clone);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Theme = AuditTheme.General,
            Units = { new InventoryUnit { Path = Unit, Module = "M", State = UnitState.Pendiente } },
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

    /// <summary>El par positivo del caso real: la mutación de <c>DefaultRequestHeaders</c>, línea 23.</summary>
    private static SubmitFindingArgs Race(string title, int line = 23, string symbol = "EnviarParteAsync")
        => new("errores.concurrencia.race", "errores", "alta", title, "desc", "impacto", "reco",
            new[] { new SubmitLocation(Unit, line, null) }, symbol);

    private Task<SessionResult> Run(IAuditorProvider agent)
        => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { Unit }), CancellationToken.None);

    private AuditSession Session() => _hub.Store.ListSessions("app").Single();

    private string Report(SessionResult r)
        => File.ReadAllText(_hub.HubPaths.ReportFile("app", r.SessionId.ToString()));

    // ---------------------------------------------------------------- el rechazo

    /// <summary>
    /// El par positivo: dos redacciones del mismo defecto, misma regla, mismo símbolo, misma línea.
    /// La segunda no entra, y el error <b>nombra el hallazgo</b> — como los ULID desconocidos de
    /// D-077. Un rechazo que no dice contra qué chocó obliga a adivinar.
    /// </summary>
    [Fact]
    public async Task Un_par_positivo_se_rechaza_con_error_tipado_que_nombra_el_hallazgo()
    {
        string? error = null;
        var agent = new ScriptedAgent((toolbox, _) =>
        {
            toolbox.SubmitFindings(new[] { Race("Mutación de DefaultRequestHeaders") });
            SubmitFindingsResult second = toolbox.SubmitFindings(new[]
            {
                Race("Mutación no atómica de Authorization bajo concurrencia"),
            });
            error = second.Results[0].Error;
        });

        SessionResult result = await Run(agent);

        result.Counters.New.Should().Be(1, "la variante no llegó a guardarse");
        result.Counters.VariantsRejected.Should().Be(1);
        _hub.Store.ListFindings("app").Should().ContainSingle();

        Finding first = _hub.Store.ListFindings("app").Single();
        error.Should().NotBeNull();
        error.Should().Contain(first.DisplayId ?? first.Id.ToString(), "el rechazo nombra contra qué chocó");
        error.Should().Contain("add_locations").And.Contain("distinctFrom");
    }

    /// <summary>
    /// <b>El motivo del arreglo, y no un efecto lateral</b>: la pasada cuya única producción fue una
    /// variante rebotada queda SECA. Antes, esa variante entraba como «nuevo» y el barrido seguía
    /// vivo pagando otra pasada entera.
    /// </summary>
    [Fact]
    public async Task Una_pasada_cuya_unica_produccion_fue_una_variante_queda_seca()
    {
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 4;
        _settings.Save(s);

        int pass = 0;
        var agent = new ScriptedAgent((toolbox, request) =>
        {
            pass++;
            toolbox.ReportVerdicts(request.Existing
                .Select(e => new VerdictArgs(e.FindingId, "presente", "sigue"))
                .ToArray());
            if (pass == 1)
            {
                toolbox.SubmitFindings(new[] { Race("Mutación de DefaultRequestHeaders") });
            }
            else
            {
                // Lo que hacía el modelo del caso de referencia cuando ya no quedaba nada.
                toolbox.SubmitFindings(new[] { Race($"Race en Authorization, dicho de la forma {pass}") });
            }
        });

        SessionResult result = await Run(agent);

        List<UnitPassRecord> passes = Session().Units.Single().Passes!;
        passes.Should().HaveCount(3, "dos secas seguidas cierran el barrido (D-755) y llegan en la 2 y la 3");
        passes[0].Dry.Should().BeFalse();
        passes[1].Dry.Should().BeTrue("una variante rebotada no es aportación");
        passes[2].Dry.Should().BeTrue();
        result.Counters.VariantsRejected.Should().Be(2);
        result.Counters.New.Should().Be(1);
    }

    // ---------------------------------------------------------------- el reintento

    /// <summary>
    /// Reenviado con <c>distinctFrom</c> y motivo: entra. Queda registrado como insistido —en el
    /// historial de la ficha, que sobrevive a la sesión, y en las notas— y el informe lo marca como
    /// posible duplicado (§5 de F23). La app no ha decidido nada: ha hecho que lo diga el auditor.
    /// </summary>
    [Fact]
    public async Task El_reintento_con_distinctFrom_entra_queda_registrado_y_se_marca()
    {
        var agent = new ScriptedAgent((toolbox, _) =>
        {
            toolbox.SubmitFindings(new[] { Race("Mutación de DefaultRequestHeaders") });
            SubmitFindingsResult rejected = toolbox.SubmitFindings(new[] { Race("Race en Authorization") });
            rejected.Results[0].Accepted.Should().BeFalse();

            string twin = ExtractUlid(rejected.Results[0].Error!);
            SubmitFindingsResult retry = toolbox.SubmitFindings(new[]
            {
                Race("Race en Authorization") with
                {
                    DistinctFrom = twin,
                    DistinctReason = "aquél es la mutación; éste es la carrera entre dos peticiones en vuelo",
                },
            });
            retry.Results[0].Accepted.Should().BeTrue();
        });

        SessionResult result = await Run(agent);

        result.Counters.New.Should().Be(2);
        result.Counters.VariantsInsisted.Should().Be(1);
        result.Counters.VariantsRejected.Should().Be(1, "un rechazo, y solo uno");

        Finding insisted = _hub.Store.ListFindings("app").Single(f => f.Title.Contains("Race"));
        insisted.History.Should().Contain(h =>
            h.Detail!.Contains("insistido") && h.Detail.Contains("carrera entre dos peticiones"));
        Session().Notes.Should().Contain(n => n.Contains("insistido") && n.Contains(Unit));

        Report(result).Should().Contain("Posible duplicado de",
            "el criterio del filtro y el de la marca son el mismo, así que lo que entra insistido se marca");
    }

    /// <summary>
    /// <b>Un reintento, no un bucle.</b> Con <c>distinctFrom</c> puesto, el filtro no vuelve a
    /// mirar: no existe un segundo rechazo. Convertir la puerta en un muro sería peor que el
    /// problema que resuelve — el auditor se quedaría sin forma de sostener una discrepancia.
    /// </summary>
    [Fact]
    public async Task No_hay_segundo_rechazo()
    {
        var errors = new List<string?>();
        var agent = new ScriptedAgent((toolbox, _) =>
        {
            toolbox.SubmitFindings(new[] { Race("Mutación de DefaultRequestHeaders") });
            for (int i = 0; i < 3; i++)
            {
                SubmitFindingsResult r = toolbox.SubmitFindings(new[]
                {
                    Race($"Race en Authorization ({i})") with { DistinctFrom = "cualquier-cosa", DistinctReason = "otro" },
                });
                errors.Add(r.Results[0].Error);
            }
        });

        SessionResult result = await Run(agent);

        errors.Should().AllSatisfy(e => e.Should().BeNull());
        result.Counters.VariantsRejected.Should().Be(0);
        result.Counters.VariantsInsisted.Should().Be(3);
    }

    /// <summary>
    /// Un reintento SIN motivo entra igual —bloquearlo sería un muro por una casilla vacía— y se
    /// registra diciendo que vino sin él. Un dato con su causa, no un hueco.
    /// </summary>
    [Fact]
    public async Task Un_reintento_sin_motivo_entra_y_se_dice_que_vino_sin_motivo()
    {
        var agent = new ScriptedAgent((toolbox, _) =>
        {
            toolbox.SubmitFindings(new[] { Race("Mutación de DefaultRequestHeaders") });
            string twin = _hub.Store.ListFindings("app").Single().Id.ToString();
            toolbox.SubmitFindings(new[] { Race("Race en Authorization") with { DistinctFrom = twin } });
        });

        SessionResult result = await Run(agent);

        result.Counters.New.Should().Be(2);
        result.Counters.VariantsInsisted.Should().Be(1);
        Session().Notes.Should().Contain(n => n.Contains("sin motivo declarado"));
    }

    // ---------------------------------------------------------------- lo que pasa

    /// <summary>
    /// <b>Regla distinta: pasa.</b> Es uno de los dos pares que el filtro no ve por construcción —
    /// «sin Timeout» es <c>criterio.rendimiento</c> y «sin CancellationToken» es
    /// <c>criterio.arquitectura</c>—. Queda a cargo del contrato del prompt, y se dice de antemano.
    /// </summary>
    [Fact]
    public async Task Con_regla_distinta_pasa()
    {
        var agent = new ScriptedAgent((toolbox, _) => toolbox.SubmitFindings(new[]
        {
            Race("HttpClient sin Timeout") with { RuleId = "criterio.rendimiento" },
            Race("Métodos async sin CancellationToken") with { RuleId = "criterio.arquitectura" },
        }));

        SessionResult result = await Run(agent);

        result.Counters.New.Should().Be(2);
        result.Counters.VariantsRejected.Should().Be(0);
    }

    /// <summary>
    /// <b>Símbolo distinto: pasa.</b> Es lo que hace el trabajo de verdad: sin mirar el símbolo,
    /// «división por cero en CargaMediaPorMetro» y «división por cero en CargaEspecifica» caen a
    /// cinco líneas la una de la otra y se rebotarían siendo dos métodos.
    /// </summary>
    [Fact]
    public async Task Con_simbolo_distinto_pasa()
    {
        var agent = new ScriptedAgent((toolbox, _) => toolbox.SubmitFindings(new[]
        {
            Race("División por cero", 28, "CargaMediaPorMetro"),
            Race("División por cero", 33, "CargaEspecifica"),
        }));

        SessionResult result = await Run(agent);

        result.Counters.New.Should().Be(2);
        result.Counters.VariantsRejected.Should().Be(0);
    }

    /// <summary>
    /// <b>A más de cinco líneas: pasa.</b> La distancia se midió (ver <see cref="DuplicateHints"/>):
    /// ampliarla pillaba un par más a cambio de once falsos.
    /// </summary>
    [Fact]
    public async Task A_mas_de_cinco_lineas_pasa()
    {
        var agent = new ScriptedAgent((toolbox, _) => toolbox.SubmitFindings(new[]
        {
            Race("Race en Authorization", 23),
            Race("Race en Authorization, más abajo", 40),
        }));

        SessionResult result = await Run(agent);

        result.Counters.New.Should().Be(2);
        result.Counters.VariantsRejected.Should().Be(0);
    }

    /// <summary>
    /// <b><c>add_locations</c> no se ve afectada</b>, y es un anti-objetivo explícito: el camino
    /// bueno —extender un defecto sistémico a otro punto— no puede pagar el peaje del malo.
    /// </summary>
    [Fact]
    public async Task add_locations_no_se_ve_afectado()
    {
        var agent = new ScriptedAgent((toolbox, _) =>
        {
            toolbox.SubmitFindings(new[] { Race("Mutación de DefaultRequestHeaders") });
            string id = _hub.Store.ListFindings("app").Single().Id.ToString();
            AddLocationsResult r = toolbox.AddLocations(id, new[] { new SubmitLocation(Unit, 25, null) });
            r.Accepted.Should().BeTrue();
            r.Added.Should().Be(1);
        });

        SessionResult result = await Run(agent);

        result.Counters.VariantsRejected.Should().Be(0);
        result.Counters.LocationsAdded.Should().Be(1);
        _hub.Store.ListFindings("app").Single().Locations.Should().HaveCount(2);
    }

    /// <summary>
    /// Y contra un hallazgo de una sesión ANTERIOR también: «existente» es la lista de la unidad,
    /// no solo lo del barrido en curso.
    /// </summary>
    [Fact]
    public async Task Tambien_se_compara_contra_los_hallazgos_previos_de_la_unidad()
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-7), AuditMode.Lotes, "old", "alvaro");
        var previous = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.concurrencia.race",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Title = "Mutación de DefaultRequestHeaders",
            Symbol = "EnviarParteAsync",
            Locations = { new Location(Unit, 23) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("app", previous);

        var agent = new ScriptedAgent((toolbox, request) =>
        {
            toolbox.ReportVerdicts(request.Existing
                .Select(e => new VerdictArgs(e.FindingId, "presente", "sigue"))
                .ToArray());
            toolbox.SubmitFindings(new[] { Race("Race en Authorization") });
        });

        SessionResult result = await Run(agent);

        result.Counters.New.Should().Be(0);
        result.Counters.VariantsRejected.Should().Be(1);
    }

    // ---------------------------------------------------------------- el criterio compartido

    /// <summary>
    /// <b>El filtro y la marca del informe son el MISMO criterio</b>, no dos con el mismo número
    /// escrito dos veces. Si divergieran, el informe marcaría lo que el filtro dejó pasar y al
    /// revés, y nadie sabría cuál de los dos mirar.
    /// <para>
    /// La prueba no compara constantes: recorre los casos de decisión del criterio, pregunta a
    /// <see cref="DuplicateHints"/> y comprueba que la puerta de <c>submit_findings</c> contesta lo
    /// mismo. Una copia con su propio umbral rompe esto en el primer caso que se separe.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("errores.concurrencia.race", 23, "EnviarParteAsync", true)]
    [InlineData("errores.concurrencia.race", 28, "EnviarParteAsync", true)]
    [InlineData("errores.concurrencia.race", 29, "EnviarParteAsync", false)]
    [InlineData("criterio.arquitectura", 23, "EnviarParteAsync", false)]
    [InlineData("errores.concurrencia.race", 23, "DescargarPlantilla", false)]
    [InlineData("errores.concurrencia.race", 23, "ClienteRemoto", false)]
    public async Task El_filtro_contesta_lo_mismo_que_la_marca_del_informe(
        string ruleId, int line, string symbol, bool esVariante)
    {
        var primero = new DuplicateHints.VariantKey("errores.concurrencia.race", Unit, 23, "EnviarParteAsync");
        var segundo = new DuplicateHints.VariantKey(ruleId, Unit, line, symbol);
        DuplicateHints.AreSimilar(primero, segundo).Should().Be(
            esVariante, "el caso está escrito contra el criterio de DuplicateHints");

        var agent = new ScriptedAgent((toolbox, _) => toolbox.SubmitFindings(new[]
        {
            Race("Mutación de DefaultRequestHeaders"),
            Race("El segundo", line, symbol) with { RuleId = ruleId },
        }));

        SessionResult result = await Run(agent);

        result.Counters.VariantsRejected.Should().Be(esVariante ? 1 : 0,
            "la puerta usa exactamente ese criterio, no una copia suya");
    }

    // ---------------------------------------------------------------- el anexo

    /// <summary>
    /// Los contadores van al ANEXO técnico, no al cuerpo (F23 §1): quien viene a arreglar su código
    /// no necesita saber cuántas veces el auditor volvió a contar lo mismo, y quien mantiene Atalaya
    /// no puede saberlo de otra manera.
    /// </summary>
    [Fact]
    public async Task Los_contadores_se_ven_en_el_anexo_tecnico()
    {
        var agent = new ScriptedAgent((toolbox, _) =>
        {
            toolbox.SubmitFindings(new[] { Race("Mutación de DefaultRequestHeaders") });
            toolbox.SubmitFindings(new[] { Race("Race en Authorization") });
        });

        SessionResult result = await Run(agent);
        string report = Report(result);

        int annex = report.IndexOf("## Anexo técnico", StringComparison.Ordinal);
        annex.Should().BeGreaterThan(0);
        int line = report.IndexOf("**Variantes** (F24)", StringComparison.Ordinal);
        line.Should().BeGreaterThan(annex, "el cuerpo del informe no cambia");
        report.Should().Contain("1 rebotada(s) en la puerta");
        report.Should().Contain($"{Unit}: pasada 1: 1 rebotada(s)");
    }

    /// <summary>Sin variantes no se escribe la línea: un cero permanente enseña un hueco.</summary>
    [Fact]
    public async Task Sin_variantes_el_anexo_no_dice_nada()
    {
        SessionResult result = await Run(new ScriptedAgent((toolbox, _) =>
            toolbox.SubmitFindings(new[] { Race("Mutación de DefaultRequestHeaders") })));

        Report(result).Should().NotContain("**Variantes** (F24)");
    }

    // ---------------------------------------------------------------- utillaje

    /// <summary>El ULID que el error tipado nombra: 26 caracteres de Crockford base32.</summary>
    private static string ExtractUlid(string error)
    {
        string[] tokens = error.Split(new[] { ' ', '\'', '(', ')', ',', '«', '»' }, StringSplitOptions.RemoveEmptyEntries);
        return tokens.First(t => t.Length == 26 && t.All(char.IsLetterOrDigit));
    }

    /// <summary>
    /// Un auditor guionizado que conduce el toolbox a mano. Hace falta porque el agente falso
    /// entrega su lote de una vez y lo que hay que ejercitar aquí es justo lo contrario: <b>ver el
    /// rechazo y reaccionar dentro de la misma pasada</b>, que es como ocurre el reintento.
    /// </summary>
    private sealed class ScriptedAgent : IAuditorProvider
    {
        private readonly Action<IAuditToolbox, AuditUnitRequest> _script;

        public ScriptedAgent(Action<IAuditToolbox, AuditUnitRequest> script) => _script = script;

        public string? ModelName => "guionizado";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            _script(toolbox, request);
            UsageReported?.Invoke(new UsageSample(100, 50, null, ModelName));
            toolbox.UnitDone(request.UnitPath, "Revisados: EnviarParteAsync, DescargarPlantilla.");
            TextStreamed?.Invoke(string.Empty);
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }
}
