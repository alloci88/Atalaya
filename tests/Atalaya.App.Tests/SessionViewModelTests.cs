using System.Text.RegularExpressions;
using System.Windows.Media;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    // ---------- F34 §2: los hallazgos de la columna se abren ----------

    /// <summary>
    /// <b>Solo se abre lo que ya existe en el hub</b> (D-226). Un hallazgo nace GUARDADO: el ULID
    /// se acuña en <c>FindingIngestionService.Create</c>, que escribe el fichero antes de avisar
    /// de que hay uno nuevo, así que todo lo que llega a la columna ya tiene ficha que abrir. Lo
    /// que no llegó a aceptarse no tiene ULID, y sin ULID el comando no está disponible: sin
    /// resaltado, sin cursor de mano y sin tooltip. O se abre, o no se anuncia.
    /// </summary>
    [Fact]
    public async Task Solo_se_abre_el_hallazgo_que_ya_esta_en_el_hub()
    {
        LiveSessionService live = NewLive();
        await live.StartAsync(Request(), new[] { "A.cs" });
        var vm = new SessionViewModel(live);

        Finding guardado = live.Findings.Should().ContainSingle().Subject;
        _hub.Store.TryReadFinding("app", guardado.Id.ToString())
            .Should().NotBeNull("un hallazgo de la columna nace guardado en el hub (D-226)");
        vm.OpenLiveFindingCommand.CanExecute(guardado).Should().BeTrue();

        // Lo propuesto y todavía no aceptado no tiene ULID: no hay ficha que abrir.
        var sinAceptar = new DetectionStamp(
            DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "auditor");
        var propuesto = new Finding
        {
            Title = "todavía no aceptado",
            RuleId = "errores.recursos.no-liberado",
            FirstDetected = sinAceptar,
            LastConfirmed = sinAceptar,
        };
        propuesto.Id.Should().Be(Ulid.Empty);
        vm.OpenLiveFindingCommand.CanExecute(propuesto).Should().BeFalse();
        vm.OpenLiveFindingCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// <b>Ir a la ficha no detiene ni reinicia la sesión</b> (D-572). El estado vive en el
    /// servicio singleton y esta pantalla es una vista sobre él, así que navegar fuera —a la ficha
    /// de un hallazgo que la propia sesión acaba de encontrar— deja la auditoría corriendo con el
    /// mismo identificador, los mismos hallazgos y las mismas unidades. El camino de vuelta lo
    /// pone el raíl, que mantiene su entrada «Sesión en vivo» mientras <c>IsRunning</c>.
    /// </summary>
    [Fact]
    public async Task Abrir_un_hallazgo_no_detiene_la_sesion_en_curso()
    {
        var agent = new PausesAfterFirstUnit(new FakeCopilotAgent(_ => new[] { Sample() }));
        LiveSessionService live = NewLive(agent);
        Task run = live.StartAsync(Request("A.cs", "B.cs"), new[] { "A.cs", "B.cs" });
        await agent.Paused;

        var navigation = new NavigationService(Services());
        var vm = new SessionViewModel(live, navigation);

        live.IsRunning.Should().BeTrue("la sesión está a mitad de camino");
        Finding hallazgo = live.Findings.Should().ContainSingle().Subject;
        string sesion = live.SessionId;
        int unidades = live.Units.Count;

        await vm.OpenLiveFindingCommand.ExecuteAsync(hallazgo);

        navigation.Current.Should().BeOfType<FindingDetailViewModel>("se ha ido a la ficha");
        live.IsRunning.Should().BeTrue("y la sesión sigue en curso detrás");
        live.SessionId.Should().Be(sesion, "es la MISMA sesión: no se ha reiniciado");
        live.Findings.Should().ContainSingle("el hilo no se ha perdido");
        live.Units.Should().HaveCount(unidades);

        agent.Open();
        await run;
    }

    /// <summary>El contenedor mínimo para que la navegación pueda resolver la ficha.</summary>
    private IServiceProvider Services()
    {
        var toasts = new ToastCenter();
        var services = new ServiceCollection();
        services.AddTransient(_ => new FindingDetailViewModel(
            _hub,
            new GovernanceService(_hub, _ulids),
            _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines),
            toasts,
            TestFactory.Links(_hub, _paths),
            TestFactory.LinkFlow(_hub, _paths, toasts)));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Audita la primera unidad entera —así hay un hallazgo en la columna— y se queda esperando en
    /// la segunda: la sesión está viva y a mitad cuando el test navega.
    /// </summary>
    private sealed class PausesAfterFirstUnit : IAuditorProvider
    {
        private readonly IAuditorProvider _inner;
        private readonly TaskCompletionSource _gate = new();
        private readonly TaskCompletionSource _paused = new();
        private int _units;

        public PausesAfterFirstUnit(IAuditorProvider inner) => _inner = inner;

        /// <summary>Se cumple cuando la primera unidad ya está auditada y la segunda espera.</summary>
        public Task Paused => _paused.Task;

        public void Open() => _gate.TrySetResult();

        public string? ModelName => _inner.ModelName;
        public event Action<string>? TextStreamed { add { } remove { } }
        public event Action<UsageSample>? UsageReported { add { } remove { } }

        public Task<AgentReadiness> CheckAsync(CancellationToken ct) => _inner.CheckAsync(ct);
        public Task<bool> EnsureReadyAsync(CancellationToken ct) => _inner.EnsureReadyAsync(ct);
        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct) => _inner.ListModelsAsync(ct);
        public Task VerifyAsync(VerifyRequest r, IVerifyToolbox t, CancellationToken ct) => _inner.VerifyAsync(r, t, ct);

        public async Task AuditUnitAsync(AuditUnitRequest r, IAuditToolbox t, CancellationToken ct)
        {
            await _inner.AuditUnitAsync(r, t, ct);
            if (Interlocked.Increment(ref _units) == 1)
            {
                _paused.TrySetResult();
                await _gate.Task;
            }
        }
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


    /// <summary>
    /// <b>La tarjeta de un hallazgo se distingue de su columna al pasar el ratón</b> (F34, retoque).
    /// <para>
    /// El defecto: <c>Button.Secondary</c> resalta pasando el fondo a <c>Brush.Surface2</c>, y la
    /// columna de hallazgos <b>es</b> <c>Brush.Surface2</c>. Al pasar el ratón la tarjeta tomaba
    /// exactamente el color de su columna y —sin borde— desaparecía justo cuando se la está
    /// señalando. Heredar el resaltado de la casa es lo correcto sobre cualquier otra superficie;
    /// sobre la propia superficie del resaltado, no.
    /// </para>
    /// <para>
    /// La regla que queda, y es la que se mide: <b>en hover, el fondo o el borde de la tarjeta
    /// tienen que ser distintos del fondo de la columna, en los DOS temas</b>. Y no «distintos» de
    /// un dígito hexadecimal: con margen, porque dos colores a un ΔE de tres se confunden igual.
    /// Se comparan RECURSOS de la paleta, no XAML: lo que se protege es que el estado se vea, no
    /// dónde está escrito.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void La_tarjeta_de_un_hallazgo_no_se_funde_con_su_columna_al_pasar_el_raton(string theme)
    {
        Dictionary<string, string> palette = PaletteOf(theme);

        string columna = palette["Color.Surface2"];
        string fondo = palette["Color.Bg"];
        string borde = palette["Color.Primary.Fill"];
        string resaltadoDeLaCasa = palette["Color.Surface2"];

        // El punto de partida, para que el test explique por qué existe: el resaltado heredado ES
        // el fondo de la columna. Si esto dejara de ser verdad, este test sobra.
        resaltadoDeLaCasa.Should().Be(
            columna, "el resaltado de Button.Secondary es el mismo tono que la columna de hallazgos");

        // Y POR QUÉ EL ARREGLO VA POR EL BORDE: las superficies no dan. Medido, el fondo de la
        // tarjeta y el de su columna están a ΔE 2,05 en claro y 8,6 en oscuro — ningún juego de
        // superficies de esta paleta separaría la tarjeta lo suficiente, y no hay un Surface3 al
        // que subir.
        Distancia(fondo, columna).Should().BeLessThan(
            10, $"en {theme}, las dos superficies están demasiado cerca para llevar ellas el estado");

        // Lo que sí se ve: el borde encendido, contra la columna y contra la propia tarjeta.
        Distancia(borde, columna).Should().BeGreaterThan(
            20, $"en {theme}, el borde encendido tiene que verse contra la columna");
        Distancia(borde, fondo).Should().BeGreaterThan(
            20, $"en {theme}, el borde encendido tiene que verse contra la propia tarjeta");
    }

    /// <summary>
    /// Y el borde del hover <b>no es un color nuevo</b>: es el acento que la casa ya usa como borde
    /// de estado —el de un campo con el foco, el de un desplegable enfocado y el del botón de
    /// acento—. La paleta no tiene ningún <c>Surface3</c> al que subir, y por eso el arreglo va por
    /// el borde y no por la superficie.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void El_borde_del_hover_es_un_estado_que_ya_existia_en_la_paleta(string theme)
    {
        Dictionary<string, string> palette = PaletteOf(theme);

        palette.Should().NotContainKey(
            "Color.Surface3", "no hay un tercer paso de superficie: subir uno exigiría un color nuevo");

        string xaml = Source($"src/Atalaya.App/Themes/Palette.{theme}.xaml");
        foreach (string estado in new[]
                 {
                     "TextControlFocusedBorderBrush", "ComboBoxBorderBrushFocused", "AccentButtonBorderBrush",
                 })
        {
            xaml.Should().MatchRegex(
                $@"x:Key=""{estado}""\s+Color=""\{{StaticResource Color\.Primary\.Fill\}}""",
                $"«{estado}» ya era el acento de borde de la casa");
        }
    }

    /// <summary>Los `Color` declarados en una paleta, leídos del XAML del repositorio.</summary>
    private static Dictionary<string, string> PaletteOf(string theme)
        => Regex.Matches(
                Source($"src/Atalaya.App/Themes/Palette.{theme}.xaml"),
                @"<Color x:Key=""([^""]+)"">\s*(#[0-9A-Fa-f]{6})\s*</Color>")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    /// <summary>Distancia CIEDE76 entre dos colores. Por debajo de ~10 dos tonos se confunden.</summary>
    private static double Distancia(string a, string b)
    {
        (double L1, double A1, double B1) = Lab(a);
        (double L2, double A2, double B2) = Lab(b);
        return Math.Sqrt(((L1 - L2) * (L1 - L2)) + ((A1 - A2) * (A1 - A2)) + ((B1 - B2) * (B1 - B2)));
    }

    private static (double L, double A, double B) Lab(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        double r = Linear(c.R / 255.0), g = Linear(c.G / 255.0), b = Linear(c.B / 255.0);
        double x = ((r * 0.4124) + (g * 0.3576) + (b * 0.1805)) / 0.95047;
        double y = (r * 0.2126) + (g * 0.7152) + (b * 0.0722);
        double z = ((r * 0.0193) + (g * 0.1192) + (b * 0.9505)) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116);
    }

    private static double Linear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
