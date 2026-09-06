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
/// La sesión en vivo: arranque, cerrojo y supervivencia a la navegación.
/// <para>
/// <b>Origen (2026-08-25).</b> <c>LoadAsync</c> significa "recarga la vista" en todas las páginas,
/// pero en V5 EJECUTABA una auditoría. El tick de polling llamaba a
/// <c>Navigation.Current.LoadAsync()</c> cada vez que un pull traía cambios, y una sesión termina
/// haciendo commit+push: el poll se traía sus propios cambios y relanzaba la auditoría cada 60 s.
/// </para>
/// <para>
/// <b>F5.2 lo cierra por diseño.</b> Lanzar es un acto explícito sobre
/// <see cref="LiveSessionService"/> y navegar NO ejecuta trabajo jamás. Las invariantes de D-085
/// siguen probadas, pero donde ahora viven: en el servicio.
/// </para>
/// </summary>
public sealed class SessionViewModelTests : IDisposable
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

    public SessionViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-vm", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));

        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;   // aquí se prueba el disparo, no el barrido
        _settings.Save(s);
        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);

        File.WriteAllText(Path.Combine(_clone, "A.cs"), "class A { void M() { } }");
        File.WriteAllText(Path.Combine(_clone, "B.cs"), "class B { void N() { } }");
        _machines.SetClonePath("app", _clone);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });
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

    private static SubmitFindingArgs Sample()
        => new("errores.recursos.no-liberado", "errores", "critica",
            "Conn leaked", "desc", "impact", "reco",
            new[] { new SubmitLocation("A.cs", 1, "snippet") }, "A.M");

    private LiveSessionService NewLive(IAuditorProvider? agent = null)
    {
        IAuditorProvider auditor = agent ?? new FakeCopilotAgent(_ => new[] { Sample() });
        return new LiveSessionService(
            () => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, auditor, _settings),
            auditor,
            new OpenSessionStore(_paths));
    }

    private static SessionRequest Request(params string[] units)
        => new("app", AuditMode.Lotes, units.Length == 0 ? new[] { "A.cs" } : units);

    // ---------- D-085: navegar no ejecuta trabajo ----------

    /// <summary>
    /// El caso exacto del incidente, ahora imposible por construcción: recargar la vista no puede
    /// arrancar una auditoría porque la vista ya no sabe arrancar sesiones.
    /// </summary>
    [Fact]
    public async Task Reloading_the_view_never_launches_an_audit()
    {
        LiveSessionService live = NewLive();
        await live.StartAsync(Request(), new[] { "A.cs" });
        _hub.Store.ListSessions("app").Should().ContainSingle();

        var vm = new SessionViewModel(live);
        for (int tick = 0; tick < 5; tick++)
        {
            await vm.LoadAsync();       // ticks de polling
        }

        _hub.Store.ListSessions("app").Should().ContainSingle(
            "una recarga de la vista no puede ejecutar una auditoría entera");
    }

    /// <summary>Y nada puede haberse resuelto por el camino de una recarga.</summary>
    [Fact]
    public async Task Reloads_do_not_touch_the_findings()
    {
        LiveSessionService live = NewLive();
        await live.StartAsync(Request(), new[] { "A.cs" });

        var before = _hub.Store.ListFindings("app")
            .Select(f => (f.Id, f.Status, f.TimesConfirmed)).OrderBy(t => t.Id.ToString()).ToList();
        before.Should().NotBeEmpty("si la sesión no produjo hallazgos el test no comprueba nada");

        var vm = new SessionViewModel(live);
        await vm.LoadAsync();
        await vm.LoadAsync();

        var after = _hub.Store.ListFindings("app")
            .Select(f => (f.Id, f.Status, f.TimesConfirmed)).OrderBy(t => t.Id.ToString()).ToList();
        after.Should().BeEquivalentTo(before);
    }

    /// <summary>Lanzar otra vez sí arranca otra sesión: el usuario manda.</summary>
    [Fact]
    public async Task Launching_again_starts_a_new_session()
    {
        LiveSessionService live = NewLive();

        await live.StartAsync(Request(), new[] { "A.cs" });
        await live.StartAsync(Request(), new[] { "A.cs" });

        _hub.Store.ListSessions("app").Should().HaveCount(2);
    }

    /// <summary>
    /// La carrera, medida donde ocurre: cuántas llamadas atraviesan el guardia. Se cuentan las
    /// entradas al agente y no las sesiones escritas, porque dos ejecuciones concurrentes se
    /// estorban entre sí y contar sesiones daría verde por accidente.
    /// </summary>
    [Fact]
    public async Task Concurrent_launches_pass_the_guard_exactly_once()
    {
        var gate = new GatedAgent();
        LiveSessionService live = NewLive(gate);

        Task a = live.StartAsync(Request(), new[] { "A.cs" });
        Task b = live.StartAsync(Request(), new[] { "A.cs" });
        Task c = live.StartAsync(Request(), new[] { "A.cs" });

        gate.Entered.Should().Be(1, "solo el primer disparo puede atravesar el guardia");

        gate.Open();
        await Task.WhenAll(a, b, c);
    }

    // ---------- F5.2 Hito 1: la sesión sobrevive a la navegación ----------

    /// <summary>
    /// Navegar fuera y volver enseña la sesión AL DÍA. Antes el estado vivía en el view-model, que
    /// es Transient: volver creaba una instancia nueva y la pantalla aparecía vacía aunque la
    /// auditoría hubiera corrido.
    /// </summary>
    [Fact]
    public async Task Coming_back_to_the_view_shows_the_real_state()
    {
        LiveSessionService live = NewLive();
        await live.StartAsync(Request("A.cs", "B.cs"), new[] { "A.cs", "B.cs" });

        // "Navegar fuera y volver" = una instancia NUEVA de la vista, como hace la navegación.
        var reopened = new SessionViewModel(live);
        await reopened.LoadAsync();

        reopened.Units.Should().HaveCount(2);
        reopened.Units.Select(u => u.Path).Should().Equal("A.cs", "B.cs");
        reopened.Units.Should().OnlyContain(u => u.State == UnitRunState.Completa
                                                 || u.State == UnitRunState.CoberturaIncompleta);
        reopened.Findings.Should().NotBeEmpty("los hallazgos de la sesión siguen ahí");
        reopened.Units.SelectMany(u => u.Passes).Should().NotBeEmpty("y su narración también");
    }

    /// <summary>Al terminar sin la vista abierta, el item de navegación pasa a «Última sesión».</summary>
    [Fact]
    public async Task Finishing_with_the_view_closed_leaves_a_last_session_to_open()
    {
        LiveSessionService live = NewLive();
        live.HasSession.Should().BeFalse("sin sesiones no hay item que enseñar");

        await live.StartAsync(Request(), new[] { "A.cs" });

        live.IsRunning.Should().BeFalse();
        live.HasFinished.Should().BeTrue();
        live.HasSession.Should().BeTrue();

        var vm = new SessionViewModel(live);
        await vm.LoadAsync();
        // Con tildes desde D-983: el rail y el MANUAL ya la llamaban asi.
        vm.Title.Should().Be("Última sesión");
        vm.ShowSummary.Should().BeTrue("la pantalla de cierre sustituye a la línea fugaz de estado");
    }

    /// <summary>
    /// Cada contador del cierre trae su explicación y su desglose. Ningún número sin causa: la
    /// disputa invisible de D-114 fue el último aviso.
    /// </summary>
    [Fact]
    public async Task The_closing_screen_explains_every_counter()
    {
        LiveSessionService live = NewLive();
        await live.StartAsync(Request(), new[] { "A.cs" });

        live.Summary.Should().NotBeEmpty();
        live.Summary.Should().OnlyContain(l => !string.IsNullOrWhiteSpace(l.Explanation));

        SummaryLine nuevos = live.Summary.Single(l => l.Label == "Nuevos");
        nuevos.Count.Should().Be(1);
        nuevos.Named.Should().ContainSingle().Which.Should().Contain("Conn leaked");

        // F12 §H.1 — y va agrupado por clase, con su recuento por severidad, igual que en Hallazgos.
        SummaryGroup group = nuevos.Groups.Should().ContainSingle().Subject;
        group.Unit.Should().Be("A.cs");
        group.FileName.Should().Be("A.cs");
        group.CountLabel.Should().Be("1 hallazgo");
        group.Chips.Should().ContainSingle().Which.Count.Should().Be(1);
    }

    /// <summary>Terminada la sesión, la barra inferior deja de decir «auditando».</summary>
    [Fact]
    public async Task The_status_line_is_empty_once_the_session_ends()
    {
        LiveSessionService live = NewLive();
        await live.StartAsync(Request(), new[] { "A.cs" });

        live.ProgressLine.Should().BeEmpty();
        live.UnitCount.Should().Be(1);
    }

    // ---------- F5.2 Hito 2: la actividad narra, nunca son puntos ----------

    /// <summary>
    /// La columna de actividad trae el texto REAL del agente y los eventos de herramienta. Antes
    /// el adaptador emitía un punto por evento y la columna era literalmente una fila de puntos.
    /// </summary>
    [Fact]
    public async Task The_activity_column_carries_real_text_and_tool_events()
    {
        LiveSessionService live = NewLive();
        await live.StartAsync(Request(), new[] { "A.cs" });

        var entries = live.Units.SelectMany(u => u.Passes).SelectMany(p => p.Entries).ToList();
        entries.Should().NotBeEmpty();

        string texto = string.Concat(entries.Where(e => e.Kind == ConversationKind.Prosa).Select(e => e.Text));
        texto.Should().NotBeNullOrWhiteSpace();
        texto.Replace(".", string.Empty).Trim().Should().NotBeEmpty("una columna de puntos no narra nada");

        entries.Should().Contain(e => e.IsEvent && e.Text.Contains("Hallazgo"));
        entries.Should().Contain(e => e.IsEvent && e.Text.Contains("Pasada"));
    }

    /// <summary>
    /// F5.3 §1: la cola enseña SOLO el nombre del fichero. Ni ruta, ni elipsis — eso vive en el
    /// tooltip y en la cabecera de actividad, que ya lo muestran. La elipsis en medio de D-122
    /// resolvía el síntoma equivocado: el problema no era dónde cortar la ruta, sino que la ruta
    /// no pintaba nada en una columna de 250 px.
    /// </summary>
    [Theory]
    [InlineData("XBLASTCommon/Class/CommonStatics.cs", "CommonStatics.cs")]
    [InlineData("src/muy/larga/ruta/con/muchos/tramos/Fichero.cs", "Fichero.cs")]
    [InlineData("A.cs", "A.cs")]
    [InlineData(@"src\windows\Ruta.cs", "Ruta.cs")]
    public void The_queue_shows_only_the_unit_file_name(string path, string expected)
        => UnitProgress.ShortNames(new[] { path }).Single().Should().Be(expected);

    /// <summary>
    /// Y cuando dos unidades del lote comparten nombre, se desambigua con el MÍNIMO necesario:
    /// un tramo, no la ruta entera. Las que no chocan siguen a nombre pelado.
    /// </summary>
    [Fact]
    public void Only_the_clashing_units_grow_and_only_by_what_they_need()
    {
        IReadOnlyList<string> names = UnitProgress.ShortNames(new[]
        {
            "XBLASTCommon/Class/EnumContextMenuType.cs",
            "XBLASTWeb/Enums/EnumContextMenuType.cs",
            "XBLASTCommon/Class/CommonStatics.cs",
        });

        names[0].Should().Be("Class/EnumContextMenuType.cs");
        names[1].Should().Be("Enums/EnumContextMenuType.cs");
        names[2].Should().Be("CommonStatics.cs", "esta no choca con nadie: no gana ruta");
    }

    /// <summary>
    /// El caso que obliga a re-agrupar: alargar un tramo puede crear un choque NUEVO entre dos que
    /// antes eran distintos. Si no se recomprobara, la cola enseñaría dos filas idénticas.
    /// </summary>
    [Fact]
    public void Growing_one_segment_never_leaves_two_rows_reading_the_same()
    {
        IReadOnlyList<string> names = UnitProgress.ShortNames(new[]
        {
            "Ruta.cs",
            "a/Ruta.cs",
            "b/a/Ruta.cs",
        });

        names.Should().OnlyHaveUniqueItems();
        names[2].Should().Be("b/a/Ruta.cs");
    }

    /// <summary>La cola se construye ya con los nombres cortos: no es cosa de la vista.</summary>
    [Fact]
    public async Task Starting_a_session_names_the_queue_with_short_names()
    {
        LiveSessionService live = NewLive();

        await live.StartAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs", "B.cs" }),
            new[] { "dir/A.cs", "otro/B.cs" });

        live.Units.Select(u => u.ShortName).Should().Equal("A.cs", "B.cs");
        // La ruta completa sigue ahí: es la que alimentan el tooltip y la cabecera de actividad.
        live.Units.Select(u => u.Path).Should().Equal("dir/A.cs", "otro/B.cs");
    }

    // ---------- F6.3 §3: «Ver informe» lleva al visor, no al bloc de notas ----------

    /// <summary>
    /// El informe de la última sesión se lee DENTRO de la aplicación, en la vista Informes, con
    /// sus tablas pintadas. Antes salía a la aplicación que el sistema asociara al <c>.md</c>:
    /// era una segunda forma de leer lo mismo, y la peor de las dos.
    /// </summary>
    [Fact]
    public async Task Ver_informe_abre_la_vista_informes_con_el_informe_de_esta_sesion()
    {
        // Con el hub conectado: es de donde LiveSessionService saca la ruta del informe.
        var live = new LiveSessionService(
            () => new SessionCoordinator(
                _hub, _ingestion, _reconciliation, _machines, _ulids,
                new FakeCopilotAgent(_ => new[] { Sample() }), _settings),
            new FakeCopilotAgent(_ => new[] { Sample() }),
            new OpenSessionStore(_paths),
            _hub);
        await live.StartAsync(Request(), new[] { "A.cs" });

        live.ReportPath.Should().NotBeEmpty();
        File.Exists(live.ReportPath).Should().BeTrue("la sesión dejó su informe en el hub");

        ReportsViewModel reports = TestFactory.Reports(_hub);
        NavigationService navigation = TestFactory.NavigationWith(reports);
        var vm = new SessionViewModel(live, navigation);

        await vm.OpenReportCommand.ExecuteAsync(null);

        navigation.Current.Should().BeSameAs(reports);
        reports.IsViewing.Should().BeTrue();
        reports.OpenReport!.Entry.ReportId.Should().Be(live.SessionId);
        reports.OpenReport.Entry.Slug.Should().Be("app");
    }

    /// <summary>Sin informe en disco no se navega a ninguna parte: se dice y se queda donde está.</summary>
    [Fact]
    public async Task Sin_informe_en_disco_ver_informe_lo_dice_en_vez_de_navegar()
    {
        LiveSessionService live = NewLive();
        ReportsViewModel reports = TestFactory.Reports(_hub);
        NavigationService navigation = TestFactory.NavigationWith(reports);
        var vm = new SessionViewModel(live, navigation);

        await vm.OpenReportCommand.ExecuteAsync(null);

        navigation.Current.Should().BeNull();
        live.StatusMessage.Should().Contain("todavia no esta en disco");
    }

    /// <summary>Agente cuyo <c>CheckAsync</c> espera a una compuerta que abre el test.</summary>
    private sealed class GatedAgent : IAuditorProvider
    {
        private readonly IAuditorProvider _inner = new FakeCopilotAgent();
        private readonly TaskCompletionSource _gate = new();

        /// <summary>Cuántas llamadas llegaron a la compuerta. Es LA medida del cerrojo.</summary>
        public int Entered;

        public void Open() => _gate.SetResult();

        public string? ModelName => _inner.ModelName;
        public event Action<string>? TextStreamed { add { } remove { } }
        public event Action<UsageSample>? UsageReported { add { } remove { } }

        public async Task<AgentReadiness> CheckAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Entered);
            await _gate.Task;
            return await _inner.CheckAsync(ct);
        }

        public async Task<bool> EnsureReadyAsync(CancellationToken ct) => (await CheckAsync(ct)).Ready;
        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct) => _inner.ListModelsAsync(ct);
        public Task AuditUnitAsync(AuditUnitRequest r, IAuditToolbox t, CancellationToken ct) => _inner.AuditUnitAsync(r, t, ct);
        public Task VerifyAsync(VerifyRequest r, IVerifyToolbox t, CancellationToken ct) => _inner.VerifyAsync(r, t, ct);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
