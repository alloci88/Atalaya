using Atalaya.Agents;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F14 — la elección de proveedor, y las dos reglas que la acompañan: <b>se registra con quién se
/// auditó</b>, y <b>los costes de dos casas no se mezclan jamás</b>.
/// </summary>
public sealed class AuditorProviderTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public AuditorProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-proveedores", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        TestRates.Seed(_hub);
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u/app", CurrentCycle = 1,
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
    }

    // ================================================================ 1 · el registro

    /// <summary>Sin nada elegido se audita con Copilot, que es como funcionaba antes de F14.</summary>
    [Fact]
    public void Sin_eleccion_guardada_manda_Copilot()
        => Registry().Current.ProviderId.Should().Be("copilot");

    /// <summary>
    /// El proveedor se RELEE de los ajustes en cada consulta. Capturarlo al construir haría que
    /// cambiarlo en Ajustes no sirviera de nada hasta reiniciar, que es BUGFIX-AJUSTES otra vez.
    /// </summary>
    [Fact]
    public void Cambiar_el_proveedor_surte_efecto_sin_reiniciar()
    {
        AuditorProviderRegistry registry = Registry();
        registry.Current.ProviderId.Should().Be("copilot");

        Save(s => s.AuditorProvider = "claude-code");

        registry.Current.ProviderId.Should().Be("claude-code", "el mismo registro, sin reconstruir nada");
    }

    /// <summary>
    /// Un ajuste con el nombre de un proveedor que ya no existe NO deja a nadie sin poder auditar:
    /// se cae a Copilot. Quedarse sin juez por una cadena vieja sería el peor de los desenlaces.
    /// </summary>
    [Fact]
    public void Un_proveedor_desconocido_cae_a_Copilot_en_vez_de_dejar_sin_auditor()
    {
        Save(s => s.AuditorProvider = "un-proveedor-retirado");

        Registry().Current.ProviderId.Should().Be("copilot");
    }

    /// <summary>
    /// Los informes y las métricas leen sesiones de hace meses. Un identificador que esta versión
    /// ya no traiga tiene que poder nombrarse igual, en vez de salir en blanco.
    /// </summary>
    [Fact]
    public void Un_identificador_desconocido_se_sigue_pudiendo_nombrar()
    {
        AuditorProviderRegistry registry = Registry();

        registry.NameOf("copilot").Should().Be("GitHub Copilot");
        registry.NameOf("claude-code").Should().Be("Claude Code");
        registry.NameOf("proveedor-de-2027").Should().Be("proveedor-de-2027");
    }

    // ================================================================ 2 · queda escrito con quién se auditó

    [Fact]
    public async Task La_sesion_registra_el_proveedor_ademas_del_modelo()
    {
        WriteUnit("A.cs");
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });

        var agent = new NamedAgent("claude-code", "Claude Code", "opus");
        var coordinator = new SessionCoordinator(
            _hub, new FindingIngestionService(_hub, _ulids), new ReconciliationService(_hub),
            new MachineConfigStore(_paths.MachinesJson), _ulids, agent, _settings);

        await coordinator.RunAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        AuditSession session = _hub.Store.ListSessions("app").Single();
        session.Provider.Should().Be("claude-code");
        session.Model.Should().Be("opus", "el modelo ya se guardaba; el proveedor es lo que faltaba");
    }

    // ================================================================ 3 · los costes NO se mezclan

    /// <summary>
    /// <b>El coste del panel es la FACTURA, y solo eso</b> (F16-RETOQUE §1). Con las dos casas en
    /// el periodo, el total existe y es limpio: son los credits de Copilot. Lo de Claude Code no
    /// se suma —ni con etiqueta ni sin ella— porque no factura a nadie: va contra la suscripción
    /// de quien lo lanzó. Y no desaparece: sale en la actividad y el azulejo dice que está fuera.
    /// </summary>
    [Fact]
    public void El_coste_del_periodo_es_solo_lo_que_factura()
    {
        WriteSession("copilot", cost: 120m, unit: "unidades SDK");
        WriteSession("claude-code", cost: 0.35m, unit: "USD (tarifa de lista)");

        MetricsDashboard dashboard = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));

        dashboard.CostInPeriod.Should().NotBeNull("lo que factura sí tiene total, y es el de Copilot");
        dashboard.CostByProvider.Should()
            .ContainSingle("solo llega al desglose la casa que factura")
            .Which.ProviderName.Should().Be("GitHub Copilot");
        dashboard.CostLines.Should().Contain(l => l.Contains("GitHub Copilot") && l.Contains("AI credits"));

        dashboard.CostLines.Should().NotContain(l => l.Contains("Claude Code"));
        dashboard.CostLines.Should().NotContain(l => l.Contains("equivalente API"));

        // Y las suyas NO se cuentan como «parciales»: no les falta una tarifa, es que no llevan.
        dashboard.CostIsPartial.Should().BeFalse(
            "pedir una tarifa para lo que no factura es justo lo que no hay que hacer");
        dashboard.HasUntariffed.Should().BeTrue();
        dashboard.UntariffedNotice.Should().Contain("Claude Code").And.Contain("suscripción");
    }

    /// <summary>
    /// Y la sesión que no se tarifa <b>sigue viéndose</b>: en la actividad, con su proveedor y sus
    /// tokens. Retirarla del panel para «no contaminar el coste» sería esconder trabajo hecho.
    /// </summary>
    [Fact]
    public void La_sesion_que_no_se_tarifa_sale_en_la_actividad_con_su_proveedor_y_sus_tokens()
    {
        WriteSession("claude-code", cost: 0.35m, unit: "USD (tarifa de lista)");

        MetricsDashboard dashboard = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));

        SessionRow row = dashboard.Sessions.Should().ContainSingle().Subject;
        row.Provider.Should().Be("Claude Code");
        row.Billed.Should().BeFalse();
        row.Cost.Should().BeNull("no hay coste que enseñar, y un cero afirmaría que fue gratis");
        row.Tokens.Should().Contain("tokens");
        row.TokensDetail.Should().Contain("entrada").And.Contain("salida");
    }

    /// <summary>Con una sola casa, el total de siempre: no se rompe lo que ya funcionaba.</summary>
    [Fact]
    public void Con_un_solo_proveedor_el_total_sigue_siendo_un_numero()
    {
        WriteSession("copilot", cost: 100m, unit: "unidades SDK");
        WriteSession("copilot", cost: 50m, unit: "unidades SDK");

        MetricsDashboard dashboard = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));

        dashboard.CostInPeriod.Should().Be(150m);
        dashboard.CostUnit.Should().Be("credits", "la unidad es la que factura GitHub (F15)");
    }

    /// <summary>
    /// Las sesiones anteriores a F14 no llevan proveedor escrito, y eso NO es un dato que falte:
    /// era Copilot, porque no había otro. Tratarlas como «desconocido» partiría el histórico en dos
    /// justo en los hubs con más historia.
    /// </summary>
    [Fact]
    public void Las_sesiones_de_antes_de_F14_cuentan_como_Copilot()
    {
        WriteSession(provider: null, cost: 80m, unit: "unidades SDK");
        WriteSession("copilot", cost: 20m, unit: "unidades SDK");

        MetricsDashboard dashboard = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));

        dashboard.CostInPeriod.Should().Be(100m);
    }

    /// <summary>
    /// La estimación previa al lanzamiento no promedia entre casas: solo entran las sesiones del
    /// proveedor con el que se va a auditar.
    /// </summary>
    [Fact]
    public void La_estimacion_solo_promedia_las_sesiones_del_proveedor_que_va_a_auditar()
    {
        WriteSession("copilot", cost: 100m, unit: "unidades SDK", perUnitCost: 100m);
        WriteSession("copilot", cost: 100m, unit: "unidades SDK", perUnitCost: 100m);

        IReadOnlyList<AuditSession> sessions = _hub.Store.ListSessions("app");

        CostEstimate conCopilot = CostEstimator.Estimate(
            sessions, units: 2, maxPasses: 5, "copilot", TestRates.Table());

        conCopilot.CostPerUnit.Should().Be(100m);
        conCopilot.CostUnit.Should().Be("credits");
    }

    /// <summary>
    /// Y con una casa que NO factura no estima nada — y lo dice por su nombre (F16-RETOQUE §1).
    /// Antes se caía por el camino de «sin histórico suficiente», que manda a buscar unas medidas
    /// que no arreglarían nada porque esas sesiones no producen coste por definición.
    /// </summary>
    [Fact]
    public void Con_una_casa_que_no_factura_no_hay_coste_que_estimar()
    {
        WriteSession("claude-code", cost: 1m, unit: "USD (tarifa de lista)", perUnitCost: 1m);

        CostEstimate estimate = CostEstimator.Estimate(
            _hub.Store.ListSessions("app"), units: 2, maxPasses: 5, "claude-code", TestRates.Table());

        estimate.Billed.Should().BeFalse();
        estimate.Total.Should().BeNull();
        estimate.Breakdown.Should().Contain("sin coste para la organización");
        estimate.Provenance.Should().Contain("suscripción");
        estimate.IsWeak.Should().BeFalse("no hay número flojo que marcar: no hay número");
    }

    // ================================================================ 4 · el diálogo dice con quién

    /// <summary>
    /// El juez de la sesión no puede ser una sorpresa que se descubra leyendo el informe: va en el
    /// titular del diálogo, con su modelo.
    /// </summary>
    [Fact]
    public void El_dialogo_de_lanzamiento_dice_el_proveedor_y_el_modelo()
    {
        var confirmation = new AuditLaunchConfirmation(
            "XBLAST",
            CostEstimator.Estimate(Array.Empty<AuditSession>(), 2, 5),
            "Claude Code",
            "opus");

        confirmation.Headline.Should().Be("Vas a auditar 2 unidades de XBLAST con Claude Code (modelo opus).");
    }

    [Fact]
    public void Sin_modelo_resuelto_el_dialogo_no_se_inventa_uno()
    {
        var confirmation = new AuditLaunchConfirmation(
            "XBLAST", CostEstimator.Estimate(Array.Empty<AuditSession>(), 1, 5), "GitHub Copilot", null);

        confirmation.Headline.Should().Be("Vas a auditar 1 unidad de XBLAST con GitHub Copilot.");
    }

    /// <summary>
    /// Con Claude Code el diálogo no promete dinero ni lo desmiente con una nota al pie: dice
    /// directamente que ese consumo no factura a la organización (F16-RETOQUE §1). La salvedad de
    /// la «tarifa de lista» existía para explicar un número; sin número no hay nada que explicar.
    /// </summary>
    [Fact]
    public void Con_Claude_Code_el_dialogo_dice_que_no_hay_coste_que_estimar()
    {
        var confirmation = new AuditLaunchConfirmation(
            "XBLAST",
            CostEstimator.Estimate(Array.Empty<AuditSession>(), 2, 5, "claude-code"),
            "Claude Code",
            "opus");

        confirmation.Breakdown.Should().Contain("sin coste para la organización");
        confirmation.Provenance.Should().Contain("suscripción");
        confirmation.IsWeakEstimate.Should().BeFalse();
        confirmation.Reassurance.Should().NotContain("tarifa de lista");
    }

    // ================================================================ 5 · Cuenta enseña los dos

    /// <summary>
    /// GitHub NO se sustituye nunca: sus tres filas van primero, y solo después una por proveedor.
    /// Que la pantalla sugiriera lo contrario sería mentir sobre lo que hace falta para trabajar.
    /// </summary>
    [Fact]
    public void Cuenta_enseña_GitHub_primero_y_luego_un_auditor_por_proveedor()
    {
        var checker = new ConnectionChecker(
            TestFactory.Account(_paths),
            new GitHubApiClient(new System.Net.Http.HttpClient()),
            DeployConfig.Load(),
            _hub,
            Registry());

        checker.Steps.Select(s => s.Key).Should().Equal(
            "auth", "org", "hub", "provider:copilot", "provider:claude-code");

        checker.Steps.Last().Title.Should().Be("Claude Code disponible");
    }

    // ================================================================ 6 · Claude Code es OPCIONAL

    /// <summary>
    /// <b>La regla, entera y en un solo test.</b> Sin el CLI de Claude en la máquina —que es la
    /// situación de todo el equipo hoy— la aplicación se comporta <b>exactamente como antes de que
    /// existiera</b>: Cuenta informa sin alertar, Ajustes ofrece solo Copilot, y ningún flujo se
    /// degrada.
    /// <para>
    /// Es una regla de producto y no un detalle de interfaz: Copilot es el requisito del equipo y
    /// Claude Code un extra que cada uno activa si quiere. Convertir en deuda de cada usuario algo
    /// que nadie le ha pedido es la forma más rápida de que un aviso legítimo deje de leerse.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sin_Claude_Code_instalado_la_aplicacion_no_reclama_nada_y_no_pierde_nada()
    {
        AuditorProviderRegistry registry = RegistryWithoutClaude();

        // --- 1. Cuenta INFORMA, no alerta -------------------------------------------------
        var checker = new ConnectionChecker(
            TestFactory.Account(_paths),
            new GitHubApiClient(new System.Net.Http.HttpClient()),
            DeployConfig.Load(),
            _hub,
            registry);

        await checker.RunAsync(CancellationToken.None);

        ConnectionStep fila = checker.Steps.Single(s => s.Key == "provider:claude-code");

        fila.State.Should().Be(CheckState.Optional,
            "un extra que no se ha activado no es un fallo, y no se pinta como tal");
        fila.State.Should().NotBe(CheckState.Failed);
        fila.IsVisible.Should().BeTrue("se enseña: la gracia es que quien lo quiera sepa que existe");
        fila.HelpUrl.Should().BeNull("no hay nada que ir a arreglar");
        fila.Detail.Should().Contain("no hace falta");
        fila.Title.Should().Contain("opcional");

        // --- 2. Ajustes ofrece SOLO Copilot como seleccionable ----------------------------
        registry.Selectable.Should().ContainSingle()
            .Which.ProviderId.Should().Be("copilot");

        SettingsViewModel ajustes = Settings(registry);

        ajustes.Providers.Select(p => p.Id).Should().Equal("copilot");
        ajustes.HasProviderChoice.Should().BeFalse(
            "con una sola opción no hay nada que elegir: el selector ni se enseña");

        // --- 3. Ningún flujo se degrada ---------------------------------------------------
        registry.Current.ProviderId.Should().Be("copilot");

        // Y ni siquiera con el ajuste apuntando a Claude: un ajuste guardado hace semanas no puede
        // dejar a nadie sin poder auditar hoy.
        Save(s => s.AuditorProvider = "claude-code");
        registry.Current.ProviderId.Should().Be("copilot",
            "si el opcional ya no está, se vuelve al de fábrica en vez de romper la sesión");
    }

    /// <summary>
    /// Y a un proveedor opcional ausente ni siquiera se le pregunta: lanzar su proceso para
    /// confirmar lo que ya sabemos sería gasto por nada, y en una pantalla que se abre a menudo.
    /// </summary>
    [Fact]
    public async Task A_un_proveedor_opcional_ausente_no_se_le_pregunta_siquiera()
    {
        var claude = new NamedAgent("claude-code", "Claude Code", "opus")
        {
            Optional = true,
            Present = false,
        };

        var checker = new ConnectionChecker(
            TestFactory.Account(_paths),
            new GitHubApiClient(new System.Net.Http.HttpClient()),
            DeployConfig.Load(),
            _hub,
            new AuditorProviderRegistry(_settings, new IAuditorProvider[]
            {
                new NamedAgent(RealCopilotAgent.Id, "GitHub Copilot", "gpt-x"),
                claude,
            }));

        await checker.RunAsync(CancellationToken.None);

        claude.Checks.Should().Be(0, "no se lanza un proceso para confirmar lo que ya se sabe");
    }

    /// <summary>
    /// Con el CLI presente, en cambio, el extra se ofrece con normalidad: la regla quita la
    /// exigencia, no la capacidad.
    /// </summary>
    [Fact]
    public void Con_Claude_Code_instalado_el_extra_se_ofrece_con_normalidad()
    {
        AuditorProviderRegistry registry = Registry();

        registry.Selectable.Select(p => p.ProviderId).Should().Equal("copilot", "claude-code");
        Settings(registry).HasProviderChoice.Should().BeTrue();
    }

    /// <summary>Copilot NO es opcional: es el requisito, y su fallo sí es un fallo.</summary>
    [Fact]
    public void Copilot_no_es_opcional()
    {
        new NamedAgent(RealCopilotAgent.Id, "GitHub Copilot", "gpt-x").IsOptional.Should().BeFalse();
        new ClaudeCodeProvider("puente.exe", locator: () => null).IsOptional.Should().BeTrue();
    }

    // ================================================================ ayudas

    /// <summary>Lo que tiene HOY todo el equipo: Copilot, y Claude Code sin instalar.</summary>
    private AuditorProviderRegistry RegistryWithoutClaude()
        => new(_settings, new IAuditorProvider[]
        {
            new NamedAgent(RealCopilotAgent.Id, "GitHub Copilot", "gpt-x"),
            new NamedAgent(ClaudeCodeProvider.Id, "Claude Code", "opus") { Optional = true, Present = false },
        });

    private SettingsViewModel Settings(AuditorProviderRegistry registry)
        => new(
            _settings,
            registry.Fallback,
            new ToastCenter(),
            new FactoryResetService(
                _hub, _paths, _settings, TestFactory.Account(_paths), new OpenSessionStore(_paths)),
            new NeverResets(),
            _hub,
            new NavigationService(new NoServices()),
            about: null,
            deploy: null,
            providers: registry);

    private sealed class NeverResets : IFactoryResetConfirmer
    {
        public bool Confirm(FactoryResetConfirmation confirmation) => false;
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private AuditorProviderRegistry Registry()
        => new(_settings, new IAuditorProvider[]
        {
            new NamedAgent(RealCopilotAgent.Id, "GitHub Copilot", "gpt-x"),
            new NamedAgent(ClaudeCodeProvider.Id, "Claude Code", "opus"),
        });

    private void Save(Action<AppSettings> change)
    {
        AppSettings s = _settings.Current;
        change(s);
        _settings.Save(s);
    }

    private void WriteUnit(string relative)
    {
        string clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(clone, "u/app");
        new MachineConfigStore(_paths.MachinesJson).SetClonePath("app", clone);
        File.WriteAllText(Path.Combine(clone, relative), "// unidad\nclass A { }\n");
    }

    private void WriteSession(string? provider, decimal cost, string unit, decimal? perUnitCost = null)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alvaro",
            Machine = "PC",
            StartedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            EndedUtc = DateTimeOffset.UtcNow,
            CycleN = 1,
            Provider = provider,
            MaxPassesPerUnit = 5,
        };

        session.Units.Add(new UnitVerdictRecord("src/U.cs", "src", "auditada", null));
        TestRates.CostAs(session, cost, inputTokens: 1000);
        session.Usage.Currency = unit;
        session.UsageBreakdown.Add(new UnitUsageBreakdown
        {
            Unit = "src/U.cs",
            OutputTokens = TestRates.OutputFor(perUnitCost ?? cost),
        });

        _hub.Store.WriteSession(session);
    }

    /// <summary>Un agente que solo aporta su identidad: es lo que estos tests miran.</summary>
    private sealed class NamedAgent : IAuditorProvider
    {
        private readonly string _model;

        public NamedAgent(string id, string name, string model)
        {
            ProviderId = id;
            ProviderName = name;
            _model = model;
        }

        public string ProviderId { get; }

        public string ProviderName { get; }

        /// <summary>Si este doble hace de extra opcional.</summary>
        public bool Optional { get; init; }

        /// <summary>Si está en la máquina. Un opcional ausente no debe interrogarse.</summary>
        public bool Present { get; init; } = true;

        public bool IsOptional => Optional;

        public bool IsPresent => Present;

        /// <summary>Cuántas veces se le ha preguntado por su estado. Debe ser 0 si es opcional y no está.</summary>
        public int Checks { get; private set; }

        public string? ModelName => _model;

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
        {
            Checks++;
            return Task.FromResult(new AgentReadiness(true, $"{ProviderName} listo."));
        }

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(new[] { new AgentModel(_model, _model) });

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke("mirando");
            UsageReported?.Invoke(new UsageSample(10, 5, null, _model));
            toolbox.UnitDone(request.UnitPath, "sin defectos");
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }
}
