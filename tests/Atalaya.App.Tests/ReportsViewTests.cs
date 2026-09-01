using System.Text.RegularExpressions;
using System.Windows.Documents;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;
using WpfTable = System.Windows.Documents.Table;

namespace Atalaya.App.Tests;

/// <summary>
/// F6.3 — la vista Informes: la lista, sus filtros, el visor y la descarga.
/// <para>
/// Lo que estos tests protegen no es una pantalla bonita: es que la lista salga de los FICHEROS
/// —lo que no tiene informe no aparece, y lo que no tiene sesión sí— y que ninguna columna se
/// invente un cero donde no hay medida.
/// </para>
/// </summary>
public sealed class ReportsViewTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public ReportsViewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f63", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        TestRates.Seed(_hub);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        App("app", "App");
    }

    // =============================================================== Utilidades

    private void App(string slug, string name)
        => _hub.Store.WriteApp(new AppConfig { Slug = slug, Name = name, RepoUrl = $"u/{slug}", CurrentCycle = 1 });

    /// <summary>Una sesión escrita en el hub. Devuelve su id, que es el nombre de su informe.</summary>
    private string Session(
        string slug,
        int daysAgo = 1,
        AuditMode mode = AuditMode.Lotes,
        string by = "alvaro",
        int units = 2,
        int nuevos = 3,
        int resueltos = 1,
        decimal? cost = 4.5m,
        bool report = true,
        string markdown = "# Informe de sesión — App\n\nCuerpo.")
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = mode,
            By = by,
            Machine = "PC",
            StartedUtc = Now.AddDays(-daysAgo),
            EndedUtc = Now.AddDays(-daysAgo).AddHours(1),
            CycleN = 2,
            Counters = new SessionCounters { New = nuevos, Resolved = resueltos },
        };

        for (int i = 0; i < units; i++)
        {
            session.Units.Add(new UnitVerdictRecord($"src/U{i}.cs", "src", "auditada", null));
        }

        TestRates.CostAs(session, cost, inputTokens: 100);
        _hub.Store.WriteSession(session);

        string id = session.Id.ToString();
        if (report)
        {
            _hub.Store.WriteReport(slug, id, markdown);
        }

        return id;
    }

    private ReportsQuery Query() => new(_hub);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Un fichero bloqueado no puede tumbar la suite.
        }
    }

    // =============================================================== §1 · la lista

    /// <summary>
    /// La fuente son los ficheros de <c>reports/</c>. Una sesión que no llegó a escribir su
    /// informe no tiene nada que abrir, y una fila que no lleva a ningún sitio es peor que no
    /// tener fila.
    /// </summary>
    [Fact]
    public void Solo_salen_las_sesiones_que_dejaron_informe()
    {
        string conInforme = Session("app", daysAgo: 1);
        Session("app", daysAgo: 2, report: false);

        IReadOnlyList<ReportEntry> all = Query().All();

        all.Should().ContainSingle();
        all[0].ReportId.Should().Be(conInforme);
        _hub.Store.ListSessions("app").Should().HaveCount(2, "las dos sesiones siguen ahí; solo una tiene informe");
    }

    [Fact]
    public void Cada_informe_trae_los_metadatos_de_su_sesion()
    {
        Session("app", daysAgo: 3, by: "marta", units: 7, nuevos: 4, resueltos: 2, cost: 12.25m);

        ReportEntry entry = Query().All().Single();

        entry.AppName.Should().Be("App");
        entry.By.Should().Be("marta");
        entry.ModeLabel.Should().Be("Lotes");
        entry.Units.Should().Be(7);
        entry.New.Should().Be(4);
        entry.Resolved.Should().Be(2);
        entry.Cost.Should().Be(12.25m);
        entry.When.Should().BeCloseTo(Now.AddDays(-3), TimeSpan.FromSeconds(5));
        entry.DateSource.Should().Be(ReportDateSource.Session);
        entry.HasSession.Should().BeTrue();
    }

    [Fact]
    public void La_lista_va_de_lo_mas_reciente_a_lo_mas_antiguo()
    {
        string viejo = Session("app", daysAgo: 30);
        string nuevo = Session("app", daysAgo: 1);
        string medio = Session("app", daysAgo: 10);

        Query().All().Select(e => e.ReportId).Should().Equal(nuevo, medio, viejo);
    }

    /// <summary>
    /// Los tres tipos conviven en la misma lista, cada uno con su insignia. El cierre escribe un
    /// consolidado y el reset es un evento de sistema: llamarlos «sesión» a los dos sería perder
    /// justo la distinción por la que existe el filtro de tipo.
    /// </summary>
    [Fact]
    public void El_cierre_es_consolidado_y_el_reset_es_operaciones()
    {
        Session("app", daysAgo: 1, mode: AuditMode.Lotes);
        Session("app", daysAgo: 2, mode: AuditMode.Cierre, markdown: "# Cierre de ciclo 1 — App");
        Session("app", daysAgo: 3, mode: AuditMode.Reset, markdown: "# Reset — App");
        Session("app", daysAgo: 4, mode: AuditMode.Verify);

        var kinds = Query().All().ToDictionary(e => e.ModeLabel!, e => e.Kind);

        kinds["Lotes"].Should().Be(ReportKind.Sesion);
        kinds["Verify"].Should().Be(ReportKind.Sesion, "verificar también es auditar");
        kinds["Cierre de ciclo"].Should().Be(ReportKind.Consolidado);
        kinds["Reset de auditoría"].Should().Be(ReportKind.Operaciones);
    }

    /// <summary>
    /// Un informe traído de v4 no tiene sesión: su nombre no es un ULID y nadie registró quién lo
    /// firmó. Lo que se enseña es lo que ÉL declara, y lo que no declara se queda vacío — nunca en
    /// cero, que se leería como una medida (D-318).
    /// </summary>
    [Fact]
    public void Un_informe_sin_sesion_se_describe_por_su_propia_cabecera()
    {
        _hub.Store.WriteReport("app", "HISTORICO", string.Join("\n", new[]
        {
            "# Informe de sesión — App",
            "",
            "- **Modo**: Integral",
            "- **Fecha**: 2026-02-01 09:30 UTC",
            "- **Autor**: pepe (PC-07)",
            "",
            "Cuerpo del informe importado.",
        }));

        ReportEntry entry = Query().All().Single();

        entry.HasSession.Should().BeFalse();
        entry.Kind.Should().Be(ReportKind.Sesion, "su título dice qué es");
        entry.By.Should().Be("pepe", "la máquina no es el autor");
        entry.ModeLabel.Should().Be("Integral");
        entry.When.Should().Be(new DateTimeOffset(2026, 2, 1, 9, 30, 0, TimeSpan.Zero));
        entry.DateSource.Should().Be(ReportDateSource.Header);

        entry.Units.Should().BeNull("nadie declaró cuántas unidades procesó");
        entry.New.Should().BeNull();
        entry.Cost.Should().BeNull();
    }

    /// <summary>Sin cabecera reconocible tampoco se afirma de más: se queda en «operaciones».</summary>
    [Fact]
    public void Un_informe_que_no_dice_lo_que_es_no_se_hace_pasar_por_una_sesion()
    {
        _hub.Store.WriteReport("app", "NOTAS", "Algo suelto, sin título.");

        Query().All().Single().Kind.Should().Be(ReportKind.Operaciones);
    }

    /// <summary>
    /// El combo de usuarios se puebla con quien de verdad firma algo de la lista. Un combo con
    /// nombres que no filtran nada es una promesa falsa.
    /// </summary>
    [Fact]
    public void Los_combos_se_pueblan_con_lo_que_hay()
    {
        App("otra", "Otra");
        Session("app", by: "alvaro");
        Session("otra", by: "marta");
        Session("app", by: "alvaro", daysAgo: 5);

        ReportsQuery q = Query();

        q.Authors().Should().Equal("alvaro", "marta");
        q.Apps().Select(a => a.Name).Should().Equal("App", "Otra");
    }

    // =============================================================== §1 · filtros

    [Fact]
    public void Los_filtros_se_combinan()
    {
        App("otra", "Otra");
        string objetivo = Session("app", by: "marta", mode: AuditMode.Lotes, daysAgo: 2);
        Session("app", by: "alvaro", mode: AuditMode.Lotes, daysAgo: 2);
        Session("otra", by: "marta", mode: AuditMode.Lotes, daysAgo: 2);
        Session("app", by: "marta", mode: AuditMode.Cierre, daysAgo: 2, markdown: "# Cierre de ciclo 1 — App");

        ReportsQuery q = Query();

        q.Filter(new ReportsFilter(Slug: "app")).Should().HaveCount(3);
        q.Filter(new ReportsFilter(By: "marta")).Should().HaveCount(3);
        q.Filter(new ReportsFilter(Kind: ReportKind.Consolidado)).Should().ContainSingle();

        var combinado = q.Filter(new ReportsFilter(Slug: "app", By: "marta", Kind: ReportKind.Sesion));
        combinado.Should().ContainSingle();
        combinado[0].ReportId.Should().Be(objetivo);
    }

    [Fact]
    public void El_rango_de_fechas_deja_fuera_lo_de_antes_y_lo_de_despues()
    {
        string reciente = Session("app", daysAgo: 2);
        Session("app", daysAgo: 45);
        Session("app", daysAgo: 200);

        ReportsQuery q = Query();

        q.Filter(new ReportsFilter(From: Now.AddDays(-7))).Select(e => e.ReportId).Should().Equal(reciente);
        q.Filter(new ReportsFilter(From: Now.AddDays(-90))).Should().HaveCount(2);
        q.Filter(new ReportsFilter(To: Now.AddDays(-100))).Should().ContainSingle("solo el más viejo cae antes del corte");
        q.Filter(ReportsFilter.None).Should().HaveCount(3);
    }

    /// <summary>
    /// La razón de ser de la búsqueda: dar con el informe que MENCIONA algo, no con el que se
    /// llama así. Es lo que permite «busca ReadCSV» sin saber en qué sesión salió.
    /// </summary>
    [Fact]
    public void La_busqueda_mira_dentro_del_informe()
    {
        string conCsv = Session("app", daysAgo: 1,
            markdown: "# Informe de sesión — App\n\n## Hallazgos\n\n- `ReadCSV` no libera el lector.");
        Session("app", daysAgo: 2, markdown: "# Informe de sesión — App\n\n- Otra cosa distinta.");

        ReportsQuery q = Query();

        q.Filter(new ReportsFilter(Search: "readcsv")).Select(e => e.ReportId).Should().Equal(conCsv);
        q.Filter(new ReportsFilter(Search: "no libera")).Should().ContainSingle();
        q.Filter(new ReportsFilter(Search: "presupuesto")).Should().BeEmpty();
    }

    /// <summary>
    /// Lo que se busca lo escribió un modelo en español. Obligar a teclear la tilde exacta de lo
    /// que uno está viendo en pantalla es una forma tonta de no encontrar nada.
    /// </summary>
    [Fact]
    public void La_busqueda_no_distingue_tildes_ni_mayusculas()
    {
        Session("app", markdown: "# Informe de sesión — App\n\nDuplicación de código en la Sesión.");

        ReportsQuery q = Query();

        q.Filter(new ReportsFilter(Search: "duplicacion")).Should().ContainSingle();
        q.Filter(new ReportsFilter(Search: "DUPLICACIÓN")).Should().ContainSingle();
        q.Filter(new ReportsFilter(Search: "sesion")).Should().ContainSingle();
    }

    [Fact]
    public void La_busqueda_tambien_encuentra_por_aplicacion_y_por_autor()
    {
        App("xblast", "XBlast");
        Session("xblast", by: "marta", markdown: "# Informe de sesión — XBlast\n\nNada reseñable.");

        ReportsQuery q = Query();

        q.Filter(new ReportsFilter(Search: "xblast")).Should().ContainSingle();
        q.Filter(new ReportsFilter(Search: "marta")).Should().ContainSingle();
    }

    // =============================================================== §1 · el view-model

    [Fact]
    public async Task La_vista_arranca_sin_filtros_y_los_limpia_de_vuelta()
    {
        App("otra", "Otra");
        Session("app", by: "alvaro", daysAgo: 1);
        Session("otra", by: "marta", daysAgo: 200);

        ReportsViewModel vm = TestFactory.Reports(_hub);
        await vm.LoadAsync();

        vm.Rows.Should().HaveCount(2);
        vm.HasActiveFilters.Should().BeFalse("«Todo» y «Todas» son el arranque");
        vm.ResultsSummary.Should().Be("2 informes");

        vm.SelectedApp = vm.AppOptions.Single(o => o.Slug == "app");
        vm.SelectedRange = vm.RangeOptions.Single(o => o.Days == 7);
        vm.Rows.Should().ContainSingle();
        vm.HasActiveFilters.Should().BeTrue();

        vm.ClearFiltersCommand.Execute(null);
        vm.Rows.Should().HaveCount(2);
        vm.HasActiveFilters.Should().BeFalse();
    }

    /// <summary>El rango a mano gana al preset, y su «hasta» incluye el día que se escribe.</summary>
    [Fact]
    public async Task El_rango_a_mano_incluye_el_dia_de_su_extremo()
    {
        ReportsViewModel vm = TestFactory.Reports(_hub);
        await vm.LoadAsync();

        vm.UseCustomRange = true;
        vm.CustomFrom = new DateTime(2026, 7, 1);
        vm.CustomTo = new DateTime(2026, 7, 31);

        ReportsFilter filter = vm.BuildFilter();
        filter.From.Should().Be(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        filter.To.Should().Be(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            "«hasta el 31» tiene que dejar dentro el 31 entero");
        vm.UsePresetRange.Should().BeFalse("con el rango a mano, el combo de presets no manda");
    }

    /// <summary>
    /// Sin resultados se dice QUÉ pasa: un hub recién estrenado y unos filtros demasiado estrechos
    /// no se arreglan igual.
    /// </summary>
    [Fact]
    public async Task El_estado_vacio_distingue_un_hub_vacio_de_un_filtro_estrecho()
    {
        ReportsViewModel vm = TestFactory.Reports(_hub);
        await vm.LoadAsync();

        vm.IsEmpty.Should().BeTrue();
        vm.EmptyMessage.Should().Contain("Todavía no hay informes");

        Session("app");
        await vm.LoadAsync();
        vm.SearchText = "algo que no está";

        vm.IsEmpty.Should().BeTrue();
        vm.EmptyMessage.Should().Contain("estos filtros");
    }

    /// <summary>
    /// Un compañero publica y el polling recarga la página: el informe nuevo aparece solo. Es lo
    /// que hace <c>LoadAsync</c> tirando la caché, no un evento que haya que estar escuchando.
    /// </summary>
    [Fact]
    public async Task Un_informe_publicado_por_otro_aparece_al_recargar()
    {
        Session("app", daysAgo: 2);
        ReportsViewModel vm = TestFactory.Reports(_hub);
        await vm.LoadAsync();
        vm.Rows.Should().ContainSingle();

        Session("app", daysAgo: 1, by: "marta");
        await vm.LoadAsync();

        vm.Rows.Should().HaveCount(2);
        vm.AuthorOptions.Select(o => o.Label).Should().Contain("marta", "el combo también se entera");
    }

    // =============================================================== §2 · el visor

    [Fact]
    public async Task Abrir_un_informe_lo_renderiza_y_volver_conserva_los_filtros()
    {
        Session("app", by: "marta", markdown: "# Informe de sesión — App\n\nUn párrafo.");
        Session("app", daysAgo: 9, by: "alvaro");

        ReportsViewModel vm = TestFactory.Reports(_hub);
        await vm.LoadAsync();
        vm.SelectedAuthor = vm.AuthorOptions.Single(o => o.Value == "marta");
        vm.Rows.Should().ContainSingle();

        vm.OpenCommand.Execute(vm.Rows[0]);

        vm.IsViewing.Should().BeTrue();
        vm.Document.Should().NotBeNull();
        vm.ViewerTitle.Should().Contain("Informe de sesión");
        vm.ViewerSubtitle.Should().Contain("App").And.Contain("marta");
        vm.CanOpenFindings.Should().BeTrue("es un informe de sesión");

        vm.BackCommand.Execute(null);

        vm.IsViewing.Should().BeFalse();
        vm.SelectedAuthor!.Value.Should().Be("marta", "volver no limpia nada");
        vm.Rows.Should().ContainSingle();
    }

    /// <summary>Un consolidado no habla de una tanda de hallazgos: no ofrece ese enlace.</summary>
    [Fact]
    public async Task Un_consolidado_no_ofrece_el_enlace_a_los_hallazgos_de_la_sesion()
    {
        Session("app", mode: AuditMode.Cierre, markdown: "# Cierre de ciclo 1 — App");

        ReportsViewModel vm = TestFactory.Reports(_hub);
        await vm.LoadAsync();
        vm.OpenCommand.Execute(vm.Rows[0]);

        vm.CanOpenFindings.Should().BeFalse();
    }

    [Fact]
    public async Task El_enlace_a_hallazgos_abre_V3_filtrado_por_esa_aplicacion()
    {
        App("xblast", "XBlast");
        Session("xblast");

        var findings = new FindingsViewModel(
            _hub, new NavigationService(new EmptyProvider()), _settings, new GroupExpansionMemory());
        NavigationService navigation = TestFactory.NavigationWith(findings);
        ReportsViewModel vm = TestFactory.Reports(_hub, navigation);
        await vm.LoadAsync();
        vm.OpenCommand.Execute(vm.Rows[0]);

        await vm.OpenFindingsCommand.ExecuteAsync(null);

        navigation.Current.Should().BeSameAs(findings);
        findings.SelectedApp!.Slug.Should().Be("xblast");
    }

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Un enlace que llega de fuera —Métricas, «Última sesión»— a un informe que no está no puede
    /// quedarse callado: sin aviso es indistinguible de que el botón no funcione.
    /// </summary>
    [Fact]
    public async Task Un_enlace_a_un_informe_que_no_existe_lo_dice()
    {
        var toasts = new ToastCenter();
        ReportsViewModel vm = TestFactory.Reports(_hub, toasts: toasts);
        vm.ShowReport("app", "01NOEXISTE");

        await vm.LoadAsync();

        vm.IsViewing.Should().BeFalse();
        toasts.Items.Should().ContainSingle(t => t.Text.Contains("no está en el hub"));
    }

    // =============================================================== §2 · la descarga

    [Fact]
    public async Task El_nombre_por_defecto_de_la_descarga_dice_de_que_informe_es()
    {
        Session("app", daysAgo: 0);

        var saver = new TestFactory.RecordingFileSaver(null);
        ReportsViewModel vm = TestFactory.Reports(_hub, saver: saver);
        await vm.LoadAsync();
        vm.OpenCommand.Execute(vm.Rows[0]);
        vm.DownloadCommand.Execute(null);

        string suggested = saver.Suggested.Single();
        suggested.Should().StartWith("atalaya-app-sesión-").And.EndWith(".md");
        suggested.Should().Contain(DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task Descargar_copia_el_markdown_tal_cual_y_lo_dice()
    {
        const string Markdown = "# Informe de sesión — App\n\n| a | b |\n| - | - |\n| 1 | 2 |\n";
        Session("app", markdown: Markdown);

        string target = Path.Combine(_root, "descargas", "informe.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var toasts = new ToastCenter();
        ReportsViewModel vm = TestFactory.Reports(_hub, saver: new TestFactory.RecordingFileSaver(target), toasts: toasts);
        await vm.LoadAsync();
        vm.OpenCommand.Execute(vm.Rows[0]);
        vm.DownloadCommand.Execute(null);

        File.Exists(target).Should().BeTrue();
        File.ReadAllText(target).Should().Be(Markdown.Replace("\r\n", "\n"),
            "esta vista solo lee: no reescribe un informe ni para guardarlo");
        toasts.Items.Should().ContainSingle(t => t.Text.Contains("guardado"));
    }

    [Fact]
    public async Task Cancelar_la_descarga_no_escribe_nada()
    {
        Session("app");

        var toasts = new ToastCenter();
        ReportsViewModel vm = TestFactory.Reports(_hub, saver: new TestFactory.RecordingFileSaver(null), toasts: toasts);
        await vm.LoadAsync();
        vm.OpenCommand.Execute(vm.Rows[0]);
        vm.DownloadCommand.Execute(null);

        toasts.Items.Should().BeEmpty("cancelar no es un resultado que anunciar");
    }

    // =============================================================== §2 · el render

    /// <summary>
    /// La razón de haber escrito el renderizador: una tabla de markdown tiene que salir como una
    /// TABLA, con sus columnas repartidas, y no como un párrafo con tuberías.
    /// </summary>
    [Fact]
    public void Una_tabla_de_markdown_sale_como_una_tabla_de_verdad()
    {
        FlowDocument doc = MarkdownFlowDocument.Build(string.Join("\n", new[]
        {
            "# Informe",
            "",
            "| Unidad | Veredicto | LOC |",
            "| ------ | --------- | --: |",
            "| A.cs   | auditada  | 120 |",
            "| B.cs   | pendiente | 980 |",
        }));

        WpfTable table = doc.Blocks.OfType<WpfTable>().Single();
        table.Columns.Should().HaveCount(3);
        table.RowGroups.Single().Rows.Should().HaveCount(3, "la cabecera y sus dos filas");
        table.RowGroups.Single().Rows[0].FontWeight.Should().Be(System.Windows.FontWeights.SemiBold);
        table.RowGroups.Single().Rows[1].Cells.Should().HaveCount(3);

        // La alineación declarada por los guiones se respeta: los números a la derecha.
        table.RowGroups.Single().Rows[1].Cells[2].TextAlignment.Should().Be(System.Windows.TextAlignment.Right);
    }

    [Fact]
    public void El_render_reconoce_encabezados_listas_y_codigo()
    {
        FlowDocument doc = MarkdownFlowDocument.Build(string.Join("\n", new[]
        {
            "# Título",
            "",
            "Un párrafo con **negrita** y `código`.",
            "",
            "- uno",
            "- dos",
            "",
            "```",
            "var x = 1;",
            "```",
        }));

        doc.Blocks.OfType<Paragraph>().First().FontSize.Should().BeGreaterThan(doc.FontSize, "un H1 es más grande");
        doc.Blocks.OfType<List>().Single().ListItems.Should().HaveCount(2);

        Paragraph code = doc.Blocks.OfType<Paragraph>().Last();
        code.FontFamily.Source.Should().Contain("Mono");
        new TextRange(code.ContentStart, code.ContentEnd).Text.Should().Contain("var x = 1;");
    }

    /// <summary>
    /// Los enlaces del informe salen FUERA. Aquí se comprueba lo que se puede comprobar sin abrir
    /// un navegador: que se pinta como enlace y que quien lo pulsa recibe la URL.
    /// </summary>
    [Fact]
    public void Un_enlace_del_informe_se_entrega_a_quien_sabe_abrirlo_fuera()
    {
        var abiertos = new List<string>();
        FlowDocument doc = MarkdownFlowDocument.Build(
            "Ver [la guía](https://example.com/guia).", abiertos.Add);

        Hyperlink link = doc.Blocks.OfType<Paragraph>()
            .SelectMany(p => p.Inlines)
            .SelectMany(Flatten)
            .OfType<Hyperlink>()
            .Single();

        link.DoClick();
        abiertos.Should().Equal("https://example.com/guia");
    }

    private static IEnumerable<System.Windows.Documents.Inline> Flatten(System.Windows.Documents.Inline inline)
    {
        yield return inline;
        if (inline is Span span)
        {
            foreach (System.Windows.Documents.Inline child in span.Inlines.SelectMany(Flatten))
            {
                yield return child;
            }
        }
    }

    [Fact]
    public void Un_informe_vacio_no_revienta_el_visor()
    {
        MarkdownFlowDocument.Build(null).Blocks.Should().BeEmpty();
        MarkdownFlowDocument.Build(string.Empty).Blocks.Should().BeEmpty();
    }

    // =============================================================== Tema

    /// <summary>
    /// Claro y oscuro. No se comprueba mirando píxeles: se comprueba que NINGÚN color esté escrito
    /// a mano — ni en el renderizador ni en la vista—, porque un color fijo es exactamente lo que
    /// se ve mal en uno de los dos temas. Es la misma disciplina de F5.9 §2 y de ButtonForeground.
    /// </summary>
    [Fact]
    public void Ni_el_visor_ni_la_vista_fijan_un_solo_color_a_mano()
    {
        string renderer = Source("src/Atalaya.App/Services/MarkdownFlowDocument.cs");
        string xaml = Source("src/Atalaya.App/Views/ReportsView.xaml");

        Regex.Matches(renderer, "#[0-9A-Fa-f]{6}").Should().BeEmpty("el renderizador pide pinceles al tema");
        renderer.Should().Contain("SetResourceReference", "y los pide de forma que sigan al tema si cambia");
        Regex.Matches(xaml, "Color=\"#|Background=\"#|Foreground=\"#").Should().BeEmpty();

        // Y los pinceles que usa son los del tema de la aplicación, no inventados.
        foreach (string key in new[]
                 {
                     "TextFillColorPrimaryBrush", "CardStrokeColorDefaultBrush", "SubtleFillColorSecondaryBrush",
                 })
        {
            renderer.Should().Contain(key);
        }
    }

    // =============================================================== La navegación

    [Fact]
    public void Informes_esta_en_el_rail_justo_debajo_de_Metricas()
    {
        string shell = Source("src/Atalaya.App/MainWindow.xaml");

        int metrics = shell.IndexOf("ShowMetricsCommand", StringComparison.Ordinal);
        int reports = shell.IndexOf("ShowReportsCommand", StringComparison.Ordinal);
        int newApp = shell.IndexOf("NewAppCommand", StringComparison.Ordinal);

        metrics.Should().BeGreaterThan(0);
        reports.Should().BeGreaterThan(metrics).And.BeLessThan(newApp);
        shell.Should().Contain("Content=\"Informes\"");

        // Y la página existe como destino: VM → vista, como todas las demás.
        Source("src/Atalaya.App/App.xaml").Should().Contain("vm:ReportsViewModel")
            .And.Contain("views:ReportsView");
    }

    /// <summary>
    /// Un <c>StaticResource</c> con una clave que no existe NO lo caza el compilador: revienta al
    /// abrir la página, en tiempo de ejecución y delante del usuario. Aquí se comprueba que todas
    /// las que pide esta vista estén definidas — en ella misma o en <c>App.xaml</c>.
    /// </summary>
    [Fact]
    public void Todas_las_claves_que_pide_la_vista_existen()
    {
        string xaml = Source("src/Atalaya.App/Views/ReportsView.xaml");
        string app = Source("src/Atalaya.App/App.xaml");

        var declared = Regex.Matches(xaml + app, "x:Key=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var used = Regex.Matches(xaml, "\\{StaticResource ([^}]+)\\}")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(k => !k.StartsWith("{x:Type", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        used.Should().NotBeEmpty("la vista usa recursos: si esto sale vacío, el test no está mirando");
        used.Except(declared).Should().BeEmpty("toda clave usada tiene que estar declarada");
    }

    private static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
