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
/// F5.12 — silencio por patrón. Lo que se comprueba aquí es lo que hace que el silenciado por tipo
/// funcione SIN taxonomía: que el ejemplar llega al prompt de la unidad, que lo que el auditor
/// declara haberse callado se cuenta y se nombra en la sesión y en el informe, que la caducidad lo
/// retira del prompt, que cada patrón acumula cuánto trabaja, que la gestión afina la frase sin
/// tocar ningún catálogo, y —el que protege el concepto entero— que silenciar en una aplicación no
/// calla a la de al lado.
/// </summary>
public sealed class PatternSilenceTests : IDisposable
{
    private const string Exemplar = "bloques catch vacíos que ocultan excepciones";

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

    public PatternSilenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-pattern", Guid.NewGuid().ToString("N"));
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

    private static SubmitFindingArgs Payload(
        string ruleId = "mejoras.estilo.nomenclatura", string title = "nombre poco claro")
        => new(ruleId, "mejoras", "baja", title, "desc", "impact", "reco",
            new[] { new SubmitLocation("A.cs", 1, "class") }, "M");

    private Task<SessionResult> Audit(string slug, params SubmitFindingArgs[] reported)
        => Run(slug, new FakeCopilotAgent(auditScript: _ => reported));

    /// <summary>Una auditoría en la que el auditor DECLARA haberse callado lo que se le pidió.</summary>
    private Task<SessionResult> AuditSuppressing(string slug, params (string PatternId, int Count)[] suppressed)
        => Run(slug, new FakeCopilotAgent(
            suppressScript: _ => suppressed.Select(s => new SuppressedByPatternArgs(s.PatternId, s.Count))));

