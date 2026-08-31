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
/// F5.15 — un fallo de arranque nunca es mudo, y el modelo por defecto no es un literal.
/// <para>
/// El parte (12:20:06): <c>session.create</c> falló con «Model gpt-5 is not available» y la interfaz
/// quedó zombi — reloj congelado en 00:02, sin «Detener», sin item de navegación y sin un solo
/// error a la vista. Dos causas encadenadas: el modelo por defecto era un nombre escrito a mano que
/// GitHub retiró, y un fallo dejaba la sesión en un estado que la interfaz interpretaba como
/// «aquí no hay nada que enseñar».
/// </para>
/// </summary>
public sealed class SessionStartFailureTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly MachineConfigStore _machines;
    private const string RepoUrl = "https://example.invalid/org/app.git";
    private const string UnitPath = "src/A.cs";

    public SessionStartFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-startfail", Guid.NewGuid().ToString("N"));
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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private void Configure(string model)
    {
        AppSettings s = _settings.Current;
        s.CopilotModel = model;
        _settings.Save(s);
    }

    private LiveSessionService Live(ICopilotAgent agent, ModelResolver? models = null)
        => new(
            () => new SessionCoordinator(
                _hub, new FindingIngestionService(_hub, _ulids), new ReconciliationService(_hub),
                _machines, _ulids, agent, _settings),
            agent, new OpenSessionStore(_paths), _hub, models);

    private static async Task Wait(LiveSessionService live)
    {
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        live.IsRunning.Should().BeFalse();
    }

    /// <summary>Un agente que revienta al crear la sesión, como hizo el runtime aquella noche.</summary>
    private sealed class FailingAgent : ICopilotAgent
    {
        private readonly Exception _failure;

        public FailingAgent(Exception failure) => _failure = failure;

        public string? ModelName => "el-modelo-configurado";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(new[] { new AgentModel("el-modelo-configurado", "M") });

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(0, 0, null, ModelName));
            throw _failure;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }

    // ================================================================ BUG 1 · el fallo se ve

    /// <summary>
    /// <b>El zombi.</b> Un <c>CreateSessionAsync</c> fallido tiene que dejar la sesión en un estado
    /// terminal VISIBLE: con su mensaje, con el camino de vuelta abierto y sin nada corriendo.
    /// </summary>
    [Fact]
    public async Task Un_arranque_fallido_deja_la_sesion_marcada_como_fallida_y_visible()
    {
        Configure("modelo-muerto");
        LiveSessionService live = Live(new FailingAgent(
            new CopilotModelUnavailableException("modelo-muerto")));
        var avisos = new List<string>();
        live.Failed += avisos.Add;

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        live.HasFailed.Should().BeTrue("un fallo es un estado terminal, no la ausencia de estado");
        live.HasFinished.Should().BeFalse("no terminó: murió");
        live.HasSession.Should().BeTrue(
            "el item del rail cuelga de esto: sin él no hay forma de volver a leer el error");
        live.FailureMessage.Should().Contain("modelo-muerto").And.Contain("Ajustes");
        live.FailureOffersModelChange.Should().BeTrue("tiene remedio de un clic y hay que ofrecerlo");
        avisos.Should().ContainSingle("quien lanzó la auditoría puede estar en otra pantalla");
    }

    /// <summary>Y V5 lo PINTA: el panel de fallo con su mensaje y su atajo.</summary>
    [Fact]
    public async Task V5_enseña_el_fallo_y_ofrece_el_atajo_a_ajustes()
    {
        Configure("modelo-muerto");
        LiveSessionService live = Live(new FailingAgent(
            new CopilotModelUnavailableException("modelo-muerto")));
        var vm = new SessionViewModel(live);

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        vm.ShowFailure.Should().BeTrue("es la única superficie que enseña por qué no se auditó nada");
        vm.ShowSummary.Should().BeFalse("no hay cierre que resumir");
        vm.FailureMessage.Should().Contain("modelo-muerto");
        vm.FailureOffersModelChange.Should().BeTrue();
        vm.IsRunning.Should().BeFalse("y «Detener» se retira: no hay nada que parar");
        vm.Title.Should().Contain("fallida");
    }

    /// <summary>
    /// La plantilla también: el panel existe, cuelga de <c>ShowFailure</c> y lleva el atajo. Es la
    /// mitad que el compilador no vigila — y la que faltaba.
    /// </summary>
    [Fact]
    public void La_vista_de_sesion_declara_el_panel_de_fallo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        string xaml = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Atalaya.App", "Views", "SessionView.xaml"));

        xaml.Should().Contain("{Binding ShowFailure, Converter={StaticResource BoolToVisibility}}");
        xaml.Should().Contain("{Binding FailureMessage, Mode=OneWay}");
        xaml.Should().Contain("FixModelCommand");
    }

    /// <summary>
    /// Un fallo cualquiera —no de modelo— también se ve, pero sin ofrecer un remedio que no aplica.
    /// </summary>
    [Fact]
    public async Task Un_fallo_que_no_es_de_modelo_se_ve_sin_ofrecer_ajustes()
    {
        Configure("modelo-vivo");
        LiveSessionService live = Live(new FailingAgent(new InvalidOperationException("el disco ardió")));

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        live.HasFailed.Should().BeTrue();
        live.HasSession.Should().BeTrue();
        live.FailureMessage.Should().Contain("el disco ardió");
        live.FailureOffersModelChange.Should().BeFalse("cambiar de modelo no arregla un disco");
    }

    /// <summary>
    /// Y el fallo no deja nada colgando: sin marca de sesión abierta —que es lo que haría creer al
    /// arranque siguiente que hubo un cierre forzado— y con el reloj parado.
    /// </summary>
    [Fact]
    public async Task Un_fallo_no_deja_marca_de_sesion_abierta()
    {
        Configure("modelo-muerto");
        var store = new OpenSessionStore(_paths);
        LiveSessionService live = Live(new FailingAgent(
            new CopilotModelUnavailableException("modelo-muerto")));

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        store.TryRead().Should().BeNull("una sesión fallida no necesita recuperación");
        live.EndedUtc.Should().NotBeNull("el reloj se para: 00:02 congelado era el síntoma");
    }

    /// <summary>Una sesión nueva limpia el fallo anterior: el panel no se queda pegado.</summary>
    [Fact]
    public async Task Lanzar_otra_vez_limpia_el_fallo_anterior()
    {
        Configure("modelo-vivo");
        LiveSessionService live = Live(new FailingAgent(new InvalidOperationException("fallo")));
        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);
        live.HasFailed.Should().BeTrue();

        var good = new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>());
        LiveSessionService second = Live(good);
        await second.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(second);

        second.HasFailed.Should().BeFalse();
        second.HasFinished.Should().BeTrue();
    }

    // ================================================================ BUG 2 · el modelo se resuelve

    private ModelResolver Resolver(params string[] available)
        => new(
            new FakeCopilotAgent(modelsScript: () => available.Select(m => new AgentModel(m, m)).ToList()),
            _settings);

    /// <summary>Máquina recién instalada: sin modelo elegido, se resuelve contra la lista real.</summary>
    [Fact]
    public async Task Sin_modelo_configurado_se_elige_uno_disponible_y_se_dice()
    {
        Configure(string.Empty);

        ModelResolution r = await Resolver("modelo-a", "modelo-b").ResolveAsync(CancellationToken.None);

        r.ModelId.Should().Be("modelo-a", "el orden del runtime es la recomendación del proveedor");
        r.Changed.Should().BeTrue();
        r.Failed.Should().BeFalse();
        r.Notice.Should().Contain("automáticamente").And.Contain("modelo-a").And.Contain("Ajustes");
        _settings.Load().CopilotModel.Should().Be("modelo-a", "y se guarda: no se resuelve dos veces");
    }

    /// <summary>El caso del parte: el modelo guardado ya no existe. Se sustituye y se avisa.</summary>
    [Fact]
    public async Task Un_modelo_guardado_que_ya_no_existe_se_sustituye_y_se_avisa()
    {
        Configure("gpt-que-retiraron");

        ModelResolution r = await Resolver("modelo-nuevo").ResolveAsync(CancellationToken.None);

        r.ModelId.Should().Be("modelo-nuevo");
        r.Changed.Should().BeTrue();
        r.Notice.Should().Contain("gpt-que-retiraron").And.Contain("ya no está disponible")
            .And.Contain("modelo-nuevo");
        _settings.Load().CopilotModel.Should().Be("modelo-nuevo");
    }

    /// <summary>Y si el configurado sigue vivo, no se toca nada ni se molesta al usuario.</summary>
    [Fact]
    public async Task Un_modelo_valido_se_deja_en_paz()
    {
        Configure("modelo-b");

        ModelResolution r = await Resolver("modelo-a", "modelo-b").ResolveAsync(CancellationToken.None);

        r.ModelId.Should().Be("modelo-b");
        r.Changed.Should().BeFalse();
        r.Notice.Should().BeNull("el caso normal no merece un aviso");
        _settings.Load().CopilotModel.Should().Be("modelo-b");
    }

    /// <summary>
    /// Sin lista y sin modelo elegido no se arranca: dejar que el runtime lo rechace después solo
    /// cambia un aviso claro por un fallo feo.
    /// </summary>
    [Fact]
    public async Task Sin_lista_y_sin_modelo_la_resolucion_falla_diciendolo()
    {
        Configure(string.Empty);
        var resolver = new ModelResolver(
            new FakeCopilotAgent(modelsScript: () => throw new InvalidOperationException("sin red")),
            _settings);

        ModelResolution r = await resolver.ResolveAsync(CancellationToken.None);

        r.Failed.Should().BeTrue();
        r.Notice.Should().Contain("sin red").And.Contain("Ajustes");
        _settings.Load().CopilotModel.Should().BeEmpty("no se inventa un modelo cuando no se pudo preguntar");
    }

    /// <summary>Sin lista pero con modelo elegido se sigue: puede ser válido y el fallo estar en la red.</summary>
    [Fact]
    public async Task Sin_lista_pero_con_modelo_configurado_se_sigue()
    {
        Configure("modelo-elegido");
        var resolver = new ModelResolver(
            new FakeCopilotAgent(modelsScript: () => throw new InvalidOperationException("sin red")),
            _settings);

        ModelResolution r = await resolver.ResolveAsync(CancellationToken.None);

        r.Failed.Should().BeFalse();
        r.ModelId.Should().Be("modelo-elegido");
        r.Notice.Should().BeNull();
    }

    /// <summary>
    /// Y el circuito entero: una máquina virgen lanza una auditoría, el modelo se resuelve solo, se
    /// avisa del cambio y la sesión corre. Es el arranque que el 2026-08-26 murió mudo.
    /// </summary>
    [Fact]
    public async Task Una_maquina_virgen_resuelve_el_modelo_y_audita()
    {
        Configure(string.Empty);
        var agent = new FakeCopilotAgent(
            auditScript: _ => Array.Empty<SubmitFindingArgs>(),
            modelsScript: () => new[] { new AgentModel("modelo-a", "Modelo A") });
        LiveSessionService live = Live(agent, new ModelResolver(agent, _settings));
        var avisos = new List<string>();
        live.Notice += avisos.Add;

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        live.HasFailed.Should().BeFalse("con el modelo resuelto la sesión arranca");
        live.HasFinished.Should().BeTrue();
        avisos.Should().ContainSingle().Which.Should().Contain("modelo-a");
        _settings.Load().CopilotModel.Should().Be("modelo-a");
    }

    /// <summary>
    /// Si no hay ningún modelo utilizable, la sesión NO arranca y lo dice — con el atajo a Ajustes.
    /// </summary>
    [Fact]
    public async Task Sin_ningun_modelo_utilizable_no_se_arranca_y_se_dice()
    {
        Configure(string.Empty);
        var agent = new FakeCopilotAgent(
            modelsScript: () => throw new InvalidOperationException("sin red"));
        LiveSessionService live = Live(agent, new ModelResolver(agent, _settings));

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        await Wait(live);

        live.HasFailed.Should().BeTrue();
        live.HasSession.Should().BeTrue("y con camino de vuelta para leerlo");
        live.FailureOffersModelChange.Should().BeTrue();
        _hub.Store.ListSessions("app").Should().BeEmpty("no se auditó nada");
    }

    // ================================================================ el reconocimiento del error

    /// <summary>
    /// El SDK no tipa este fallo: se reconoce por el texto que devolvió aquella noche, y por sus
    /// variantes razonables. Un mensaje que solo menciona un modelo de pasada NO cuenta.
    /// </summary>
    [Theory]
    [InlineData("Model gpt-5 is not available", true)]
    [InlineData("session.create failed: unknown model 'x'", true)]
    [InlineData("The model is not available for your account", true)]
    [InlineData("model_not_found", true)]
    [InlineData("Request timed out", false)]
    [InlineData("Bad credentials", false)]
    [InlineData("the model answered slowly", false)]
    public void El_fallo_de_modelo_se_reconoce_por_su_mensaje(string message, bool expected)
        => RealCopilotAgent.LooksLikeModelUnavailable(new InvalidOperationException(message))
            .Should().Be(expected);

    /// <summary>Y se reconoce también anidado, que es como llega envuelto por el SDK.</summary>
    [Fact]
    public void El_fallo_de_modelo_se_reconoce_dentro_de_otra_excepcion()
        => RealCopilotAgent.LooksLikeModelUnavailable(
                new InvalidOperationException("session.create", new Exception("Model gpt-5 is not available")))
            .Should().BeTrue();
}
