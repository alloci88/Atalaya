using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
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
/// F5.10 — silencio con alcance. Lo que se comprueba aquí es lo que hace que la exclusión sea
/// ESTRUCTURAL y no cosmética: que un hallazgo de una regla excluida no entra aunque el auditor lo
/// reporte, que la regla se retira del brief, que la caducidad la devuelve al juego, que el
/// silencio en masa deja traza hallazgo por hallazgo, y —el que protege el concepto entero— que
/// excluir en una aplicación no ciega a la de al lado.
/// </summary>
public sealed class RuleExclusionTests : IDisposable
{
    private const string Rule = "mejoras.estilo.nomenclatura";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly SettingsService _settings;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly GovernanceService _governance;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public RuleExclusionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-excl", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);
        _governance = new GovernanceService(_hub, _ulids);

        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
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
        catch (IOException)
        {
            // best effort
        }
    }

    // ------------------------------------------------------------------ arnés

    /// <summary>Una app con su clon, su inventario de una unidad y un fichero que auditar.</summary>
    private string SeedApp(string slug)
    {
        string clone = Path.Combine(_root, slug);
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "A.cs"), $"class {slug} {{ void M() {{ }} }}");
        _machines.SetClonePath(slug, clone);
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = slug,
            Name = slug.ToUpperInvariant(),
            RepoUrl = "u",
            Stack = TechStack.DotNet,
            CurrentCycle = 1,
        });
        _hub.Store.WriteInventory(slug, new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });
        return clone;
    }

    private static SubmitFindingArgs Payload(string ruleId = Rule, string title = "nombre poco claro")
        => new(ruleId, "mejoras", "baja", title, "desc", "impact", "reco",
            new[] { new SubmitLocation("A.cs", 1, "class") }, "M");

    private Task<SessionResult> Audit(string slug, params SubmitFindingArgs[] reported)
        => new SessionCoordinator(
                _hub, _ingestion, _reconciliation, _machines, _ulids,
                new FakeCopilotAgent(auditScript: _ => reported), _settings)
            .RunAsync(new SessionRequest(slug, AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

    private void Exclude(string slug, DateTimeOffset? expires = null, bool silenceExisting = false)
        => _governance.ExcludeRule(slug, Rule, SilenceReason.DeudaAceptada, "no aplica aquí", expires, silenceExisting);

    private Finding SeedFinding(string slug, string ruleId = Rule, string title = "existente")
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, "old", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = ruleId,
            Pillar = Pillar.Mejoras,
            Severity = Severity.Baja,
            Confidence = Confidence.Media,
            Title = title,
            Locations = { new Location("A.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding(slug, f);
        return f;
    }

    // ================================================================== §1 modelo

    [Fact]
    public void La_exclusion_vive_en_su_propio_fichero_por_app()
    {
        SeedApp("alpha");
        Exclude("alpha");

        string file = _hub.HubPaths.RuleExclusionFile("alpha", Rule);
        File.Exists(file).Should().BeTrue("un fichero por regla, como todo el hub");
        Path.GetDirectoryName(file).Should().EndWith(Path.Combine("alpha", "rule-exclusions"));

        RuleExclusion? read = _hub.Store.TryReadRuleExclusion("alpha", Rule);
        read.Should().NotBeNull();
        read!.Reason.Should().Be(SilenceReason.DeudaAceptada);
        read.Notes.Should().Be("no aplica aquí");
        read.By.Should().NotBeNullOrWhiteSpace("excluir es una acción humana con autor");
        read.Utc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    /// <summary>
    /// Un <c>ruleId</c> con separadores de ruta no se convierte en un nombre de fichero: lo
    /// escribiría fuera de la carpeta y la exclusión no suprimiría nada donde se la busca.
    /// </summary>
    [Theory]
    [InlineData("../../escapa")]
    [InlineData("con/barra")]
    [InlineData("con\\contrabarra")]
    [InlineData("")]
    public void Un_ruleId_que_no_vale_como_fichero_se_rechaza(string ruleId)
    {
        SeedApp("alpha");
        Action act = () => _governance.ExcludeRule(
            "alpha", ruleId, SilenceReason.Otro, null, null, silenceExisting: false);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Una_exclusion_caducada_es_inexistente_para_filtrar_y_visible_para_revisar()
    {
        var past = new RuleExclusion
        {
            RuleId = Rule,
            By = "alvaro",
            Utc = DateTimeOffset.UtcNow.AddDays(-10),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        RuleExclusionSet live = RuleExclusionSet.From(new[] { past }, DateTimeOffset.UtcNow);

        live.Excludes(Rule).Should().BeFalse("caducada = inexistente a efectos de filtrado");
        past.IsExpiredAt(DateTimeOffset.UtcNow).Should().BeTrue("la UI la sigue listando como «caducada — revisar»");
    }

    // ================================================================== §2 ingestión

    [Fact]
    public async Task Una_regla_excluida_se_suprime_en_la_ingestion_y_no_crea_hallazgo()
    {
        SeedApp("alpha");
        Exclude("alpha");

        SessionResult result = await Audit("alpha", Payload());

        _hub.Store.ListFindings("alpha").Should().BeEmpty("suprimido = no entra, ni como hallazgo silenciado");
        result.Counters.New.Should().Be(0);
        result.Counters.SuppressedByRule.Should().Be(1);
        result.Counters.Rejected.Should().Be(0, "el payload era válido: suprimir no es rechazar");
    }

    [Fact]
    public async Task El_informe_cuenta_los_suprimidos_y_dice_cuales_fueron()
    {
        SeedApp("alpha");
        Exclude("alpha");

        SessionResult result = await Audit("alpha", Payload(title: "nombre críptico"));

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("alpha", result.SessionId.ToString()));
        report.Should().Contain("Suprimidos por regla: 1");
        report.Should().Contain("Detecciones suprimidas por exclusión de regla");
        report.Should().Contain(Rule).And.Contain("nombre críptico");
        report.Should().NotContain("Payloads rechazados");
    }

    [Fact]
    public async Task Lo_que_no_esta_excluido_sigue_entrando_en_la_misma_sesion()
    {
        SeedApp("alpha");
        Exclude("alpha");

        SessionResult result = await Audit(
            "alpha", Payload(), Payload("errores.null.desreferencia", "posible nulo"));

        result.Counters.SuppressedByRule.Should().Be(1);
        _hub.Store.ListFindings("alpha").Should().ContainSingle()
            .Which.RuleId.Should().Be("errores.null.desreferencia");
    }

    [Fact]
    public async Task La_caducidad_reactiva_la_regla()
    {
        SeedApp("alpha");
        Exclude("alpha", expires: DateTimeOffset.UtcNow.AddDays(-1));

        SessionResult result = await Audit("alpha", Payload());

        result.Counters.SuppressedByRule.Should().Be(0);
        _hub.Store.ListFindings("alpha").Should().ContainSingle("la exclusión caducó: la regla vuelve al juego");
    }

    /// <summary>
    /// El invariante que sostiene todo el concepto: la exclusión es POR-APLICACIÓN. Una regla que
    /// sobra en una app puede ser vital en la de al lado, y por eso no hay exclusiones globales.
    /// </summary>
    [Fact]
    public async Task Excluir_en_una_app_no_afecta_a_otra()
    {
        SeedApp("alpha");
        SeedApp("beta");
        Exclude("alpha");

        SessionResult inAlpha = await Audit("alpha", Payload());
        SessionResult inBeta = await Audit("beta", Payload());

        inAlpha.Counters.SuppressedByRule.Should().Be(1);
        _hub.Store.ListFindings("alpha").Should().BeEmpty();

        inBeta.Counters.SuppressedByRule.Should().Be(0);
        _hub.Store.ListFindings("beta").Should().ContainSingle()
            .Which.RuleId.Should().Be(Rule);
    }

    /// <summary>
    /// Las áreas de criterio no se pueden retirar del brief —son juicio libre— pero SÍ se suprimen
    /// si alguien las excluye explícitamente. La exclusión se respeta como filtro de entrada.
    /// </summary>
    [Fact]
    public async Task Un_criterio_excluido_explicitamente_se_suprime_aunque_siga_en_el_brief()
    {
        SeedApp("alpha");
        _governance.ExcludeRule(
            "alpha", "criterio.observabilidad", SilenceReason.DecisionArquitectonica, "sin telemetría",
            null, silenceExisting: false);

        SessionResult result = await Audit("alpha", Payload("criterio.observabilidad", "sin logs"));

        result.Counters.SuppressedByRule.Should().Be(1);
        _hub.Store.ListFindings("alpha").Should().BeEmpty();
    }

    /// <summary>
    /// Suprimir no alarga el barrido. Si contase como trabajo, una app con una regla excluida y un
    /// auditor tozudo agotaría el tope de pasadas sin producir nada.
    /// </summary>
    [Fact]
    public async Task Una_pasada_que_solo_suprime_queda_seca()
    {
        SeedApp("alpha");
        Exclude("alpha");
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 3;
        _settings.Save(s);

        SessionResult result = await Audit("alpha", Payload());

        AuditSession session = _hub.Store.ListSessions("alpha").Single();
        session.Units.Single().Passes!.Should().ContainSingle("la primera pasada ya quedó seca");
        session.Units.Single().Verdict.Should().Be("auditada");
        result.Counters.SuppressedByRule.Should().Be(1);
    }

    // ================================================================== §2 brief

    [Fact]
    public async Task La_regla_excluida_no_viaja_en_el_prompt_de_la_unidad()
    {
        SeedApp("alpha");
        Exclude("alpha");

        var prompts = new List<string>();
        var agent = new FakeCopilotAgent(auditScript: r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        });

        await new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest("alpha", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

        prompts.Should().ContainSingle();
        prompts[0].Should().NotContain(Rule, "no se pide lo que se va a tirar");
        prompts[0].Should().Contain("errores.null.desreferencia", "el resto del catálogo sigue entero");
    }

    // ================================================================== §2 existentes

    [Fact]
    public void El_silencio_en_masa_registra_history_por_hallazgo()
    {
        SeedApp("alpha");
        Finding a = SeedFinding("alpha", title: "uno");
        Finding b = SeedFinding("alpha", title: "dos");
        Finding otra = SeedFinding("alpha", "errores.null.desreferencia", "de otra regla");

        GovernanceService.RuleExclusionResult result = _governance.ExcludeRule(
            "alpha", Rule, SilenceReason.DeudaAceptada, "no aplica aquí", null, silenceExisting: true);

        result.SilencedFindings.Should().Be(2);
        foreach (Finding f in new[] { a, b })
        {
            Finding after = _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;
            after.Status.Should().Be(FindingStatus.Silenciado);
            after.History.Should().Contain(h => h.Event == FindingEvent.Silenced
                                                && h.Detail!.Contains(Rule));

            Silence? silence = _hub.Store.TryReadSilence("alpha", f.Id);
            silence.Should().NotBeNull();
            silence!.ByRuleExclusion.Should().Be(Rule, "la ficha tiene que poder decir de dónde vino");
            silence.Reason.Should().Be(SilenceReason.DeudaAceptada);
        }

        _hub.Store.TryReadFinding("alpha", otra.Id.ToString())!.Status
            .Should().Be(FindingStatus.Activo, "otra regla no se toca");
    }

    [Fact]
    public void Sin_silencio_en_masa_los_existentes_siguen_activos()
    {
        SeedApp("alpha");
        Finding a = SeedFinding("alpha");

        _governance.ExcludeRule("alpha", Rule, SilenceReason.Otro, null, null, silenceExisting: false)
            .SilencedFindings.Should().Be(0);

        _hub.Store.TryReadFinding("alpha", a.Id.ToString())!.Status.Should().Be(FindingStatus.Activo);
        _hub.Store.TryReadSilence("alpha", a.Id).Should().BeNull();
    }

    /// <summary>
    /// Reconciliación (§4): un hallazgo silenciado por exclusión sigue apareciéndole al auditor
    /// mientras esté activo-silenciado, para que no lo re-reporte como nuevo.
    /// </summary>
    [Fact]
    public void Un_hallazgo_silenciado_por_regla_sigue_en_la_lista_de_existentes()
    {
        SeedApp("alpha");
        Finding a = SeedFinding("alpha");
        _governance.ExcludeRule("alpha", Rule, SilenceReason.Otro, null, null, silenceExisting: true);

        _reconciliation.ExistingForUnit("alpha", "A.cs").Should().ContainSingle()
            .Which.Id.Should().Be(a.Id);
    }

    [Fact]
    public void Des_excluir_devuelve_la_regla_al_juego_sin_des_silenciar_lo_ya_decidido()
    {
        SeedApp("alpha");
        Finding a = SeedFinding("alpha");
        _governance.ExcludeRule("alpha", Rule, SilenceReason.Otro, null, null, silenceExisting: true);

        _governance.UnexcludeRule("alpha", Rule).Should().BeTrue();

        _hub.Store.TryReadRuleExclusion("alpha", Rule).Should().BeNull();
        _hub.Store.TryReadFinding("alpha", a.Id.ToString())!.Status
            .Should().Be(FindingStatus.Silenciado, "cada silencio fue una decisión registrada: se levanta desde su ficha");
    }

    [Fact]
    public async Task Des_excluir_hace_que_la_regla_vuelva_a_reportarse()
    {
        SeedApp("alpha");
        Exclude("alpha");
        (await Audit("alpha", Payload())).Counters.SuppressedByRule.Should().Be(1);

        _governance.UnexcludeRule("alpha", Rule);

        (await Audit("alpha", Payload())).Counters.New.Should().Be(1);
        _hub.Store.ListFindings("alpha").Should().ContainSingle().Which.RuleId.Should().Be(Rule);
    }

    // ================================================================== §3 ficha

    private FindingDetailViewModel Detail(TestFactory.RecordingExcludeConfirmer confirmer)
        => new(
            _hub, _governance, _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines), _toasts,
            TestFactory.Links(_hub, _paths), TestFactory.LinkFlow(_hub, _paths, _toasts),
            confirmer);

    [Fact]
    public void El_selector_de_alcance_dice_la_consecuencia_de_cada_opcion()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        FindingDetailViewModel vm = Detail(new TestFactory.RecordingExcludeConfirmer());
        vm.Load("alpha", f.Id);

        vm.SilenceScope.Should().Be(SilenceScope.Hallazgo, "el gesto de todos los días es el de por defecto");
        vm.ScopeOptions.Should().HaveCount(2);
        vm.ScopeOptions[0].Label.Should().Be("Solo este hallazgo");
        vm.ScopeOptions[0].Consequence.Should().Contain("La regla sigue vigente");
        vm.ScopeOptions[1].Label.Should().Contain(Rule);
        vm.ScopeOptions[1].Consequence.Should().Be($"Ninguna auditoría de ALPHA volverá a reportar {Rule}.");
    }

    [Fact]
    public void Elegir_el_alcance_de_regla_cambia_el_boton()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        FindingDetailViewModel vm = Detail(new TestFactory.RecordingExcludeConfirmer());
        vm.Load("alpha", f.Id);

        vm.SilenceActionLabel.Should().Be("Silenciar");
        vm.ScopeOptions[1].IsSelected = true;
        vm.SilenceScope.Should().Be(SilenceScope.Regla);
        vm.SilenceActionLabel.Should().Be("Excluir la regla");
        vm.ScopeOptions[0].IsSelected.Should().BeFalse("son excluyentes");
    }

    [Fact]
    public void Excluir_desde_la_ficha_pregunta_por_los_existentes_y_puede_silenciarlos()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        SeedFinding("alpha", title: "otro de la misma regla");

        var confirmer = new TestFactory.RecordingExcludeConfirmer(ExcludeRuleChoice.ExcludeAndSilence);
        FindingDetailViewModel vm = Detail(confirmer);
        vm.Load("alpha", f.Id);
        vm.SilenceScope = SilenceScope.Regla;
        vm.SilenceNotes = "no hay i18n en este proyecto";
        vm.SilenceCommand.Execute(null);

        confirmer.Asked.Should().ContainSingle();
        confirmer.Asked[0].ActiveFindings.Should().Be(2);
        confirmer.Asked[0].Question.Should().Contain("2 hallazgos activos").And.Contain("ALPHA");
        confirmer.Asked[0].Consequence.Should().Be($"Ninguna auditoría de ALPHA volverá a reportar {Rule}.");

        _hub.Store.TryReadRuleExclusion("alpha", Rule).Should().NotBeNull();
        _hub.Store.ListFindings("alpha").Should().OnlyContain(x => x.Status == FindingStatus.Silenciado);
        _toasts.Items.Last().Text.Should().Contain("2 hallazgo(s) silenciado(s)");
    }

    [Fact]
    public void Decir_que_no_excluye_y_deja_los_existentes_activos()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");

        var confirmer = new TestFactory.RecordingExcludeConfirmer(ExcludeRuleChoice.ExcludeOnly);
        FindingDetailViewModel vm = Detail(confirmer);
        vm.Load("alpha", f.Id);
        vm.SilenceScope = SilenceScope.Regla;
        vm.SilenceCommand.Execute(null);

        _hub.Store.TryReadRuleExclusion("alpha", Rule).Should().NotBeNull();
        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!.Status.Should().Be(FindingStatus.Activo);
        _toasts.Items.Last().Text.Should().Contain("siguen activos");
    }

    [Fact]
    public void Cancelar_la_pregunta_no_toca_nada()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");

        FindingDetailViewModel vm = Detail(new TestFactory.RecordingExcludeConfirmer(ExcludeRuleChoice.Cancel));
        vm.Load("alpha", f.Id);
        vm.SilenceScope = SilenceScope.Regla;
        vm.SilenceCommand.Execute(null);

        _hub.Store.TryReadRuleExclusion("alpha", Rule).Should().BeNull();
        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!.Status.Should().Be(FindingStatus.Activo);
        _toasts.Items.Last().Text.Should().Contain("no se ha tocado nada");
    }

    /// <summary>Sin hallazgos activos no hay nada que preguntar: el diálogo no aparece.</summary>
    [Fact]
    public void Sin_hallazgos_activos_de_la_regla_no_se_pregunta()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        _governance.Silence("alpha", f.Id, SilenceReason.FalsoPositivo, null, null);

        var confirmer = new TestFactory.RecordingExcludeConfirmer(ExcludeRuleChoice.Cancel);
        FindingDetailViewModel vm = Detail(confirmer);
        vm.Load("alpha", f.Id);
        vm.SilenceScope = SilenceScope.Regla;
        vm.SilenceCommand.Execute(null);

        confirmer.Asked.Should().BeEmpty();
        _hub.Store.TryReadRuleExclusion("alpha", Rule).Should().NotBeNull();
    }

    /// <summary>
    /// El camino natural: silencias un falso positivo, ves que se repite por toda la app y vuelves
    /// a esa misma ficha a apagar la regla. Con el botón atado a <c>CanSilence</c> te encontrabas
    /// el selector pintado y nada que pulsar.
    /// </summary>
    [Fact]
    public void Sobre_un_hallazgo_ya_silenciado_se_puede_excluir_la_regla()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        _governance.Silence("alpha", f.Id, SilenceReason.FalsoPositivo, "aquí no", null);

        FindingDetailViewModel vm = Detail(new TestFactory.RecordingExcludeConfirmer());
        vm.Load("alpha", f.Id);

        vm.CanSilence.Should().BeFalse("silenciar lo ya silenciado no hace nada");
        vm.CanApplySilence.Should().BeFalse("con el alcance por defecto el botón sigue retirado");

        vm.SilenceScope = SilenceScope.Regla;
        vm.CanApplySilence.Should().BeTrue("excluir la regla sí hace algo sobre un silenciado");

        vm.SilenceCommand.Execute(null);
        _hub.Store.TryReadRuleExclusion("alpha", Rule).Should().NotBeNull();
    }

    [Fact]
    public void Sobre_un_hallazgo_activo_el_boton_esta_en_los_dos_alcances()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        FindingDetailViewModel vm = Detail(new TestFactory.RecordingExcludeConfirmer());
        vm.Load("alpha", f.Id);

        vm.CanApplySilence.Should().BeTrue();
        vm.SilenceScope = SilenceScope.Regla;
        vm.CanApplySilence.Should().BeTrue();
    }

    [Fact]
    public void La_ficha_dice_que_el_silencio_vino_de_una_exclusion_de_regla()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        _governance.ExcludeRule("alpha", Rule, SilenceReason.DeudaAceptada, null, null, silenceExisting: true);

        FindingDetailViewModel vm = Detail(new TestFactory.RecordingExcludeConfirmer());
        vm.Load("alpha", f.Id);

        vm.SilenceSummary.Should().StartWith($"Silenciado por exclusión de regla ({Rule}, por ");
    }

    [Fact]
    public void Un_silencio_normal_sigue_diciendo_lo_de_siempre()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        _governance.Silence("alpha", f.Id, SilenceReason.FalsoPositivo, "aquí no", null);

        FindingDetailViewModel vm = Detail(new TestFactory.RecordingExcludeConfirmer());
        vm.Load("alpha", f.Id);

        vm.SilenceSummary.Should().StartWith("Silenciado por ").And.NotContain("exclusión de regla");
    }

    // ================================================================== §3 gestión

    private RuleExclusionsViewModel Manage(string slug)
    {
        var vm = new RuleExclusionsViewModel(_hub, _governance, _toasts);
        vm.Load(slug);
        return vm;
    }

    [Fact]
    public void La_gestion_lista_la_regla_con_su_motivo_autor_caducidad_y_estado()
    {
        SeedApp("alpha");
        Exclude("alpha", expires: DateTimeOffset.UtcNow.AddDays(30));

        RuleExclusionsViewModel vm = Manage("alpha");

        vm.IsEmpty.Should().BeFalse();
        RuleExclusionRow row = vm.Rows.Single();
        row.RuleId.Should().Be(Rule);
        row.Reason.Should().Be("Deuda aceptada");
        row.Notes.Should().Be("no aplica aquí");
        row.By.Should().NotBeNullOrWhiteSpace();
        row.State.Should().Be("activa");
        row.Expiry.Should().NotBe("permanente");
    }

    [Fact]
    public void Una_exclusion_caducada_se_lista_diciendo_que_hay_que_revisarla()
    {
        SeedApp("alpha");
        Exclude("alpha", expires: DateTimeOffset.UtcNow.AddDays(-2));

        RuleExclusionRow row = Manage("alpha").Rows.Single();

        row.Expired.Should().BeTrue();
        row.State.Should().Be("caducada — revisar");
        row.Effect.Should().Contain("vuelve a reportarse");
    }

    [Fact]
    public void Des_excluir_desde_la_gestion_retira_la_regla()
    {
        SeedApp("alpha");
        Exclude("alpha");
        RuleExclusionsViewModel vm = Manage("alpha");

        vm.UnexcludeCommand.Execute(vm.Rows.Single());

        vm.Rows.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
        _hub.Store.TryReadRuleExclusion("alpha", Rule).Should().BeNull();
    }

    [Fact]
    public void Editar_la_caducidad_la_escribe_donde_la_ingestion_la_lee()
    {
        SeedApp("alpha");
        Exclude("alpha");
        RuleExclusionsViewModel vm = Manage("alpha");

        vm.Rows.Single().ExpiryDays = 7;
        vm.ApplyExpiryCommand.Execute(vm.Rows.Single());

        RuleExclusion after = _hub.Store.TryReadRuleExclusion("alpha", Rule)!;
        after.ExpiresUtc.Should().NotBeNull();
        after.ExpiresUtc!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromMinutes(2));
        after.Reason.Should().Be(SilenceReason.DeudaAceptada, "editar la caducidad no reescribe el motivo");
        after.Notes.Should().Be("no aplica aquí");
    }

    [Fact]
    public void Poner_cero_dias_devuelve_la_exclusion_a_permanente()
    {
        SeedApp("alpha");
        Exclude("alpha", expires: DateTimeOffset.UtcNow.AddDays(-1));
        RuleExclusionsViewModel vm = Manage("alpha");

        vm.Rows.Single().ExpiryDays = 0;
        vm.ApplyExpiryCommand.Execute(vm.Rows.Single());

        _hub.Store.TryReadRuleExclusion("alpha", Rule)!.ExpiresUtc.Should().BeNull();
        Manage("alpha").Rows.Single().State.Should().Be("activa");
    }

    [Fact]
    public void Una_caducidad_negativa_no_se_aplica_y_se_dice()
    {
        SeedApp("alpha");
        Exclude("alpha");
        RuleExclusionsViewModel vm = Manage("alpha");

        vm.Rows.Single().ExpiryDays = -3;
        vm.ApplyExpiryCommand.Execute(vm.Rows.Single());

        _hub.Store.TryReadRuleExclusion("alpha", Rule)!.ExpiresUtc.Should().BeNull();
        _toasts.Items.Last().Text.Should().Contain("no puede ser negativa");
    }

    [Fact]
    public void La_gestion_de_una_app_no_ve_las_exclusiones_de_otra()
    {
        SeedApp("alpha");
        SeedApp("beta");
        Exclude("alpha");

        Manage("alpha").Rows.Should().ContainSingle();
        Manage("beta").Rows.Should().BeEmpty();
    }
}