    private Task<SessionResult> Run(string slug, IAuditorProvider agent)
        => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest(slug, AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

    private Finding SeedFinding(
        string slug, string ruleId = "mejoras.estilo.nomenclatura", string title = "existente",
        string? symbol = "M")
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
            Symbol = symbol,
            Locations = { new Location("A.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding(slug, f);
        return f;
    }

    /// <summary>Silencia un patrón desde un hallazgo recién sembrado y devuelve el patrón.</summary>
    private PatternSilence SeedPattern(
        string slug, string exemplar = Exemplar, DateTimeOffset? expires = null, Finding? source = null)
        => _governance.SilencePattern(
            slug, (source ?? SeedFinding(slug, title: "origen")).Id, exemplar,
            SilenceReason.DeudaAceptada, "no aplica aquí", expires).Pattern;

    // ================================================================== §1 modelo

    [Fact]
    public void El_patron_vive_en_su_propio_fichero_por_app()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");

        string file = _hub.HubPaths.PatternSilenceFile("alpha", pattern.Id.ToString());
        File.Exists(file).Should().BeTrue("un fichero por patrón, como todo el hub");
        Path.GetDirectoryName(file).Should().EndWith(Path.Combine("alpha", "pattern-silences"));

        PatternSilence? read = _hub.Store.TryReadPatternSilence("alpha", pattern.Id);
        read.Should().NotBeNull();
        read!.Exemplar.Should().Be(Exemplar);
        read.ShortId.Should().Be("P-1");
        read.Reason.Should().Be(SilenceReason.DeudaAceptada);
        read.Notes.Should().Be("no aplica aquí");
        read.By.Should().NotBeNullOrWhiteSpace("silenciar es una acción humana con autor");
        read.SourceFindingUlid.Should().NotBeNull("un patrón nace de un hallazgo concreto");
        read.Utc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    /// <summary>Sin frase no hay alcance: el patrón no llega a escribirse.</summary>
    [Fact]
    public void Un_patron_sin_ejemplar_se_rechaza()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");

        Action act = () => _governance.SilencePattern(
            "alpha", f.Id, "   ", SilenceReason.Otro, null, null);

        act.Should().Throw<ArgumentException>();
        _hub.Store.ListPatternSilences("alpha").Should().BeEmpty();
    }

    /// <summary>Los ids cortos no se repiten dentro de una app: son el handle del prompt.</summary>
    [Fact]
    public void Los_ids_cortos_se_reparten_sin_repetirse()
    {
        SeedApp("alpha");
        SeedPattern("alpha", "primero").ShortId.Should().Be("P-1");
        SeedPattern("alpha", "segundo").ShortId.Should().Be("P-2");
        SeedPattern("alpha", "tercero").ShortId.Should().Be("P-3");
    }

    [Fact]
    public void Un_patron_caducado_es_inexistente_para_filtrar_y_visible_para_revisar()
    {
        var past = new PatternSilence
        {
            Id = _ulids.NewUlid(),
            ShortId = "P-1",
            Exemplar = Exemplar,
            By = "alvaro",
            Utc = DateTimeOffset.UtcNow.AddDays(-10),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        PatternSilenceSet live = PatternSilenceSet.From(new[] { past }, DateTimeOffset.UtcNow);

        live.IsEmpty.Should().BeTrue("caducado = inexistente a efectos de filtrado");
        past.IsExpiredAt(DateTimeOffset.UtcNow).Should().BeTrue("la gestión lo sigue listando como «caducado — revisar»");
    }

    /// <summary>
    /// El borrador del ejemplar quita los nombres propios del caso: es lo que separa «este catch»
    /// de «los catch vacíos». Es un BORRADOR — el usuario lo pule— pero tiene que partir del tipo.
    /// </summary>
    [Theory]
    [InlineData("catch vacío en ReadCSV oculta errores de parseo", "ReadCSV", "catch vacío oculta errores de parseo")]
    [InlineData("Posible desreferencia nula en el parámetro", null, "Posible desreferencia nula en el parámetro")]
    [InlineData("no valida argumentos nulos en Convert(id)", "Convert", "no valida argumentos nulos")]
    public void El_ejemplar_se_propone_generalizado(string title, string? symbol, string expected)
        => ExemplarDraft.Propose(title, symbol).Should().Be(expected);

    /// <summary>Si el recorte se lo come todo, se devuelve el título: una caja vacía es peor.</summary>
    [Fact]
    public void Un_titulo_que_es_todo_identificadores_se_devuelve_tal_cual()
        => ExemplarDraft.Propose("ReadCSV.Parse", "ReadCSV").Should().Be("ReadCSV.Parse");

    // ================================================================== §2 el prompt

    [Fact]
    public async Task El_patron_silenciado_llega_al_prompt_de_la_unidad()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");

        var prompts = new List<string>();
        await Run("alpha", new FakeCopilotAgent(auditScript: r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        }));

        prompts.Should().ContainSingle();
        prompts[0].Should().Contain("TIPOS DE PROBLEMA SILENCIADOS");
        prompts[0].Should().Contain($"[{pattern.ShortId}] {Exemplar}");
        prompts[0].Should().Contain("suppressedByPattern");
    }

    /// <summary>
    /// El catálogo vuelve a viajar entero: F5.12 le quitó el papel de gobernanza, así que su
    /// granularidad deja de importar y no se recorta nada de él.
    /// </summary>
    [Fact]
    public async Task El_catalogo_sigue_entero_en_el_prompt_haya_patrones_o_no()
    {
        SeedApp("alpha");
        SeedPattern("alpha");

        var prompts = new List<string>();
        await Run("alpha", new FakeCopilotAgent(auditScript: r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        }));

