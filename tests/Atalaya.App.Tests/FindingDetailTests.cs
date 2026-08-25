using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.5 — el rediseño de la ficha de hallazgo (V4).
/// <para>
/// <b>Qué hay que poder entender sin que nadie te lo explique</b>: qué es el hallazgo, dónde está,
/// qué dice el modelo y qué puedes hacer tú. Lo que estos tests fijan es la parte de eso que es
/// comprobable: qué controles existen <i>cuándo</i> —reabrir solo si está resuelto, la disputa solo
/// si hay disputa—, que las justificaciones obligatorias lo sean de verdad, que el código que se
/// enseña sea el que hay ahora en el clon y que ningún identificador de C# se cuele en una interfaz
/// en castellano.
/// </para>
/// </summary>
public sealed class FindingDetailTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly GovernanceService _governance;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public FindingDetailTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-v4", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _machines.SetClonePath("alpha", _clone);
        _governance = new GovernanceService(_hub, _ulids);

        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig { Slug = "alpha", Name = "Alpha", RepoUrl = "u", CurrentCycle = 1 });
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

    private const string Fuente = """
        using System;

        namespace Demo;

        public sealed class Repositorio
        {
            public void Guardar(string dato)
            {
                var stream = File.OpenWrite(dato);
                stream.Write(Encoding.UTF8.GetBytes(dato));
            }
        }
        """;

    /// <summary>La línea del hallazgo dentro de <see cref="Fuente"/>: el stream sin liberar.</summary>
    private const int LineaDelHallazgo = 9;

    private const string LineaAnclada = "        var stream = File.OpenWrite(dato);";

    private Finding Seed(
        string path = "src/Repositorio.cs",
        FindingStatus status = FindingStatus.Activo,
        string? disputedBy = null,
        string? disputeJustification = null,
        bool writeClone = true,
        string? snippetHash = null)
    {
        if (writeClone)
        {
            string abs = Path.Combine(_clone, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, Fuente);
        }

        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, "0123456789abcdef", "alvaro");
        var finding = new Finding
        {
            Id = _ulids.NewUlid(),
            DisplayId = "BUG-0042",
            RuleId = "errores.recursos.idisposable-no-liberado",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = status,
            Title = "IDisposable sin liberar",
            Description = "El stream no se cierra.",
            Impact = "Fuga de descriptores.",
            Recommendation = "Envolver en using.",
            Locations = { new Location(path, LineaDelHallazgo, snippetHash ?? CodeAnchor.ComputeSnippetHash(LineaAnclada)) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            TimesConfirmed = 2,
        };

        finding.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Detected, "alvaro", "detected via Lotes"));

        if (disputedBy is not null)
        {
            finding.Dispute(
                DateTimeOffset.UtcNow, "alvaro", disputedBy,
                disputeJustification ?? "no es un defecto");
        }

        if (status == FindingStatus.Resuelto)
        {
            finding.Resolved = new ResolutionStamp(
                DateTimeOffset.UtcNow, ResolutionVia.Manual, AuditMode.Verify, "abc", "alvaro", "ya estaba");
        }

        _hub.Store.WriteFinding("alpha", finding);
        return finding;
    }

    private FindingDetailViewModel NewDetail(ICopilotAgent? agent = null)
        => new(
            _hub,
            _governance,
            _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, agent ?? new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines),
            _toasts);

    private FindingDetailViewModel Open(Finding f)
    {
        FindingDetailViewModel vm = NewDetail();
        vm.Load("alpha", f.Id);
        return vm;
    }

    private string LastToast() => _toasts.Items.Last().Text;

    // =========================================================== §2 cabecera y metadatos

    /// <summary>
    /// El ruleId NO se elimina de la ficha: baja a metadatos con nombre. Es la regla del checklist
    /// que motivó el hallazgo y desde V3 se puede buscar por ella, así que perderla sería perder
    /// el puente entre la lista y el detalle.
    /// </summary>
    [Fact]
    public void El_ruleId_sigue_en_la_ficha_pero_como_campo_ETIQUETADO()
    {
        FindingDetailViewModel vm = Open(Seed());

        MetaRow? regla = vm.Meta.FirstOrDefault(m => m.Label == "Regla");

        regla.Should().NotBeNull("el ruleId es un dato, no un adorno del título");
        regla!.Value.Should().Be("errores.recursos.idisposable-no-liberado");
        regla.Tooltip.Should().Contain("checklist");
        regla.Mono.Should().BeTrue("un identificador en proporcional no se lee");
    }

    [Fact]
    public void Los_metadatos_traen_todo_lo_que_situa_al_hallazgo()
    {
        FindingDetailViewModel vm = Open(Seed());

        string[] labels = vm.Meta.Select(m => m.Label).ToArray();

        labels.Should().Contain(new[]
        {
            "Regla", "Identificador", "Aplicación", "Unidad", "Origen",
            "Primera detección", "Última confirmación", "Veces confirmado", "Commit anclado",
        });
        vm.Meta.First(m => m.Label == "Unidad").Value.Should().Contain("src/Repositorio.cs:9");
        vm.Meta.First(m => m.Label == "Aplicación").Value.Should().Be("Alpha");
        vm.Meta.First(m => m.Label == "Veces confirmado").Value.Should().Be("2");
    }

    /// <summary>El origen se escribe, no se vuelca: «Auditoría por lotes», no «Lotes».</summary>
    [Fact]
    public void El_origen_y_el_estado_se_escriben_en_castellano()
    {
        FindingDetailViewModel vm = Open(Seed());

        vm.Meta.First(m => m.Label == "Origen").Value.Should().Be("Auditoría por lotes");
        vm.StatusLabel.Should().Be("Activo");
        vm.ConfidenceLabel.Should().Be("Confianza media");
    }

    [Fact]
    public void El_chip_de_disputa_cuenta_los_modelos_que_discrepan()
    {
        Finding f = Seed(disputedBy: "gpt-5");
        f.Dispute(DateTimeOffset.UtcNow, "alvaro", "claude", "tampoco lo veo");
        _hub.Store.WriteFinding("alpha", f);

        FindingDetailViewModel vm = Open(f);

        vm.DisputeBadge.Should().Be("⚖︎ Disputado ×2");
        vm.DisputeSummary.Should().Contain("gpt-5").And.Contain("claude");
    }

    // =========================================================== §4 visibilidad condicional

    /// <summary>La sección de disputa existe SOLO si hay disputa: si no, es ruido con botones.</summary>
    [Fact]
    public void La_disputa_solo_aparece_cuando_hay_disputa()
    {
        Open(Seed()).ShowDispute.Should().BeFalse();
        Open(Seed(path: "src/Otro.cs", disputedBy: "gpt-5")).ShowDispute.Should().BeTrue();
    }

    /// <summary>Cerrada la disputa, la sección se retira sola sin volver a navegar.</summary>
    [Fact]
    public void Cerrada_la_disputa_la_seccion_desaparece()
    {
        FindingDetailViewModel vm = Open(Seed(disputedBy: "gpt-5"));
        vm.ShowDispute.Should().BeTrue();

        vm.DismissDisputeCommand.Execute(null);

        vm.ShowDispute.Should().BeFalse();
        vm.IsDisputed.Should().BeFalse();
    }

    [Fact]
    public void Reabrir_solo_existe_si_el_hallazgo_esta_resuelto()
    {
        Open(Seed()).CanReopen.Should().BeFalse();
        Open(Seed(path: "src/Otro.cs", status: FindingStatus.Resuelto)).CanReopen.Should().BeTrue();
    }

    [Fact]
    public void Resolver_a_mano_no_se_ofrece_sobre_algo_ya_resuelto()
    {
        Open(Seed()).CanResolveManually.Should().BeTrue();
        Open(Seed(path: "src/Otro.cs", status: FindingStatus.Resuelto)).CanResolveManually.Should().BeFalse();
    }

    [Fact]
    public void Silenciar_y_des_silenciar_no_se_ofrecen_a_la_vez()
    {
        FindingDetailViewModel activo = Open(Seed());
        activo.CanSilence.Should().BeTrue();
        activo.CanUnsilence.Should().BeFalse();

        FindingDetailViewModel silenciado = Open(Seed(path: "src/Otro.cs", status: FindingStatus.Silenciado));
        silenciado.CanSilence.Should().BeFalse();
        silenciado.CanUnsilence.Should().BeTrue();
    }

    /// <summary>Tras silenciar, la ficha cambia de estado sin que haya que volver a entrar.</summary>
    [Fact]
    public void Silenciar_actualiza_el_estado_y_resume_el_silencio_en_la_ficha()
    {
        FindingDetailViewModel vm = Open(Seed());

        vm.SilenceReason = SilenceReason.DeudaAceptada;
        vm.SilenceNotes = "lo asumimos este trimestre";
        vm.SilenceExpiryDays = 30;
        vm.SilenceCommand.Execute(null);

        vm.StatusLabel.Should().Be("Silenciado");
        vm.CanUnsilence.Should().BeTrue();
        vm.HasSilence.Should().BeTrue();
        vm.SilenceSummary.Should().Contain("Deuda aceptada").And.Contain("caduca el");
    }

    [Fact]
    public void Un_silencio_sin_caducidad_se_declara_permanente()
    {
        FindingDetailViewModel vm = Open(Seed());

        vm.SilenceExpiryDays = 0;
        vm.SilenceCommand.Execute(null);

        vm.SilenceSummary.Should().Contain("permanente");
        LastToast().Should().Contain("permanente");
    }

    // =========================================================== §4 validación

    /// <summary>
    /// Sin justificación no hay resolución manual: es la única acción que cierra un hallazgo sin
    /// que nadie haya mirado el código, y lo que queda escrito de por qué es ese texto.
    /// </summary>
    [Fact]
    public void La_resolucion_manual_sin_justificacion_no_resuelve_nada()
    {
        Finding f = Seed();
        FindingDetailViewModel vm = Open(f);

        vm.Justification = "   ";
        vm.ResolveManuallyCommand.Execute(null);

        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!.Status.Should().Be(FindingStatus.Activo);
        LastToast().Should().Contain("justificación");
        vm.ManualResolutionExpanded.Should().BeTrue("el aviso tiene que llevar al campo que falta");
    }

    [Fact]
    public void La_resolucion_manual_con_justificacion_cierra_y_deja_traza()
    {
        Finding f = Seed();
        FindingDetailViewModel vm = Open(f);

        vm.Justification = "corregido en el commit 9f2a1c";
        vm.ResolveManuallyCommand.Execute(null);

        Finding after = _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;
        after.Status.Should().Be(FindingStatus.Resuelto);
        after.Resolved!.Via.Should().Be(ResolutionVia.Manual);
        after.Resolved.Justification.Should().Be("corregido en el commit 9f2a1c");
        vm.Justification.Should().BeEmpty("el campo se vacía: ya está escrito donde tenía que estar");
        vm.CanReopen.Should().BeTrue();
    }

    [Fact]
    public void Reabrir_algo_que_no_esta_resuelto_avisa_y_no_toca_el_hallazgo()
    {
        Finding f = Seed();
        FindingDetailViewModel vm = Open(f);

        vm.ReopenCommand.Execute(null);

        LastToast().Should().Contain("resuelto");
        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!
            .History.Should().NotContain(h => h.Event == FindingEvent.Reopened);
    }

    [Fact]
    public void Reclasificar_a_la_misma_severidad_avisa_en_vez_de_escribir_una_entrada_vacia()
    {
        Finding f = Seed();
        FindingDetailViewModel vm = Open(f);

        vm.Severity = Severity.Alta;   // la que ya tiene
        vm.ApplySeverityCommand.Execute(null);

        LastToast().Should().Contain("ya es de severidad Alta");
        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!
            .History.Should().NotContain(h => h.Event == FindingEvent.SeverityChanged);
    }

    [Fact]
    public void Reclasificar_de_verdad_queda_en_el_historial()
    {
        Finding f = Seed();
        FindingDetailViewModel vm = Open(f);

        vm.Severity = Severity.Critica;
        vm.ApplySeverityCommand.Execute(null);

        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!.Severity.Should().Be(Severity.Critica);
        vm.History.Should().Contain(h => h.Event == FindingEvent.SeverityChanged);
        LastToast().Should().Contain("Crítica");
    }

    /// <summary>Los motivos se leen, no se declaran: «Falso positivo», no «FalsoPositivo».</summary>
    [Fact]
    public void Los_motivos_de_silencio_se_escriben_con_espacios_y_tildes()
    {
        FindingDetailViewModel vm = Open(Seed());

        vm.ReasonOptions.Select(o => o.Label).Should().BeEquivalentTo(new[]
        {
            "Falso positivo", "Deuda aceptada", "Decisión arquitectónica", "Otro",
        });
        vm.ReasonOptions.Should().Contain(o => o.Value == SilenceReason.DecisionArquitectonica);
    }

    // =========================================================== §5 historial

    /// <summary>
    /// El historial habla castellano y va de lo más reciente a lo más antiguo: lo último que le
    /// pasó al hallazgo es lo que explica en qué estado está ahora.
    /// </summary>
    [Fact]
    public void El_historial_esta_traducido_y_del_reves()
    {
        Finding f = Seed();
        FindingDetailViewModel vm = Open(f);

        vm.SilenceCommand.Execute(null);

        vm.History.First().Label.Should().Be("Silenciado", "lo último arriba");
        vm.History.Last().Label.Should().Be("Detectado");
        vm.History.Select(h => h.Label).Should().NotContain(l => l.Contains("Silenced") || l.Contains("Detected"));
        vm.History.Should().OnlyContain(h => h.Icon.Length > 0);
    }

    /// <summary>
    /// El texto del evento se conserva ENTERO. Lo que se pliega es su altura, y solo si es largo;
    /// truncarlo escondía justo la justificación que hay que leer para decidir una disputa.
    /// </summary>
    [Fact]
    public void Una_justificacion_larga_se_pliega_pero_no_se_recorta()
    {
        string largo = string.Join(" ", Enumerable.Repeat("este patrón es deliberado en el proyecto", 12));
        Finding f = Seed(disputedBy: "gpt-5", disputeJustification: largo);

        FindingDetailViewModel vm = Open(f);
        HistoryRow disputa = vm.History.First(h => h.Event == FindingEvent.Disputed);

        disputa.Detail.Should().Contain(largo, "el texto completo sigue ahí");
        disputa.IsLong.Should().BeTrue();
        disputa.DetailMaxHeight.Should().Be(ExpandableRow.CollapsedHeight);
        disputa.ToggleLabel.Should().Be("ver más");

        disputa.ToggleCommand.Execute(null);

        disputa.DetailMaxHeight.Should().Be(double.PositiveInfinity);
        disputa.ToggleLabel.Should().Be("ver menos");
    }

    [Fact]
    public void Un_evento_corto_no_ofrece_ver_mas()
    {
        HistoryRow detectado = Open(Seed()).History.Single(h => h.Event == FindingEvent.Detected);

        detectado.IsLong.Should().BeFalse();
        detectado.DetailMaxHeight.Should().Be(double.PositiveInfinity);
    }

    // =========================================================== §3 snippet

    [Fact]
    public void El_snippet_es_el_metodo_completo_con_las_lineas_REALES_del_fichero()
    {
        FindingDetailViewModel vm = Open(Seed());

        vm.SnippetState.Should().Be(SnippetState.Anclado);
        vm.SnippetFirstLine.Should().Be(7, "el recorte empieza en la firma de Guardar, no en la línea 1");
        vm.SnippetHighlightLine.Should().Be(LineaDelHallazgo);
        vm.Snippet.Should().StartWith("    public void Guardar");
        vm.Snippet.Should().Contain("File.OpenWrite");
        vm.SnippetCaption.Should().Contain("Repositorio.Guardar").And.Contain("src/Repositorio.cs:9");
        vm.HasSnippetNotice.Should().BeFalse("nada que advertir: el código es el que se auditó");
    }

    /// <summary>
    /// El código cambió bajo el ancla: se avisa y se ofrece verificar. Nunca se enseña lo viejo
    /// como si fuera lo actual — que es exactamente lo que hacía el recorte anterior.
    /// </summary>
    [Fact]
    public void Si_el_codigo_ya_no_casa_con_el_hash_la_ficha_lo_dice()
    {
        Finding f = Seed();
        string abs = Path.Combine(_clone, "src", "Repositorio.cs");
        File.WriteAllText(abs, Fuente.Replace(
            "var stream = File.OpenWrite(dato);", "using var stream = File.OpenWrite(dato);"));

        FindingDetailViewModel vm = Open(f);

        vm.SnippetState.Should().Be(SnippetState.Cambiado);
        vm.HasSnippetNotice.Should().BeTrue();
        vm.SnippetNotice.Should().Contain("ha cambiado");
        vm.SnippetNoticeOffersVerify.Should().BeTrue("verificar es lo que re-ancla o cierra esto");
    }

    /// <summary>Moverse no es cambiar: se enseña la posición nueva, sin gritar.</summary>
    [Fact]
    public void Si_el_codigo_solo_se_movio_la_ficha_lo_sigue()
    {
        Finding f = Seed();
        string abs = Path.Combine(_clone, "src", "Repositorio.cs");
        File.WriteAllText(abs, "// una línea nueva arriba del todo\n" + Fuente);

        FindingDetailViewModel vm = Open(f);

        vm.SnippetState.Should().Be(SnippetState.Movido);
        vm.SnippetHighlightLine.Should().Be(LineaDelHallazgo + 1);
        vm.SnippetNotice.Should().Contain("se ha movido");
        vm.Snippet.Should().Contain("File.OpenWrite");
    }

    [Fact]
    public void Sin_clon_local_no_se_inventa_codigo_se_dice_a_que_commit_estaba_anclado()
    {
        Finding f = Seed();
        _machines.SetClonePath("alpha", string.Empty);

        FindingDetailViewModel vm = Open(f);

        vm.SnippetState.Should().Be(SnippetState.SinClon);
        vm.HasSnippet.Should().BeFalse();
        vm.SnippetNotice.Should().Contain("01234567", "el commit al que quedó anclado");
        vm.SnippetNoticeOffersVerify.Should().BeFalse("verificar sin clon no puede hacer nada");
    }

    [Fact]
    public void Si_el_fichero_ya_no_existe_se_dice_y_se_ofrece_verificar()
    {
        Finding f = Seed();
        File.Delete(Path.Combine(_clone, "src", "Repositorio.cs"));

        FindingDetailViewModel vm = Open(f);

        vm.SnippetState.Should().Be(SnippetState.FicheroNoEncontrado);
        vm.SnippetNotice.Should().Contain("src/Repositorio.cs");
        vm.SnippetNoticeOffersVerify.Should().BeTrue();
    }

    [Fact]
    public void Sin_hash_guardado_se_muestra_la_linea_anclada_sin_alarma()
    {
        Finding f = Seed(snippetHash: string.Empty);

        FindingDetailViewModel vm = Open(f);

        vm.SnippetState.Should().Be(SnippetState.Anclado);
        vm.HasSnippetNotice.Should().BeFalse();
        vm.SnippetHighlightLine.Should().Be(LineaDelHallazgo);
    }

    // =========================================================== §6 toasts y estados pegados

    /// <summary>
    /// Cada acción de la ficha avisa por el sistema de toasts de F5.3. El texto de estado al pie
    /// —que además se quedaba pegado— ya no existe: no hay dónde dejar un mensaje colgado.
    /// </summary>
    [Fact]
    public void Toda_accion_avisa_por_toast_y_la_ficha_no_tiene_texto_de_estado()
    {
        typeof(FindingDetailViewModel).GetProperty("StatusMessage")
            .Should().BeNull("el texto de estado incrustado desaparece con F5.5 §6");

        Finding f = Seed();
        FindingDetailViewModel vm = Open(f);
        int before = _toasts.Items.Count;

        vm.SilenceCommand.Execute(null);

        _toasts.Items.Count.Should().BeGreaterThan(before);
    }

    /// <summary>Los avisos caducan solos: por construcción, nada puede quedarse «Abriendo…».</summary>
    [Fact]
    public void Los_avisos_de_la_ficha_caducan_solos()
    {
        FindingDetailViewModel vm = Open(Seed());
        vm.SilenceCommand.Execute(null);

        _toasts.Items.Should().NotBeEmpty();
        _toasts.SweepAt(DateTimeOffset.UtcNow + ToastCenter.Lifetime + TimeSpan.FromSeconds(1));

        _toasts.Items.Should().BeEmpty("un aviso de la ficha no es un elemento fijo de la interfaz");
    }

    /// <summary>
    /// El arranque del editor tiene tope: si no vuelve, se resuelve en fallo. Antes se quedaba
    /// «Abriendo en el editor…» para siempre porque nadie ponía un límite.
    /// </summary>
    [Fact]
    public async Task El_arranque_del_editor_falla_por_tiempo_en_vez_de_colgarse()
    {
        // El arranque simulado se libera SIEMPRE —por el `using`, pase lo que pase con la
        // aserción— y además tiene su propio tope. Un arnés que deja un hilo del pool bloqueado
        // para siempre no prueba que el código no se cuelgue: cuelga el testhost.
        using var lento = new ManualResetEventSlim(false);

        bool opened = await EditorLauncher.WithTimeout(
            () => lento.Wait(TimeSpan.FromSeconds(5)),
            TimeSpan.FromMilliseconds(120));

        try
        {
            opened.Should().BeFalse("el arranque no volvió dentro del tope");
        }
        finally
        {
            lento.Set();
        }
    }

    [Fact]
    public async Task Un_arranque_que_funciona_devuelve_exito()
        => (await EditorLauncher.WithTimeout(() => true, TimeSpan.FromSeconds(5))).Should().BeTrue();

    [Fact]
    public async Task Abrir_en_el_editor_sin_clon_avisa_del_fallo()
    {
        Finding f = Seed();
        _machines.SetClonePath("alpha", string.Empty);
        FindingDetailViewModel vm = Open(f);

        await vm.OpenInEditorCommand.ExecuteAsync(null);

        LastToast().Should().Contain("No se pudo abrir el editor");
    }

    // =========================================================== §1 y §4 — lo que se fue de la vista

    /// <summary>
    /// La asignación sale de la VISTA, no del modelo ni del view-model: la decisión del usuario es
    /// que no se usa, y el camino de vuelta tiene que seguir existiendo.
    /// </summary>
    [Fact]
    public void La_asignacion_desaparece_de_la_vista_pero_no_del_modelo()
    {
        string xaml = Markup(DetailXaml());

        xaml.Should().NotContain("ApplyAssignCommand", "los controles de asignar salen de la ficha");
        xaml.Should().NotContain("Asignar a");

        typeof(FindingDetailViewModel).GetProperty("Assignee").Should().NotBeNull();
        Finding f = Seed();
        FindingDetailViewModel vm = Open(f);
        vm.Assignee = "maria";
        vm.ApplyAssignCommand.Execute(null);
        _hub.Store.TryReadFinding("alpha", f.Id.ToString())!.Assignee.Should().Be("maria");
    }

    [Fact]
    public void La_ficha_no_tiene_ningun_texto_de_estado_incrustado()
        => Markup(DetailXaml()).Should().NotContain("StatusMessage");

    /// <summary>El ruleId ya no flota bajo el título: la cabecera es de chips.</summary>
    [Fact]
    public void La_cabecera_no_lleva_el_ruleId_suelto()
    {
        string cabecera = Markup(DetailXaml());
        int fin = cabecera.IndexOf("El hallazgo", StringComparison.Ordinal);
        fin.Should().BeGreaterThan(0);

        cabecera[..fin].Should().NotContain("Finding.RuleId");
    }

    [Fact]
    public void La_ficha_se_dibuja_en_dos_columnas_con_la_lateral_desmontable()
    {
        string xaml = DetailXaml();

        xaml.Should().Contain("x:Name=\"MainStack\"").And.Contain("x:Name=\"SideStack\"");
        xaml.Should().Contain("x:Name=\"SideColumn\"").And.Contain("x:Name=\"SideScroll\"");
    }

    /// <summary>
    /// La caducidad deja de ser un numérico huérfano: lleva su nombre y su unidad, y dice qué
    /// significa el cero.
    /// </summary>
    [Fact]
    public void La_caducidad_del_silencio_esta_etiquetada()
        => DetailXaml().Should().Contain("Caducidad (días) — 0 = permanente");

    /// <summary>
    /// La resolución manual llega plegada y con su advertencia: es excepcional y no debe competir
    /// visualmente con silenciar o verificar.
    /// </summary>
    [Fact]
    public void La_resolucion_manual_viene_plegada_y_avisada()
    {
        string xaml = DetailXaml();

        xaml.Should().Contain("Cierra este hallazgo por decisión humana, sin auditoría");
        xaml.Should().Contain("Requiere justificación y queda registrado con tu nombre");
        xaml.Should().MatchRegex("<Expander[^>]*IsExpanded=\"\\{Binding ManualResolutionExpanded\\}\"");

        new FindingDetailViewModel(
            _hub, _governance, _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines), _toasts)
            .ManualResolutionExpanded.Should().BeFalse("plegada por defecto");
    }

    /// <summary>
    /// El ancho al que la ficha pasa a una columna, fijado con los números de la carcasa: la
    /// ventana por defecto (1340) va a dos columnas y el mínimo (900) a una, en el mismo orden.
    /// </summary>
    [Fact]
    public void El_colapso_a_una_columna_ocurre_donde_debe()
    {
        Views.FindingDetailView.FitsTwoColumnsInWindow(1340).Should().BeTrue("la ventana por defecto");
        Views.FindingDetailView.FitsTwoColumnsInWindow(900).Should().BeFalse("el mínimo de la ventana");
        Views.FindingDetailView.FitsTwoColumns(Views.FindingDetailView.TwoColumnBreakpoint).Should().BeTrue();
        Views.FindingDetailView.FitsTwoColumns(Views.FindingDetailView.TwoColumnBreakpoint - 1).Should().BeFalse();
    }

    // ---------------------------------------------------------------- utillaje de XAML

    /// <summary>
    /// Igual que en F5.3: hay invariantes que son de la PLANTILLA —qué controles existen y cuáles
    /// dejaron de existir— y no tienen estado observable que interrogar. Se leen del fichero.
    /// </summary>
    private static string DetailXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        string path = Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", "FindingDetailView.xaml");
        File.Exists(path).Should().BeTrue($"se esperaba la ficha en {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Sin comentarios: lo que documenta la decisión no cuenta como interfaz.</summary>
    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
}