        prompts[0].Should().Contain("errores.null.desreferencia").And.Contain("mejoras.estilo.nomenclatura");
    }

    [Fact]
    public async Task La_caducidad_retira_el_patron_del_prompt()
    {
        SeedApp("alpha");
        SeedPattern("alpha", expires: DateTimeOffset.UtcNow.AddDays(-1));

        var prompts = new List<string>();
        await Run("alpha", new FakeCopilotAgent(auditScript: r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        }));

        prompts[0].Should().NotContain("TIPOS DE PROBLEMA SILENCIADOS");
        prompts[0].Should().NotContain(Exemplar);
    }

    /// <summary>El invariante que sostiene todo: el patrón es POR-APLICACIÓN.</summary>
    [Fact]
    public async Task Silenciar_en_una_app_no_calla_a_otra()
    {
        SeedApp("alpha");
        SeedApp("beta");
        SeedPattern("alpha");

        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slug in new[] { "alpha", "beta" })
        {
            string current = slug;
            await Run(current, new FakeCopilotAgent(auditScript: r =>
            {
                prompts[current] = r.Prompt;
                return Array.Empty<SubmitFindingArgs>();
            }));
        }

        prompts["alpha"].Should().Contain(Exemplar);
        prompts["beta"].Should().NotContain(Exemplar, "un tipo que sobra en una app puede ser crítico en la de al lado");
        _hub.Store.ListPatternSilences("beta").Should().BeEmpty();
    }

    // ================================================================== §2 contadores

    [Fact]
    public async Task Lo_que_el_auditor_declara_callarse_se_cuenta_y_se_nombra()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");

        SessionResult result = await AuditSuppressing("alpha", (pattern.ShortId, 3));

        result.Counters.SuppressedByPattern.Should().Be(3);
        result.Counters.New.Should().Be(0);
        result.Counters.Rejected.Should().Be(0, "callarse lo que se le pidió no es un payload malo");
        result.SuppressionsByPattern.Should().ContainSingle()
            .Which.Should().Be(new PatternSuppressionTally(pattern.ShortId, Exemplar, 3));

        AuditSession session = _hub.Store.ListSessions("alpha").Single();
        session.Counters.SuppressedByPattern.Should().Be(3);
        session.SuppressionsByPattern.Should().ContainSingle();
        session.Notes.Should().Contain(n => n.Contains("suprimido por patrón") && n.Contains(Exemplar));
    }

    [Fact]
    public async Task El_informe_cuenta_los_suprimidos_y_dice_de_que_patron_fueron()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");

        SessionResult result = await AuditSuppressing("alpha", (pattern.ShortId, 2));

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("alpha", result.SessionId.ToString()));
        report.Should().Contain("Suprimidos por patrón: 2");
        report.Should().Contain($"patrón {pattern.ShortId}: 2");
        report.Should().Contain("Detecciones suprimidas por patrón silenciado");
        report.Should().Contain(Exemplar);
        report.Should().NotContain("Payloads rechazados");
    }

    /// <summary>
    /// Sin patrones no se escribe nada del asunto: un contador a cero con su párrafo explicativo es
    /// ruido en el informe de todos los días.
    /// </summary>
    [Fact]
    public async Task Sin_supresiones_el_informe_no_habla_de_patrones()
    {
        SeedApp("alpha");
        SessionResult result = await Audit("alpha");

        string report = File.ReadAllText(_hub.HubPaths.ReportFile("alpha", result.SessionId.ToString()));
        report.Should().NotContain("Suprimidos por patrón");
        report.Should().NotContain("Detecciones suprimidas por patrón silenciado");
    }

    /// <summary>
    /// Un id que el modelo se inventó se cuenta igual y se dice que no existe. Tragárselo dejaría
    /// una supresión invisible, que es lo único que esta pieza no puede permitirse.
    /// </summary>
    [Fact]
    public async Task Un_patron_citado_que_no_existe_se_cuenta_y_se_marca()
    {
        SeedApp("alpha");
        SeedPattern("alpha");

        SessionResult result = await AuditSuppressing("alpha", ("P-99", 1));

        result.Counters.SuppressedByPattern.Should().Be(1);
        result.SuppressionsByPattern.Should().ContainSingle()
            .Which.Exemplar.Should().Contain("no existe");
    }

    /// <summary>Un count que no suma no se cuenta, pero queda su rastro en la traza de tools.</summary>
    [Fact]
    public async Task Una_supresion_con_count_cero_no_se_cuenta()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");

        SessionResult result = await AuditSuppressing("alpha", (pattern.ShortId, 0));

        result.Counters.SuppressedByPattern.Should().Be(0);
        result.SuppressionsByPattern.Should().BeEmpty();
        _hub.Store.ListSessions("alpha").Single().Notes
            .Should().Contain(n => n.Contains("count=0"));
    }

    /// <summary>
    /// Callarse no alarga el barrido. Si contase como trabajo, una app con un patrón y un auditor
    /// tozudo agotaría el tope de pasadas sin producir nada.
    /// </summary>
    [Fact]
    public async Task Una_pasada_que_solo_suprime_queda_seca()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 3;
        _settings.Save(s);

        SessionResult result = await AuditSuppressing("alpha", (pattern.ShortId, 2));

        AuditSession session = _hub.Store.ListSessions("alpha").Single();

        // Dos pasadas, las dos secas: es lo que cierra una unidad desde F12 §E. Lo que este test
        // fija es que suprimir no las ROMPE — si callarse contase como trabajo, la racha no
        // arrancaría nunca y una app con un patrón agotaría el tope sin producir nada.
        session.Units.Single().Passes!.Should().HaveCount(2);
        session.Units.Single().Passes!.Should().OnlyContain(p => p.Dry);
        session.Units.Single().Verdict.Should().Be("auditada");
        result.Counters.SuppressedByPattern.Should().Be(4, "dos por pasada, y hubo dos pasadas");
    }

    /// <summary>
    /// No hay filtro programático: un hallazgo que corresponda a un patrón y que el auditor reporte
    /// igualmente ENTRA, con normalidad. El coste del fallo es un hallazgo de más, visible.
    /// </summary>
    [Fact]
    public async Task Un_hallazgo_reportado_pese_al_patron_entra_con_normalidad()
    {
        SeedApp("alpha");
        SeedPattern("alpha", "nombres poco claros");

        SessionResult result = await Audit("alpha", Payload(title: "nombre poco claro"));

        result.Counters.New.Should().Be(1);
        result.Counters.SuppressedByPattern.Should().Be(0);
        _hub.Store.ListFindings("alpha").Should().ContainSingle(f => f.Title == "nombre poco claro");
    }

    // ================================================================== §2 trabajo del patrón

    [Fact]
    public async Task Cada_patron_acumula_cuanto_ha_suprimido()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");

        await AuditSuppressing("alpha", (pattern.ShortId, 2));
        await AuditSuppressing("alpha", (pattern.ShortId, 3));

        PatternSilence after = _hub.Store.TryReadPatternSilence("alpha", pattern.Id)!;
        after.Suppressions.Should().Be(5, "el contador dice cuánto trabaja el patrón");
        after.LastSuppressionUtc.Should().NotBeNull();
    }

    // ================================================================== §3 el silencio individual

    /// <summary>Anti-objetivo: el silencio por hallazgo NO cambia.</summary>
    [Fact]
    public void El_silencio_individual_sigue_intacto()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");

        _governance.Silence("alpha", f.Id, SilenceReason.FalsoPositivo, "aquí no", null);

        Finding after = _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Silenciado);
        Silence? silence = _hub.Store.TryReadSilence("alpha", f.Id);
        silence!.ByPatternExemplar.Should().BeNull("este silencio lo decidió una persona sobre este caso");
        _hub.Store.ListPatternSilences("alpha").Should().BeEmpty("silenciar un hallazgo no crea patrones");
    }

    /// <summary>Un hallazgo silenciado sigue apareciéndole al auditor para que se pronuncie.</summary>
    [Fact]
    public void El_hallazgo_origen_sigue_en_la_lista_de_existentes()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        SeedPattern("alpha", source: f);

        _reconciliation.ExistingForUnit("alpha", "A.cs").Should().ContainSingle()
            .Which.Id.Should().Be(f.Id);
    }

    [Fact]
    public void Silenciar_el_patron_silencia_el_hallazgo_que_lo_origino()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");

        GovernanceService.PatternSilenceResult result = _governance.SilencePattern(
            "alpha", f.Id, Exemplar, SilenceReason.DeudaAceptada, "no aplica", null);

        result.SilencedSource.Should().BeTrue();
        Finding after = _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Silenciado);
        after.History.Should().Contain(h => h.Event == FindingEvent.Silenced && h.Detail!.Contains(Exemplar));

        Silence? silence = _hub.Store.TryReadSilence("alpha", f.Id);
        silence!.ByPatternExemplar.Should().Be(Exemplar, "la ficha tiene que poder decir de dónde vino");
    }

    /// <summary>Sobre un hallazgo ya silenciado se puede crear el patrón sin tocar su silencio.</summary>
    [Fact]
    public void Sobre_un_hallazgo_ya_silenciado_el_patron_no_reescribe_su_silencio()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        _governance.Silence("alpha", f.Id, SilenceReason.FalsoPositivo, "aquí no", null);

        GovernanceService.PatternSilenceResult result = _governance.SilencePattern(
            "alpha", f.Id, Exemplar, SilenceReason.DeudaAceptada, null, null);

        result.SilencedSource.Should().BeFalse();
        Silence? silence = _hub.Store.TryReadSilence("alpha", f.Id);
        silence!.Reason.Should().Be(SilenceReason.FalsoPositivo, "la decisión previa era suya");
        silence.ByPatternExemplar.Should().BeNull();
    }

    /// <summary>
    /// F12 §F INVIERTE lo que F5.12 había decidido aquí. Antes, retirar un patrón no des-silenciaba
    /// nada «porque cada silencio fue una decisión registrada»; pero el silencio que pone un patrón
    /// NO es una decisión sobre ese hallazgo —nadie lo miró— y dejarlo congelado significaba que
    /// solo otra auditoría, pagada, podía recuperarlo. Ahora el silencio por patrón es derivado:
    /// retirar el patrón devuelve a activo, al instante, lo que solo él tapaba. Lo que sí se
    /// conserva es el silencio individual, y eso lo prueba <see cref="DerivedPatternSilenceTests"/>.
    /// </summary>
    [Fact]
    public void Des_silenciar_el_patron_devuelve_a_activo_lo_que_solo_el_tapaba()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        PatternSilence pattern = SeedPattern("alpha", source: f);

        _governance.UnsilencePattern("alpha", pattern.Id).Should().BeTrue();

        _hub.Store.TryReadPatternSilence("alpha", pattern.Id).Should().BeNull();
        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!.Status
            .Should().Be(FindingStatus.Activo, "nadie había decidido nada sobre ESTE hallazgo");
    }

    [Fact]
    public async Task Des_silenciar_el_patron_lo_retira_del_prompt()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");

        var prompts = new List<string>();
        FakeCopilotAgent Recorder() => new(auditScript: r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        });

        await Run("alpha", Recorder());
        prompts[0].Should().Contain(Exemplar);

        _governance.UnsilencePattern("alpha", pattern.Id);

        await Run("alpha", Recorder());
        prompts[1].Should().NotContain(Exemplar, "el auditor vuelve a poder reportar problemas de ese tipo");
    }

    // ================================================================== §3 la ficha

    private FindingDetailViewModel Detail()
        => new(
            _hub, _governance, _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines), _toasts,
            TestFactory.Links(_hub, _paths), TestFactory.LinkFlow(_hub, _paths, _toasts));

    [Fact]
    public void El_selector_de_alcance_dice_la_consecuencia_de_cada_opcion()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.SilenceScope.Should().Be(SilenceScope.Hallazgo, "el gesto de todos los días es el de por defecto");
        vm.ScopeOptions.Should().HaveCount(2);
        vm.ScopeOptions[0].Label.Should().Be("Solo este hallazgo");
        vm.ScopeOptions[0].Consequence.Should().Contain("Este caso concreto deja de contar");
        vm.ScopeOptions[0].HasExemplar.Should().BeFalse();
        vm.ScopeOptions[1].Label.Should().Be("Este tipo de problema en toda la aplicación");
        vm.ScopeOptions[1].Consequence.Should().Be(
            "Las auditorías de ALPHA dejarán de reportar problemas de este tipo. "
            + "El juicio de similitud lo hace el auditor.");
        vm.ScopeOptions[1].HasExemplar.Should().BeTrue("la frase se ve y se edita bajo su opción");
    }

    [Fact]
    public void La_ficha_propone_el_ejemplar_del_hallazgo_abierto()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha", title: "catch vacío en ReadCSV oculta errores", symbol: "ReadCSV");
        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.PatternExemplar.Should().Be("catch vacío oculta errores");
    }

    [Fact]
    public void Elegir_el_alcance_de_patron_cambia_el_boton()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.SilenceActionLabel.Should().Be("Silenciar");
        vm.ScopeOptions[1].IsSelected = true;
        vm.SilenceScope.Should().Be(SilenceScope.Patron);
        vm.SilenceActionLabel.Should().Be("Silenciar este tipo");
        vm.ScopeOptions[0].IsSelected.Should().BeFalse("son excluyentes");
    }

    [Fact]
    public void Silenciar_el_tipo_usa_el_ejemplar_editado()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha", title: "nombre poco claro");
        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.SilenceScope = SilenceScope.Patron;
        vm.PatternExemplar = "  nombres de una sola letra en código de negocio  ";
        vm.SilenceNotes = "convención del equipo";
        vm.SilenceCommand.Execute(null);

        PatternSilence pattern = _hub.Store.ListPatternSilences("alpha").Single();
        pattern.Exemplar.Should().Be("nombres de una sola letra en código de negocio");
        pattern.Notes.Should().Be("convención del equipo");
        pattern.SourceFindingUlid.Should().Be(f.Id);
        _toasts.Items.Last().Text.Should().Contain("P-1").And.Contain("ALPHA");
    }

    [Fact]
    public void Sin_ejemplar_no_se_silencia_nada_y_se_dice()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.SilenceScope = SilenceScope.Patron;
        vm.PatternExemplar = "   ";
        vm.SilenceCommand.Execute(null);

        _hub.Store.ListPatternSilences("alpha").Should().BeEmpty();
        _toasts.Items.Last().Text.Should().Contain("Escribe la frase");
    }

    /// <summary>
    /// El camino natural: silencias un falso positivo, ves que se repite por toda la app y vuelves
    /// a esa misma ficha a callar el tipo. Con el botón atado a <c>CanSilence</c> te encontrabas el
    /// selector pintado y nada que pulsar.
    /// </summary>
    [Fact]
    public void Sobre_un_hallazgo_ya_silenciado_se_puede_silenciar_el_tipo()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        _governance.Silence("alpha", f.Id, SilenceReason.FalsoPositivo, "aquí no", null);

        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.CanSilence.Should().BeFalse("silenciar lo ya silenciado no hace nada");
        vm.CanApplySilence.Should().BeFalse("con el alcance por defecto el botón sigue retirado");

        vm.SilenceScope = SilenceScope.Patron;
        vm.CanApplySilence.Should().BeTrue("silenciar el tipo sí hace algo sobre un silenciado");

        vm.SilenceCommand.Execute(null);
        _hub.Store.ListPatternSilences("alpha").Should().ContainSingle();
    }

    [Fact]
    public void La_ficha_dice_que_el_silencio_vino_de_un_patron()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        SeedPattern("alpha", source: f);

        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.SilenceSummary.Should().StartWith($"Silenciado por el patrón «{Exemplar}»")
            .And.Contain("puesto por", "F12 §F: quién lo puso y cuándo, no solo que lo puso un patrón");
    }

    [Fact]
    public void La_ficha_dice_que_este_hallazgo_es_el_origen_del_patron()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        PatternSilence pattern = SeedPattern("alpha", source: f);

        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.HasPatternOrigin.Should().BeTrue();
        vm.PatternOriginSummary.Should().Contain($"Origen del patrón silenciado «{Exemplar}»")
            .And.Contain(pattern.ShortId);
    }

    [Fact]
    public void Un_hallazgo_cualquiera_no_dice_nada_de_patrones()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");

        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.HasPatternOrigin.Should().BeFalse();
        vm.SilenceSummary.Should().BeEmpty();
    }

    [Fact]
    public void Un_silencio_normal_sigue_diciendo_lo_de_siempre()
    {
        SeedApp("alpha");
        Finding f = SeedFinding("alpha");
        _governance.Silence("alpha", f.Id, SilenceReason.FalsoPositivo, "aquí no", null);

        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);

        vm.SilenceSummary.Should().StartWith("Silenciado por ").And.NotContain("patrón");
    }

    // ================================================================== §3 gestión

    private PatternSilencesViewModel Manage(string slug)
    {
        var vm = new PatternSilencesViewModel(_hub, _governance, _toasts);
        vm.Load(slug);
        return vm;
    }

    [Fact]
    public void La_gestion_lista_el_patron_con_su_frase_autor_caducidad_estado_y_trabajo()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha", expires: DateTimeOffset.UtcNow.AddDays(30));

        PatternSilencesViewModel vm = Manage("alpha");

        vm.IsEmpty.Should().BeFalse();
        PatternSilenceRow row = vm.Rows.Single();
        row.ShortId.Should().Be(pattern.ShortId);
        row.Exemplar.Should().Be(Exemplar);
        row.Reason.Should().Be("Deuda aceptada");
        row.Notes.Should().Be("no aplica aquí");
        row.By.Should().NotBeNullOrWhiteSpace();
        row.Origin.Should().Contain("desde el hallazgo");
        row.State.Should().Be("activo");
        row.Expiry.Should().NotBe("permanente");
        row.Work.Should().Contain("Todavía no ha suprimido");
    }

    [Fact]
    public async Task La_gestion_dice_cuanto_trabaja_cada_patron()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");
        await AuditSuppressing("alpha", (pattern.ShortId, 4));

        Manage("alpha").Rows.Single().Work.Should().Contain("4 detección(es)");
    }

    [Fact]
    public void Un_patron_caducado_se_lista_diciendo_que_hay_que_revisarlo()
    {
        SeedApp("alpha");
        SeedPattern("alpha", expires: DateTimeOffset.UtcNow.AddDays(-2));

        PatternSilenceRow row = Manage("alpha").Rows.Single();

        row.Expired.Should().BeTrue();
        row.State.Should().Be("caducado — revisar");
        row.Effect.Should().Contain("vuelven a reportar");
    }

    /// <summary>Afinar el alcance es reescribir la frase. Sin catálogo que partir.</summary>
    [Fact]
    public async Task Editar_el_ejemplar_cambia_lo_que_lee_el_auditor_sin_cambiar_el_id()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");
        PatternSilencesViewModel vm = Manage("alpha");

        vm.Rows.Single().Exemplar = "catch vacíos en el capa de datos, no en la de UI";
        vm.ApplyExemplarCommand.Execute(vm.Rows.Single());

        PatternSilence after = _hub.Store.TryReadPatternSilence("alpha", pattern.Id)!;
        after.Exemplar.Should().Be("catch vacíos en el capa de datos, no en la de UI");
        after.ShortId.Should().Be(pattern.ShortId, "un informe viejo tiene que seguir nombrando lo mismo");

        var prompts = new List<string>();
        await Run("alpha", new FakeCopilotAgent(auditScript: r =>
        {
            prompts.Add(r.Prompt);
            return Array.Empty<SubmitFindingArgs>();
        }));
        prompts[0].Should().Contain("catch vacíos en el capa de datos").And.NotContain(Exemplar);
    }

    [Fact]
    public void Un_ejemplar_vacio_no_se_guarda_y_se_dice()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");
        PatternSilencesViewModel vm = Manage("alpha");

        vm.Rows.Single().Exemplar = "   ";
        vm.ApplyExemplarCommand.Execute(vm.Rows.Single());

        _hub.Store.TryReadPatternSilence("alpha", pattern.Id)!.Exemplar.Should().Be(Exemplar);
        _toasts.Items.Last().Text.Should().Contain("no puede quedarse vacío");
    }

    [Fact]
    public void Des_silenciar_desde_la_gestion_retira_el_patron()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");
        PatternSilencesViewModel vm = Manage("alpha");

        vm.UnsilenceCommand.Execute(vm.Rows.Single());

        vm.Rows.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
        _hub.Store.TryReadPatternSilence("alpha", pattern.Id).Should().BeNull();
    }

    [Fact]
    public void Editar_la_caducidad_la_escribe_donde_el_prompt_la_lee()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");
        PatternSilencesViewModel vm = Manage("alpha");

        vm.Rows.Single().ExpiryDays = 7;
        vm.ApplyExpiryCommand.Execute(vm.Rows.Single());

        PatternSilence after = _hub.Store.TryReadPatternSilence("alpha", pattern.Id)!;
        after.ExpiresUtc.Should().NotBeNull();
        after.ExpiresUtc!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromMinutes(2));
        after.Reason.Should().Be(SilenceReason.DeudaAceptada, "editar la caducidad no reescribe el motivo");
        after.Notes.Should().Be("no aplica aquí");
        after.Exemplar.Should().Be(Exemplar);
    }

    [Fact]
    public void Poner_cero_dias_devuelve_el_patron_a_permanente()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha", expires: DateTimeOffset.UtcNow.AddDays(-1));
        PatternSilencesViewModel vm = Manage("alpha");

        vm.Rows.Single().ExpiryDays = 0;
        vm.ApplyExpiryCommand.Execute(vm.Rows.Single());

        _hub.Store.TryReadPatternSilence("alpha", pattern.Id)!.ExpiresUtc.Should().BeNull();
        Manage("alpha").Rows.Single().State.Should().Be("activo");
    }

    [Fact]
    public void Una_caducidad_negativa_no_se_aplica_y_se_dice()
    {
        SeedApp("alpha");
        PatternSilence pattern = SeedPattern("alpha");
        PatternSilencesViewModel vm = Manage("alpha");

        vm.Rows.Single().ExpiryDays = -3;
        vm.ApplyExpiryCommand.Execute(vm.Rows.Single());

        _hub.Store.TryReadPatternSilence("alpha", pattern.Id)!.ExpiresUtc.Should().BeNull();
        _toasts.Items.Last().Text.Should().Contain("no puede ser negativa");
    }

    [Fact]
    public void La_gestion_de_una_app_no_ve_los_patrones_de_otra()
    {
        SeedApp("alpha");
        SeedApp("beta");
        SeedPattern("alpha");

        Manage("alpha").Rows.Should().ContainSingle();
        Manage("beta").Rows.Should().BeEmpty();
    }

    /// <summary>
    /// §4: el tope no bloquea nada. Muchos patrones activos no es un problema de coste —la lista
    /// es despreciable frente a la unidad— sino el síntoma de que el silenciado se está usando como
    /// taxonomía, que es lo que F5.12 vino a evitar.
    /// </summary>
    [Fact]
    public void Pasado_el_tope_la_gestion_pide_consolidar()
    {
        SeedApp("alpha");
        PatternSilencesViewModel below = Manage("alpha");
        below.HasCapNotice.Should().BeFalse();

        for (int i = 0; i <= PatternSilenceSet.SoftCap; i++)
        {
            SeedPattern("alpha", $"tipo de problema número {i}");
        }

        PatternSilencesViewModel above = Manage("alpha");
        above.HasCapNotice.Should().BeTrue();
        above.CapNotice.Should().Contain("consolidar");
    }
}
