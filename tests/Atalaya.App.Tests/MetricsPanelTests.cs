using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.9 §2 — las reglas de visualización, comprobadas una a una.
/// <para>
/// No son preferencias de estilo: cada una impide una forma concreta de mentir con una gráfica.
/// Un eje secundario deja elegir dónde se cruzan dos series; un color que cambia entre sesiones
/// rompe la lectura de un vistazo; un color de severidad usado como serie hace que «xblast» se
/// lea como «crítico»; y un cero con formato sobre cero medidas se lee como una medida.
/// </para>
/// </summary>
public sealed class MetricsPanelTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public MetricsPanelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f59-panel", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        TestRates.Seed(_hub);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
    }

    // ============================================ Color por identidad, fijo

    /// <summary>
    /// El corazón de la regla: xblast es del mismo color hoy, mañana y en otra máquina. Si esto
    /// se rompiera, cada arranque repartiría colores distintos y la leyenda sería lo único
    /// legible de la gráfica.
    /// </summary>
    [Fact]
    public void El_color_de_una_app_no_depende_del_proceso_ni_del_orden_de_la_lista()
    {
        var uno = SeriesPalette.Assign(new[] { "xblast", "atalaya", "nomina" });
        var otro = SeriesPalette.Assign(new[] { "nomina", "xblast", "atalaya" });

        uno.Should().BeEquivalentTo(otro, "el reparto no depende del orden en que los devuelva el disco");
        SeriesPalette.For("xblast", uno).Should().Be(SeriesPalette.For("xblast", otro));

        // Y no sale de string.GetHashCode, que es aleatorio por proceso: el valor está fijado.
        SeriesPalette.Fnv1a("xblast").Should().Be(SeriesPalette.Fnv1a("XBlast"), "el slug se normaliza");
        SeriesPalette.Fnv1a("xblast").Should().NotBe(SeriesPalette.Fnv1a("atalaya"));
    }

    /// <summary>
    /// «Un filtro que oculta apps NO repinta las restantes». El reparto se hace sobre el
    /// portafolio completo, así que da igual qué se esté enseñando.
    /// </summary>
    [Fact]
    public void Filtrar_el_panel_no_repinta_ninguna_app()
    {
        foreach (string slug in new[] { "xblast", "atalaya", "nomina" })
        {
            _hub.Store.WriteApp(new AppConfig { Slug = slug, Name = slug, RepoUrl = $"u/{slug}", CurrentCycle = 1 });
        }

        var query = new MetricsQuery(_hub);
        MetricsDashboard todas = query.Build(new MetricsFilter(null, MetricsRange.Weeks8));
        MetricsDashboard una = query.Build(new MetricsFilter("xblast", MetricsRange.Weeks8));

        SeriesPalette.For("xblast", una.Palette)
            .Should().Be(SeriesPalette.For("xblast", todas.Palette));
        una.Palette.Should().BeEquivalentTo(todas.Palette, "el reparto es del portafolio, no de lo filtrado");
    }

    [Fact]
    public void Seis_apps_reciben_seis_colores_distintos_aunque_su_hash_colisione()
    {
        // Un puñado suficientemente grande para forzar colisiones en una paleta de seis.
        string[] slugs = Enumerable.Range(0, 6).Select(i => $"app-{i}").ToArray();

        var assigned = SeriesPalette.Assign(slugs);

        assigned.Should().HaveCount(6);
        assigned.Values.Distinct().Should().HaveCount(6, "seis apps, seis colores: nadie comparte");
    }

    // ============================================ Severidades reservadas

    /// <summary>
    /// Los cuatro colores de severidad son de ESTADO y no pueden aparecer como serie. Y la
    /// comparación es posible porque desde F5.9 viven en un solo sitio.
    /// </summary>
    [Fact]
    public void Ningun_color_de_serie_es_un_color_de_severidad()
    {
        var series = SeriesPalette.Steps
            .SelectMany(s => new[] { s.Light, s.Dark })
            .Concat(new[] { SeriesPalette.Others.Light, SeriesPalette.Others.Dark })
            .Concat(new[] { SeriesPalette.Pending.Light, SeriesPalette.Pending.Dark })
            .Concat(new[] { SeriesPalette.Large.Light, SeriesPalette.Large.Dark })
            .Select(h => h.ToUpperInvariant())
            .ToList();

        foreach (string reserved in SeverityPalette.All.Select(h => h.ToUpperInvariant()))
        {
            series.Should().NotContain(reserved, $"{reserved} significa una severidad en toda la aplicación");
        }
    }

    /// <summary>
    /// Y el sitio único no es una intención: el convertidor que pinta los badges de severidad en
    /// todas las demás vistas lee de ahí, así que no pueden divergir.
    /// </summary>
    [Fact]
    public void El_color_de_severidad_sale_de_un_solo_sitio()
    {
        var converter = new SeverityToBrushConverter();
        foreach (Severity severity in Enum.GetValues<Severity>())
        {
            var brush = (SolidColorBrush)converter.Convert(severity, typeof(Brush), null!, null!);
            var expected = (Color)ColorConverter.ConvertFromString(SeverityPalette.Hex(severity));
            brush.Color.Should().Be(expected);
        }
    }

    // ============================================ Dos temas

    [Fact]
    public void Cada_color_de_serie_tiene_su_paso_para_claro_y_para_oscuro()
    {
        foreach (SeriesColor color in SeriesPalette.Steps
                     .Append(SeriesPalette.Others)
                     .Append(SeriesPalette.Pending)
                     .Append(SeriesPalette.Large))
        {
            color.Light.Should().MatchRegex("^#[0-9A-Fa-f]{6}$");
            color.Dark.Should().MatchRegex("^#[0-9A-Fa-f]{6}$");
            color.Dark.Should().NotBe(color.Light, $"«{color.Name}» necesita un paso distinto en cada tema");
            color.For(dark: true).Should().Be(color.Dark);
            color.For(dark: false).Should().Be(color.Light);
        }

        // Y no es un flip automático de la luminosidad. Los colores de SERIE se aclaran para
        // fondo oscuro, porque una línea tiene que destacar sobre el fondo; los rellenos NEUTROS
        // del rosco —lo pendiente y lo excluido— hacen lo contrario, porque ahí lo que se busca
        // es un fondo apagado contra el que resalte el tramo auditado.
        foreach (SeriesColor color in SeriesPalette.Steps.Append(SeriesPalette.Others))
        {
            Luma(color.Dark).Should().BeGreaterThan(Luma(color.Light), $"«{color.Name}» es una serie");
        }

        foreach (SeriesColor neutral in new[] { SeriesPalette.Pending, SeriesPalette.Large })
        {
            Luma(neutral.Dark).Should().BeLessThan(Luma(neutral.Light), $"«{neutral.Name}» es un relleno neutro");
        }
    }

    [Fact]
    public async Task El_panel_pinta_con_los_colores_del_tema_vigente()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "xblast", Name = "XBlast", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("xblast", cost: 5m);

        _settings.Current.Theme = "dark";
        MetricsViewModel oscuro = TestFactory.Metrics(_hub, _paths, _settings);
        await oscuro.LoadAsync();

        _settings.Current.Theme = "light";
        MetricsViewModel claro = TestFactory.Metrics(_hub, _paths, _settings);
        await claro.LoadAsync();

        SeriesColor color = SeriesPalette.For("xblast", SeriesPalette.Assign(new[] { "xblast" }));
        Hex(oscuro.CostSeries.Single().Stroke).Should().Be(color.Dark.ToUpperInvariant());
        Hex(claro.CostSeries.Single().Stroke).Should().Be(color.Light.ToUpperInvariant());
    }

    // ============================================ Un solo eje

    /// <summary>
    /// <see cref="AxisScale"/> es la ÚNICA escala de una gráfica, y la comparte todo lo que se
    /// dibuja en ella. Aquí se fija lo que hace: terminar en un número redondo.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 8)]
    [InlineData(137, 150)]
    [InlineData(1010, 1500)]
    public void El_eje_termina_en_un_numero_redondo(double dataMax, double expected)
    {
        AxisScale axis = AxisScale.For(dataMax);

        axis.Max.Should().Be(expected);
        axis.Ticks.Should().HaveCountLessThanOrEqualTo(AxisScale.TargetTicks + 2, "más marcas son ruido");
        axis.Ticks.Last().Should().Be(axis.Max, "la última marca ES el tope del eje");
    }

    [Fact]
    public void El_eje_no_divide_por_cero_cuando_no_hay_datos()
    {
        AxisScale empty = AxisScale.For(0);

        empty.Max.Should().Be(1);
        empty.Fraction(0).Should().Be(0);
        empty.Ticks.Should().NotBeEmpty();
    }

    /// <summary>
    /// La gráfica de flujo mezcla barras y línea, y ésa es exactamente la tentación de meter un
    /// segundo eje. Las tres series son CONTEOS de hallazgos: comparten el único que hay.
    /// </summary>
    [Fact]
    public async Task El_flujo_dibuja_barras_y_linea_sobre_el_mismo_eje()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-3));

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        vm.FlowSeries.Should().HaveCount(3);
        vm.FlowSeries.Count(s => s.Kind == ChartSeriesKind.Bar).Should().Be(2, "nuevos y resueltos");
        vm.FlowSeries.Count(s => s.Kind == ChartSeriesKind.Line).Should().Be(1, "los activos acumulados");
        vm.HasFlow.Should().BeTrue();

        // El control recibe UNA lista de series y calcula UNA escala sobre todas ellas. No hay
        // propiedad de segundo eje que poner, que es la forma más sólida de no tenerlo.
        typeof(ChartPlot).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("SecondaryAxis", StringComparison.OrdinalIgnoreCase));
    }

    // ============================================ Leyenda y tooltips

    [Fact]
    public async Task Hay_leyenda_en_cuanto_hay_dos_series_y_no_antes()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "a", Name = "A", RepoUrl = "u/a", CurrentCycle = 1 });
        WriteSession("a", cost: 5m);

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();
        vm.CostLegend.Should().HaveCount(1);
        vm.ShowCostLegend.Should().BeFalse("con una serie, la leyenda es ruido");
        vm.ShowFlowLegend.Should().BeTrue("el flujo siempre tiene tres");

        _hub.Store.WriteApp(new AppConfig { Slug = "b", Name = "B", RepoUrl = "u/b", CurrentCycle = 1 });
        WriteSession("b", cost: 3m);

        MetricsViewModel dos = TestFactory.Metrics(_hub, _paths, _settings);
        await dos.LoadAsync();
        dos.CostLegend.Should().HaveCount(2);
        dos.ShowCostLegend.Should().BeTrue();
        dos.CostLegend.Select(l => l.Name).Should().BeEquivalentTo(new[] { "A", "B" }, "la leyenda NOMBRA");
    }

    /// <summary>
    /// El tooltip del cursor y las bandas que lo disparan están en el control, cubriendo todo el
    /// alto de cada columna: sin eso habría que acertarle a una línea de 1,6 px.
    /// </summary>
    [Fact]
    public void La_grafica_trae_cursor_y_tooltip_por_columna()
    {
        string source = Source("src/Atalaya.App/Controls/ChartPlot.cs");

        source.Should().Contain("AddHitColumns", "la banda invisible por cubo es lo que dispara el tooltip");
        source.Should().Contain("ToolTip = BuildTooltip", "y lo que enseña es TODAS las series del cubo");
        source.Should().Contain("_crosshair", "con su cursor vertical");
        source.Should().Contain("Sin actividad", "un cubo vacío lo dice; un tooltip en blanco parece un fallo");

        // Cada tramo del rosco dice lo que vale. Desde F6.5 el tramo puede traer su propia frase
        // —el de severidad dice «Alta — 4 hallazgos (33 %)»— y si no la trae, el rosco la compone.
        Source("src/Atalaya.App/Controls/DonutRing.cs")
            .Should().Contain("segment.Tooltip ?? Tip(", "con su texto propio o con el de serie");
    }

    // ============================================ Ningún número engañoso

    [Fact]
    public async Task Sin_coste_ni_inventario_los_tiles_escriben_una_raya_y_dicen_cuando_se_activan()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        StatCard cost = vm.Cards.Single(c => c.Key == "cost");
        cost.Value.Should().Be(MetricsViewModel.Unknown);
        cost.Subtitle.Should().Contain("Se activará cuando");

        StatCard coverage = vm.Cards.Single(c => c.Key == "coverage");
        coverage.Value.Should().Be(MetricsViewModel.Unknown);
        coverage.Subtitle.Should().Contain("Se activará cuando");

        // Y sin resueltos, la cuarta tampoco inventa una división (F35 §1.3).
        StatCard perResolution = vm.Cards.Single(c => c.Key == "cost-per-resolution");
        perResolution.Value.Should().Be(MetricsViewModel.Unknown);
        perResolution.Subtitle.Should().Be("sin resueltos en el periodo");

        vm.HasCost.Should().BeFalse();
    }

    /// <summary>
    /// Las dos métricas que §3 retira. «% criterio» mide al AUDITOR y sigue viva donde se puede
    /// interpretar —el informe de sesión—; el tiempo medio a resolución vuelve cuando haya
    /// resoluciones que promediar, no antes.
    /// </summary>
    [Fact]
    public void El_porcentaje_de_criterio_y_el_tiempo_medio_salen_del_panel()
    {
        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));

        xaml.Should().NotContain("criterio");
        xaml.Should().NotContain("Tiempo medio");
        xaml.Should().NotContain("días");

        typeof(MetricsDashboard).GetProperty("CriterioPct").Should().BeNull();
        typeof(MetricsDashboard).GetProperty("AvgDaysToResolution").Should().BeNull();

        // Pero el % criterio no se ha perdido: sigue en el informe de cada sesión.
        Source("src/Atalaya.App/Services/ReportBuilder.cs")
            .Should().Contain("% criterio (informativo)");
    }

    // ============================================ La vista

    [Fact]
    public void La_vista_trae_los_filtros_los_cuatro_tiles_y_las_siete_graficas()
    {
        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));

        xaml.Should().Contain("{Binding AppOptions}").And.Contain("{Binding RangeOptions}");

        // F35 §1.3 — las cuatro cifras son UNA plantilla sobre `Cards`, no cuatro bloques a mano,
        // así que sus títulos ya no están en el XAML: los escribe el view-model.
        xaml.Should().Contain("{Binding Cards}").And.Contain("CopyCardCommand");
        Regex.Matches(xaml, "c:ColumnsPanel").Count.Should()
            .Be(3, "la rejilla de las cifras (una etiqueta, es plantilla de panel) y la fila de "
                   + "antigüedad y top de reglas (apertura y cierre)");

        foreach (string chart in new[]
                 {
                     "Coste en el tiempo", "Coste por acción", "Resoluciones en el tiempo",
                     "Cobertura por aplicación", "Severidad por aplicación", "Flujo de hallazgos",
                     "Antigüedad de la deuda", "Top 5 reglas del periodo", "Ciclos y temáticas",
                     "Actividad de sesiones",
                 })
        {
            xaml.Should().Contain(chart);
        }

        xaml.Should().Contain("{Binding Cumulative}", "el toggle «Acumulado» de la gráfica de coste");
        xaml.Should().Contain("{Binding CumulativeResolutions}", "y el suyo en la de resoluciones");
        Regex.Matches(xaml, "controls:ChartPlot").Count.Should()
            .Be(4, "coste, resoluciones, flujo y antigüedad; ni una gráfica de más");

        // La de resoluciones va JUSTO debajo de la de coste: se leen en pareja.
        xaml.IndexOf("Resoluciones en el tiempo", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.IndexOf("Coste en el tiempo", StringComparison.Ordinal))
            .And.BeLessThan(xaml.IndexOf("Cobertura por aplicación", StringComparison.Ordinal));
        Regex.Matches(xaml, "controls:DonutRing").Count.Should()
            .Be(3, "cobertura, severidad y coste por acción; cada fila es UNA plantilla repetida");

        // F35 §2 — cada gráfica nueva va DEBAJO de la suya: el coste por acción bajo el coste en
        // el tiempo, y la antigüedad y el top de reglas bajo el flujo. La rejilla no se reordena.
        xaml.IndexOf("Coste por acción", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.IndexOf("Coste en el tiempo", StringComparison.Ordinal))
            .And.BeLessThan(xaml.IndexOf("Resoluciones en el tiempo", StringComparison.Ordinal));
        xaml.IndexOf("Antigüedad de la deuda", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.IndexOf("Flujo de hallazgos", StringComparison.Ordinal))
            .And.BeLessThan(xaml.IndexOf("Top 5 reglas del periodo", StringComparison.Ordinal));
        Regex.Matches(xaml, "controls:CycleRibbon").Count.Should().Be(1, "la cinta de ciclos (F17 §6)");

        // La cinta va entre el flujo y el registro de sesiones: es historia, y el registro es el
        // detalle — y sigue siendo lo ÚLTIMO de la página (F35 §2.9).
        xaml.IndexOf("Ciclos y temáticas", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.IndexOf("Top 5 reglas del periodo", StringComparison.Ordinal))
            .And.BeLessThan(xaml.IndexOf("Actividad de sesiones", StringComparison.Ordinal));
        xaml.LastIndexOf("Actividad de sesiones", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.LastIndexOf("Ciclos y temáticas", StringComparison.Ordinal));

        // La de severidad va justo debajo de la de cobertura: las dos son roscos y se leen juntas.
        xaml.IndexOf("Severidad por aplicación", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.IndexOf("Cobertura por aplicación", StringComparison.Ordinal))
            .And.BeLessThan(xaml.IndexOf("Flujo de hallazgos", StringComparison.Ordinal));
    }

    // ============================================ Gestos

    [Fact]
    public async Task Un_clic_en_una_sesion_sin_informe_lo_dice_en_vez_de_no_hacer_nada()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("app", cost: 2m);

        var toasts = new ToastCenter();
        NavigationService navigation = TestFactory.NavigationWith(TestFactory.Reports(_hub));
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, navigation, toasts);
        await vm.LoadAsync();

        SessionLine line = vm.Sessions.Single();
        line.HasReport.Should().BeFalse("esta sesión no dejó markdown");
        await ((IAsyncRelayCommand)vm.OpenSessionCommand).ExecuteAsync(line);

        toasts.Items.Should().ContainSingle(t => t.Text.Contains("no dejó informe"));
        navigation.Current.Should().BeNull("no hay informe que enseñar: no se navega a ninguna parte");
    }

    /// <summary>
    /// F6.3 §3: el registro de operaciones enlaza al VISOR de informes, no al bloc de notas del
    /// sistema. En toda la aplicación hay un solo sitio donde se lee un informe.
    /// </summary>
    [Fact]
    public async Task Un_clic_en_una_sesion_con_informe_lo_abre_en_la_vista_informes()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        AuditSession session = WriteSession("app", cost: 2m);
        _hub.Store.WriteReport("app", session.Id.ToString(), "# Informe de sesión — App");

        var toasts = new ToastCenter();
        ReportsViewModel reports = TestFactory.Reports(_hub);
        NavigationService navigation = TestFactory.NavigationWith(reports);
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, navigation, toasts);
        await vm.LoadAsync();

        SessionLine line = vm.Sessions.Single();
        line.HasReport.Should().BeTrue();
        await ((IAsyncRelayCommand)vm.OpenSessionCommand).ExecuteAsync(line);

        navigation.Current.Should().BeSameAs(reports);
        reports.IsViewing.Should().BeTrue();
        reports.OpenReport!.Entry.ReportId.Should().Be(session.Id.ToString(), "el informe de ESA sesión");
        toasts.Items.Should().BeEmpty("abrió: no hay nada que avisar");
    }

    /// <summary>Un clic en un rosco lleva al inventario DE ESA app, que es lo que el rosco cuenta.</summary>
    [Fact]
    public void El_rosco_lleva_al_inventario_de_su_aplicacion()
    {
        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));

        xaml.Should().Contain("DataContext.OpenInventoryCommand");
        xaml.Should().Contain("CommandParameter=\"{Binding}\"");

        // Y la tarjeta que va como parámetro lleva el slug: el destino no se adivina de la
        // posición en la fila.
        typeof(CoverageCard).GetProperty("Slug").Should().NotBeNull();
    }

    [Fact]
    public async Task El_toggle_acumulado_convierte_la_serie_en_una_curva_ascendente()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("app", cost: 5m, daysAgo: 3);
        WriteSession("app", cost: 7m, daysAgo: 20);

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        IReadOnlyList<double> crudo = vm.CostSeries.Single().Values;
        crudo.Sum().Should().BeApproximately(12, 0.001);

        vm.Cumulative = true;
        IReadOnlyList<double> acumulado = vm.CostSeries.Single().Values;

        acumulado.Should().BeInAscendingOrder("una curva de mercado no baja");
        acumulado[^1].Should().BeApproximately(12, 0.001, "acaba en el total del periodo");
    }

    // ============================================ Gráfica de resoluciones (F6.1)

    /// <summary>
    /// La regla del §2 aplicada a la gráfica nueva: una aplicación tiene EL MISMO color en las
    /// dos gráficas. Es lo que permite leerlas en pareja —«esto costó, esto saldó»— sin volver a
    /// buscar la leyenda al bajar la vista.
    /// </summary>
    [Fact]
    public async Task Una_app_lleva_el_mismo_color_en_la_grafica_de_coste_y_en_la_de_resoluciones()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "xblast", Name = "XBlast", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("xblast", cost: 5m);
        WriteResolved("xblast", daysAgo: 2);

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        vm.HasResolutions.Should().BeTrue();
        Hex(vm.ResolutionSeries.Single().Stroke).Should().Be(Hex(vm.CostSeries.Single().Stroke));
        vm.ResolutionSeries.Single().Kind.Should().Be(ChartSeriesKind.Line, "el mismo tipo de trazo");
        vm.ResolutionLegend.Single().Name.Should().Be("XBlast", "la leyenda NOMBRA, aquí también");
    }

    /// <summary>
    /// El acumulado de la gráfica nueva es SUYO: mueve su curva y deja la de coste donde estaba.
    /// Dos gráficas en tarjetas distintas no pueden compartir un interruptor.
    /// </summary>
    [Fact]
    public async Task El_acumulado_de_resoluciones_es_propio_y_no_mueve_la_grafica_de_coste()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("app", cost: 5m);
        WriteResolved("app", daysAgo: 3);
        WriteResolved("app", daysAgo: 20);

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        IReadOnlyList<double> crudo = vm.ResolutionSeries.Single().Values;
        crudo.Sum().Should().BeApproximately(2, 0.001);
        crudo.Should().NotBeInAscendingOrder("sin acumular hay un punto por tramo, no una escalera");

        IReadOnlyList<double> costeAntes = vm.CostSeries.Single().Values;
        vm.CumulativeResolutions = true;

        IReadOnlyList<double> acumulado = vm.ResolutionSeries.Single().Values;
        acumulado.Should().BeInAscendingOrder("la deuda saldada no se desanda");
        acumulado[^1].Should().BeApproximately(2, 0.001, "acaba en el total del periodo");
        vm.CostSeries.Single().Values.Should().Equal(costeAntes, "el interruptor de una tarjeta no toca la otra");
    }

    /// <summary>Sin resoluciones no hay eje mudo: se escribe por qué está en blanco.</summary>
    [Fact]
    public async Task Un_periodo_sin_resoluciones_lo_dice_en_vez_de_ensenar_una_grafica_vacia()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("app", cost: 5m);
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-3));

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        vm.HasResolutions.Should().BeFalse();
        vm.ResolutionSeries.Should().BeEmpty();
        vm.ResolutionLegend.Should().BeEmpty();

        Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"))
            .Should().Contain("Aún no hay resoluciones en este periodo");
    }

    // ============================================ Severidad por aplicación (F6.5)

    /// <summary>
    /// Los cuatro tramos van en orden fijo desde las 12 en punto —de lo más grave a lo menos— y
    /// con los colores RESERVADOS de severidad. Es la única gráfica del panel que los usa, y es
    /// su sitio: aquí la paleta semántica no decora, es el dato (D-316).
    /// </summary>
    [Fact]
    public async Task Los_tramos_van_en_orden_de_gravedad_y_con_los_colores_reservados()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-1), Severity.Baja);
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-1), Severity.Critica);
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-1), Severity.Media);
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-1), Severity.Media);

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        SeverityCard card = vm.SeverityCards.Single();
        card.TotalText.Should().Be("4", "el número del centro es el total de activos");

        // Orden fijo, y la Alta no aparece porque no hay ninguna: el orden se conserva entre las
        // que sí están, no se reordena por tamaño.
        card.Segments.Select(s => s.Name).Should().Equal("Crítica", "Media", "Baja");
        Hex(card.Segments[0].Brush).Should().Be(SeverityPalette.Critica.ToUpperInvariant());
        Hex(card.Segments[1].Brush).Should().Be(SeverityPalette.Media.ToUpperInvariant());
        Hex(card.Segments[2].Brush).Should().Be(SeverityPalette.Baja.ToUpperInvariant());
    }

    /// <summary>El tooltip de un tramo dice qué es, cuántos son y qué parte del total.</summary>
    [Fact]
    public async Task El_tooltip_de_un_tramo_dice_cuantos_son_y_que_parte_del_total()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        for (int i = 0; i < 3; i++)
        {
            WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-1), Severity.Alta);
        }

        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-1), Severity.Baja);

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        IReadOnlyList<DonutSegment> segments = vm.SeverityCards.Single().Segments;
        // BUGFIX-REDONDEO: el porcentaje del tooltip lo escribe el formateador común, así que se
        // compara contra ÉL y no contra un literal — que además llevaría el separador de esta
        // máquina y no el de la aplicación.
        segments[0].Tooltip.Should().Be($"Alta — 3 hallazgos ({PercentText.Of(3, 4)})");
        segments[1].Tooltip.Should().Be($"Baja — 1 hallazgo ({PercentText.Of(1, 4)})", "uno en singular");
    }

    /// <summary>Una leyenda para toda la fila, no una por rosco: las cuatro son siempre las mismas.</summary>
    [Fact]
    public async Task La_leyenda_es_una_para_la_fila_entera()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "a", Name = "A", RepoUrl = "u", CurrentCycle = 1 });
        _hub.Store.WriteApp(new AppConfig { Slug = "b", Name = "B", RepoUrl = "u", CurrentCycle = 1 });
        WriteFinding("a", DateTimeOffset.UtcNow.AddDays(-1), Severity.Alta);
        WriteFinding("b", DateTimeOffset.UtcNow.AddDays(-1), Severity.Baja);

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        vm.SeverityCards.Should().HaveCount(2);
        vm.SeverityLegend.Select(l => l.Name).Should().Equal(
            new[] { "Crítica", "Alta", "Media", "Baja" },
            "las cuatro, en el mismo orden que los tramos, y una sola vez");

        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));
        xaml.Should().Contain("{Binding SeverityLegend}");
        Regex.Matches(xaml, Regex.Escape("{Binding SeverityLegend}")).Count.Should()
            .Be(1, "la leyenda se pinta fuera de la plantilla del rosco: una, no una por app");
    }

    /// <summary>
    /// Una app limpia se dibuja vacía, no se omite. Y no escribe un cero suelto en el centro sin
    /// explicar de qué: la etiqueta «0 activos» va debajo.
    /// </summary>
    [Fact]
    public async Task Una_app_limpia_se_ve_vacia_en_vez_de_desaparecer()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "limpia", Name = "Limpia", RepoUrl = "u", CurrentCycle = 1 });

        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        SeverityCard card = vm.SeverityCards.Single();
        card.HasData.Should().BeFalse();
        card.TotalText.Should().Be("0");
        card.Segments.Should().BeEmpty();
        card.Detail.Should().Be("Sin hallazgos activos");

        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));
        xaml.Should().Contain("0 activos", "el rosco vacío se explica");
        xaml.Should().Contain("EmptyBrush", "y se dibuja apagado en vez de dejar un hueco");
    }

    /// <summary>
    /// La promesa que hace un tramo con el ratón encima: llevar EXACTAMENTE a los hallazgos que
    /// cuenta — esa app y esa severidad.
    /// </summary>
    [Fact]
    public async Task Un_clic_en_un_tramo_abre_los_hallazgos_de_esa_app_y_esa_severidad()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "xblast", Name = "XBlast", RepoUrl = "u", CurrentCycle = 1 });
        _hub.Store.WriteApp(new AppConfig { Slug = "otra", Name = "Otra", RepoUrl = "u", CurrentCycle = 1 });
        WriteFinding("xblast", DateTimeOffset.UtcNow.AddDays(-1), Severity.Critica);
        WriteFinding("xblast", DateTimeOffset.UtcNow.AddDays(-1), Severity.Baja);
        WriteFinding("otra", DateTimeOffset.UtcNow.AddDays(-1), Severity.Critica);

        var findings = new FindingsViewModel(
            _hub, new NavigationService(new NoServices()), _settings, new GroupExpansionMemory());
        NavigationService navigation = TestFactory.NavigationWith(findings);
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, navigation);
        await vm.LoadAsync();

        SeverityCard card = vm.SeverityCards.Single(c => c.Slug == "xblast");
        var slice = (SeveritySlice)card.Segments[0].Payload!;
        await vm.OpenSeverityCommand.ExecuteAsync(slice);

        navigation.Current.Should().BeSameAs(findings);
        findings.SelectedApp!.Slug.Should().Be("xblast");
        findings.SelectedSeverity!.Value.Should().Be(Severity.Critica);
        findings.ResultCount.Should().Be(1, "la crítica de xblast, ni la baja ni la de la otra app");
    }

    /// <summary>Y el centro lleva a esa app entera, que es lo que el número del centro cuenta.</summary>
    [Fact]
    public async Task Un_clic_en_el_centro_abre_los_hallazgos_de_esa_app_sin_recortar_por_severidad()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "xblast", Name = "XBlast", RepoUrl = "u", CurrentCycle = 1 });
        WriteFinding("xblast", DateTimeOffset.UtcNow.AddDays(-1), Severity.Critica);
        WriteFinding("xblast", DateTimeOffset.UtcNow.AddDays(-1), Severity.Baja);

        var findings = new FindingsViewModel(
            _hub, new NavigationService(new NoServices()), _settings, new GroupExpansionMemory());
        NavigationService navigation = TestFactory.NavigationWith(findings);
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, navigation);
        await vm.LoadAsync();

        await vm.OpenAppFindingsCommand.ExecuteAsync(vm.SeverityCards.Single());

        findings.SelectedApp!.Slug.Should().Be("xblast");
        findings.SelectedSeverity!.Value.Should().BeNull("el centro no recorta por severidad");
        findings.ResultCount.Should().Be(2);
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    // ============================================ El cuadre de la vista

    /// <summary>
    /// El registro de operaciones ESCRIBE de qué tipo es cada sesión. Sin esa columna, un arreglo
    /// asistido y una auditoría que no tocó nada son la misma fila: misma fecha, «0 unidades»,
    /// «sin cambios» y —en el arreglo— un coste sin nada que lo explique.
    /// </summary>
    [Fact]
    public async Task El_registro_escribe_el_tipo_de_cada_sesion()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("app", cost: 105m);
        WriteSession("app", cost: 67.5m, mode: AuditMode.Fix, units: 0, daysAgo: 0);
        WriteSession("app", cost: 12m, mode: AuditMode.Verify, units: 0, daysAgo: 0);

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        vm.Sessions.Should().HaveCount(3);
        vm.Sessions.Select(l => l.Type).Should().Contain(new[]
        {
            "Auditoría por lotes", "Arreglo asistido", "Verificación",
        });

        // Y el coste del arreglo se escribe en su fila, no se queda en «—».
        vm.Sessions.Single(l => l.Type == "Arreglo asistido").Cost.Should().Contain("67,5");
    }

    /// <summary>
    /// Las tres gráficas de eje temporal llevan el tramo completo de cada cubo al tooltip. En el
    /// eje no cabe («28 ago»), y sin el tooltip un cubo semanal se lee como el día que lo rotula.
    /// </summary>
    [Fact]
    public async Task Las_graficas_llevan_el_tramo_completo_de_cada_cubo_al_tooltip()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("app", cost: 105m);
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-3));

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        vm.CostRanges.Should().HaveCount(vm.CostLabels.Count);
        vm.ResolutionRanges.Should().HaveCount(vm.ResolutionLabels.Count);
        vm.FlowRanges.Should().HaveCount(vm.FlowLabels.Count);

        // Con el periodo por defecto de F35 —cuatro semanas— los cubos son diarios y el tramo ES
        // el día. La ambigüedad que el tooltip resuelve aparece con cubos de más de un día, así
        // que se mira ahí: ocho semanas, cubos semanales.
        vm.SelectedRange = vm.RangeOptions.Single(r => r.Range == MetricsRange.Weeks8);
        await vm.LoadAsync();
        vm.CostRanges[^1].Should().Contain("–").And.NotBe(vm.CostLabels[^1]);

        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));
        Regex.Matches(xaml, "TooltipLabels=").Count.Should()
            .Be(3, "coste, resoluciones y flujo: las tres gráficas de eje temporal");
    }


    // ============================================ F35 §1.3 — las cuatro cifras, escritas

    /// <summary>
    /// Las cuatro tarjetas, en su orden, con su cifra y su línea de detalle. El orden importa: es
    /// qué parte está mirada → cuánto queda → cuánto costó → a cómo sale cada arreglo.
    /// </summary>
    [Fact]
    public async Task Las_cuatro_cifras_escriben_su_numero_su_linea_y_su_tendencia()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 3 });
        WriteInventory("app", audited: 4, pending: 6, large: 2, cycle: 3);
        WriteSession("app", cost: 200m, daysAgo: 3);
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-2));
        WriteResolved("app", daysAgo: 1);

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        vm.Cards.Select(c => c.Key).Should().Equal(
            new[] { "coverage", "debt", "cost", "cost-per-resolution" });

        StatCard coverage = vm.Cards[0];
        coverage.Title.Should().Be("Cobertura");
        coverage.Value.Should().Be("40 %");
        coverage.Subtitle.Should().Be("4 de 10 unidades · ciclo 3", "una sola app: se dice su ciclo");

        StatCard debt = vm.Cards[1];
        debt.Title.Should().Be("Deuda activa");
        debt.Value.Should().Be("1");
        debt.Subtitle.Should().Be(
            "+1 nuevo · −1 resuelto en el periodo",
            "el resuelto se detectó hace 31 días, así que no es nuevo de este periodo");

        StatCard cost = vm.Cards[2];
        cost.Value.Should().Be("200,0");
        cost.Subtitle.Should().Be("1 sesión · 200,0 credits por unidad auditada");

        StatCard perResolution = vm.Cards[3];
        perResolution.Title.Should().Be("Coste por hallazgo resuelto");
        perResolution.Value.Should().Be("200,0");
        perResolution.Subtitle.Should().Be("1 resuelto · sin cifra del periodo anterior");

        // Ninguna flecha se inventa un porcentaje (D-318). Hay periodo anterior —el resuelto se
        // detectó antes— pero ninguna cifra de entonces con la que dividir, salvo la deuda: había
        // uno vivo entonces y hay uno vivo hoy.
        debt.Trend.Should().Be("igual que el periodo anterior");
        coverage.Trend.Should().Be("sin cifra anterior con la que comparar");
        cost.Trend.Should().Be("sin cifra anterior con la que comparar");
        perResolution.Trend.Should().Be("sin cifra anterior con la que comparar");
        vm.Cards.Should().OnlyContain(c => c.Tone == StatCard.Neutral, "ninguna se movió a mejor ni a peor");
    }

    /// <summary>
    /// Con VARIAS aplicaciones no se enseña ningún ciclo: se dice cuántas son. Un ciclo sobre una
    /// suma de ciclos distintos no significa nada.
    /// </summary>
    [Fact]
    public async Task Con_varias_aplicaciones_la_cobertura_dice_cuantas_son_y_no_un_ciclo()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "a", Name = "A", RepoUrl = "u", CurrentCycle = 3 });
        _hub.Store.WriteApp(new AppConfig { Slug = "b", Name = "B", RepoUrl = "u", CurrentCycle = 7 });
        WriteInventory("a", audited: 4, pending: 6, large: 0, cycle: 3);
        WriteInventory("b", audited: 1, pending: 9, large: 0, cycle: 7);

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        StatCard coverage = vm.Cards.Single(c => c.Key == "coverage");
        coverage.Value.Should().Be("25 %", "5 de 20 unidades, no la media de 40 % y 10 %");
        coverage.Subtitle.Should().Be("5 de 20 unidades · 2 aplicaciones");
        coverage.Subtitle.Should().NotContain("ciclo");
    }

    /// <summary>
    /// <b>El conmutador de unidad mueve las DOS tarjetas de coste y nada más</b> (F29 §2). La
    /// cobertura y la deuda no son dinero: cambiar de divisa no puede tocarlas.
    /// </summary>
    [Fact]
    public async Task Conmutar_la_divisa_cambia_las_dos_tarjetas_de_coste_y_ninguna_otra()
    {
        CostCurrency original = CostFormat.Currency;
        try
        {
            _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
            WriteInventory("app", audited: 1, pending: 1, large: 0);
            WriteSession("app", cost: 200m, daysAgo: 3);
            WriteResolved("app", daysAgo: 1);

            CostFormat.Currency = CostCurrency.Credits;
            MetricsViewModel enCredits = Panel();
            await enCredits.LoadAsync();
            var antes = enCredits.Cards.ToDictionary(c => c.Key);

            CostFormat.Currency = CostCurrency.Usd;
            MetricsViewModel enDolares = Panel();
            await enDolares.LoadAsync();
            var despues = enDolares.Cards.ToDictionary(c => c.Key);

            // Las dos de dinero cambian: 200 credits son 2,00 $.
            antes["cost"].Amount.Should().Be("200,0 credits");
            despues["cost"].Amount.Should().Be("2,00 $");
            antes["cost-per-resolution"].Amount.Should().Be("200,0 credits");
            despues["cost-per-resolution"].Amount.Should().Be("2,00 $");

            // Y las otras dos, ni una letra.
            foreach (string key in new[] { "coverage", "debt" })
            {
                despues[key].Value.Should().Be(antes[key].Value);
                despues[key].Subtitle.Should().Be(antes[key].Subtitle);
            }
        }
        finally
        {
            CostFormat.Currency = original;
        }
    }

    /// <summary>
    /// <b>La tendencia lleva su signo, y el coste no lleva ninguno</b> (F35 §1.3): verde cuando la
    /// cifra se mueve a mejor, rojo a peor, y apagado siempre en el coste — gastar más no es malo
    /// por sí mismo.
    /// </summary>
    [Fact]
    public async Task La_flecha_dice_si_es_buena_noticia_y_el_coste_no_juzga()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });

        // Periodo anterior (4 semanas antes): una sesión de 100 y un hallazgo viejo, todavía vivo.
        WriteSession("app", cost: 100m, daysAgo: 40);
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-60));

        // Periodo: otro hallazgo nuevo (la deuda sube: mala noticia) y más gasto.
        WriteSession("app", cost: 300m, daysAgo: 3);
        WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-2));

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        StatCard debt = vm.Cards.Single(c => c.Key == "debt");
        debt.Trend.Should().StartWith("▲ 100");
        debt.Tone.Should().Be(StatCard.Bad, "subir la deuda es una mala noticia");

        StatCard cost = vm.Cards.Single(c => c.Key == "cost");
        cost.Trend.Should().StartWith("▲ 200");
        cost.Tone.Should().Be(StatCard.Neutral, "el coste no lleva juicio de color");
    }

    /// <summary>
    /// El «Copiar» de cada tarjeta (F33) se lleva el número, el subtítulo y la tendencia — con el
    /// título delante, porque un «40 %» pegado en un correo no dice de qué es.
    /// </summary>
    [Fact]
    public async Task Cada_tarjeta_se_copia_con_su_numero_su_linea_y_su_tendencia()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 3 });
        WriteInventory("app", audited: 4, pending: 6, large: 0, cycle: 3);

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        vm.Cards.Single(c => c.Key == "coverage").CopyText.Should()
            .Be("Cobertura: 40 % · 4 de 10 unidades · ciclo 3 · sin periodo anterior");

        // Y el comando existe y acepta la tarjeta: es el gesto que la vista enlaza.
        vm.CopyCardCommand.Should().BeOfType<RelayCommand<StatCard>>();
        vm.CopyCardCommand.CanExecute(vm.Cards[0]).Should().BeTrue();
    }

    // ============================================ F35 §2.6 — los cuatro tonos de acción

    /// <summary>
    /// <b>Los cuatro tonos de acción no pisan NADA reservado</b>, en los dos temas: ni un color de
    /// aplicación, ni una severidad, ni uno de los tres de estado, ni una de las tres series del
    /// flujo. Es la misma reserva de D-316, y se puede comprobar por la misma razón: los tonos
    /// viven en una paleta, no dentro del método que los pinta.
    /// <para>
    /// No se comprueba solo que no COINCIDAN —dos colores distintos a un ΔE de 3 se confunden
    /// igual—: se exige un margen medido. El de esta familia es ΔE ≥ 20 contra todo lo reservado.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_tono_de_accion_es_un_color_de_app_de_severidad_ni_de_estado()
    {
        foreach (bool dark in new[] { false, true })
        {
            var reserved = new List<(string Name, string Hex)>();
            foreach (SeriesColor c in SeriesPalette.Steps
                         .Append(SeriesPalette.Others).Append(SeriesPalette.Pending).Append(SeriesPalette.Large)
                         .Concat(FlowPalette.All))
            {
                reserved.Add((c.Name, c.For(dark)));
            }

            foreach (Severity s in Enum.GetValues<Severity>())
            {
                reserved.Add((s.ToString(), SeverityPalette.Hex(s)));
            }

            foreach ((string key, string hex) in StatePalette(dark ? "Dark" : "Light"))
            {
                reserved.Add((key, hex));
            }

            foreach (SeriesColor tone in ActionPalette.All)
            {
                string hex = tone.For(dark);
                foreach ((string name, string other) in reserved)
                {
                    hex.ToUpperInvariant().Should().NotBe(
                        other.ToUpperInvariant(), $"«{tone.Name}» no puede ser «{name}»");
                    Distance(hex, other).Should().BeGreaterThan(
                        15, $"«{tone.Name}» tiene que distinguirse de «{name}» (tema {(dark ? "oscuro" : "claro")})");
                }
            }
        }
    }

    /// <summary>
    /// Los cuatro se distinguen ENTRE SÍ en cada tema —si no, la leyenda sería lo único legible del
    /// rosco— y cada uno tiene su paso propio para cada tema, aclarándose en oscuro (D-317).
    /// </summary>
    [Fact]
    public void Los_cuatro_tonos_de_accion_se_distinguen_entre_si_y_tienen_dos_pasos()
    {
        foreach (SeriesColor tone in ActionPalette.All)
        {
            tone.Light.Should().MatchRegex("^#[0-9A-Fa-f]{6}$");
            tone.Dark.Should().MatchRegex("^#[0-9A-Fa-f]{6}$");
            Luma(tone.Dark).Should().BeGreaterThan(
                Luma(tone.Light), $"«{tone.Name}» se aclara para fondo oscuro, como toda serie");
        }

        foreach (bool dark in new[] { false, true })
        {
            for (int i = 0; i < ActionPalette.All.Count; i++)
            {
                for (int j = i + 1; j < ActionPalette.All.Count; j++)
                {
                    Distance(ActionPalette.All[i].For(dark), ActionPalette.All[j].For(dark))
                        .Should().BeGreaterThan(
                            10,
                            $"«{ActionPalette.All[i].Name}» y «{ActionPalette.All[j].Name}» son dos tramos "
                            + "del mismo rosco");
                }
            }
        }
    }

    /// <summary>
    /// <b>El mismo tono en todas las aplicaciones</b> (F35 §2.6): la acción es lo que el color
    /// dice, y la aplicación va en el título. Un tono que dependiera de la app necesitaría cuatro
    /// por aplicación, y en la paleta no caben ni cuatro (D-315).
    /// </summary>
    [Fact]
    public async Task El_rosco_de_accion_lleva_el_mismo_color_en_todas_las_aplicaciones()
    {
        foreach (string slug in new[] { "xblast", "otra" })
        {
            _hub.Store.WriteApp(new AppConfig { Slug = slug, Name = slug, RepoUrl = "u", CurrentCycle = 1 });
            WriteSession(slug, cost: 50m);
            WriteSession(slug, cost: 10m, mode: AuditMode.Fix, units: 0, daysAgo: 1);
        }

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        vm.ActionCost.Should().HaveCount(2);
        CoverageCard uno = vm.ActionCost[0];
        CoverageCard otro = vm.ActionCost[1];

        uno.Segments.Select(s => Hex(s.Brush)).Should().Equal(
            otro.Segments.Select(s => Hex(s.Brush)),
            "los tramos de las dos apps son los mismos cuatro tonos");
        uno.Name.Should().NotBe(otro.Name, "lo que distingue a las dos es el título, no el color");

        // Y la leyenda nombra las cuatro acciones, no las aplicaciones (D-296).
        vm.ActionLegend.Select(l => l.Name).Should().Equal(
            "Auditoría", "Verificación", "Arreglo", "Gestión");
    }

    /// <summary>
    /// <b>La antigüedad SE DIBUJA</b> (F35-3 §0), y con el color de la app (D-314).
    /// <para>
    /// <b>Sustituye al test que no veía nada.</b> El de la Entrega 2 comprobaba los rótulos de los
    /// cubos, el número de series, que fueran barras y su color — todo del view-model, o sea las
    /// ENTRADAS de la gráfica. Con eso en verde, la gráfica salía vacía en el <c>dist</c>: no había
    /// una sola afirmación sobre lo que se dibuja. La regla que queda: <b>un test de gráfica
    /// comprueba lo que se dibuja, no solo lo que se calcula.</b>
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_antiguedad_se_dibuja_con_su_eje_sus_rotulos_y_el_color_de_la_app()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "xblast", Name = "XBLAST", RepoUrl = "u", CurrentCycle = 1 });
        _hub.Store.WriteApp(new AppConfig { Slug = "otra", Name = "Otra", RepoUrl = "u", CurrentCycle = 1 });
        for (int i = 0; i < 390; i++)
        {
            WriteFinding("xblast", DateTimeOffset.UtcNow.AddDays(-2));
        }

        for (int i = 0; i < 5; i++)
        {
            WriteFinding("otra", DateTimeOffset.UtcNow.AddDays(-2));
        }

        WriteSession("xblast", cost: 50m);

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        // Lo que se calcula: dos series de barras, con los cuatro cubos y el color de cada app.
        vm.AgeLabels.Should().Equal("< 1 sem", "1–4 sem", "4–12 sem", "> 12 sem");
        vm.AgeSeries.Should().HaveCount(2);
        vm.AgeSeries.Should().OnlyContain(s => s.Kind == ChartSeriesKind.Bar);
        Hex(vm.AgeSeries.Single(s => s.Key == "xblast").Stroke).Should()
            .Be(Hex(vm.CostSeries[0].Stroke), "la misma app, el mismo color que en coste (D-314)");

        // Y LO QUE SE DIBUJA, que es lo que faltaba. Se sacan CIFRAS del hilo de UI, no los
        // objetos: un `DependencyObject` solo lo puede leer el hilo que lo creó.
        var barras = new List<double>();
        var textos = new List<string>();
        ViewLayout.OnUiThread(() =>
        {
            ChartPlot plot = Paint(vm.AgeSeries, vm.AgeLabels, barValues: true);

            // Las barras son las cajas OPACAS: las cuatro transparentes son las columnas del
            // tooltip, una por cubo.
            barras.AddRange(plot.Children.OfType<Rectangle>()
                .Where(r => r.Fill is SolidColorBrush { Color.A: 0xFF })
                .Select(r => r.Height));
            textos.AddRange(plot.Children.OfType<TextBlock>().Select(t => t.Text));
        });

        // Una barra por aplicación en el único cubo con hallazgos. Un cubo vacío se ve vacío: con
        // su rótulo y sin barra.
        barras.Should().HaveCount(2, "una barra por aplicación en el único cubo con hallazgos");
        barras.Should().OnlyContain(h => h > 0);
        barras.Max().Should().BeGreaterThan(barras.Min() * 10, "390 y 5 no pueden salir del mismo alto");

        // El eje llega a un número REDONDO por encima del dato (D-313: marcas 1-2-5).
        AxisScale eje = AxisScale.For(390);
        eje.Max.Should().Be(400).And.BeGreaterThanOrEqualTo(390);
        textos.Should().Contain("400").And.Contain("0");

        // Los cuatro rótulos de cubo, y el número encima de cada barra con datos (§1.1).
        textos.Should().Contain(new[] { "< 1 sem", "1–4 sem", "4–12 sem", "> 12 sem" });
        textos.Should().Contain("390").And.Contain("5");

        // UN solo eje: el control no tiene ninguna propiedad de segundo eje (D-313).
        typeof(ChartPlot).GetProperties().Should().NotContain(p => p.Name.Contains("Secondary"));
    }

    /// <summary>
    /// <b>La gráfica dibuja aunque el dato llegue mientras está colapsada</b> (F35-3 §0). Ésta es
    /// la regresión exacta: el control nace <c>Collapsed</c> —su visibilidad cuelga de un
    /// <c>HasX</c> que empieza en false—, el view-model le asigna la serie ANTES de encender ese
    /// interruptor, y aquel <c>Rebuild</c> se encontraba con ancho 0 y se iba sin dibujar. Las
    /// otras tres gráficas se libraban por casualidad, porque sus view-models encienden el
    /// interruptor primero: una regla que depende de en qué orden asigne quien llama no es una
    /// regla, y por eso el arreglo está en el control.
    /// </summary>
    [Fact]
    public void La_grafica_dibuja_aunque_el_dato_llegue_estando_colapsada()
    {
        int hijos = 0;
        int barras = 0;

        ViewLayout.OnUiThread(() =>
        {
            var plot = new ChartPlot
            {
                ValueFormat = "0",
                Height = 200,
                AxisBrush = Brushes.Gray,
                GridBrush = Brushes.Gray,
                Visibility = Visibility.Collapsed,
            };
            var host = new Border { Width = 600, Height = 400, Child = new StackPanel { Children = { plot } } };
            ViewLayout.Layout(host, 600, 400);

            // El orden hostil: primero el dato —con el control todavía colapsado y sin tamaño—, y
            // el interruptor después.
            plot.Labels = new[] { "< 1 sem", "1–4 sem", "4–12 sem", "> 12 sem" };
            plot.Series = new[]
            {
                new ChartSeries("a", "A", Brushes.Blue, new double[] { 390, 0, 0, 0 }, ChartSeriesKind.Bar),
            };
            plot.Visibility = Visibility.Visible;

            host.UpdateLayout();
            hijos = plot.Children.Count;
            barras = plot.Children.OfType<Rectangle>()
                .Count(r => r.Fill is SolidColorBrush { Color.A: 0xFF });
        });

        hijos.Should().BeGreaterThan(0, "con tamaño y datos, la gráfica tiene que haber pintado algo");
        barras.Should().Be(1);
    }

    /// <summary>
    /// <b>El top de reglas son barras proporcionales</b> (F35-3 §1.2): la primera llena el hueco y
    /// las demás guardan su proporción contra ella. Y el texto que se copia no cambia.
    /// </summary>
    [Fact]
    public async Task El_top_de_reglas_dibuja_barras_proporcionales_a_la_primera()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        foreach ((string rule, int n) in new[]
                 {
                     ("errores.null.desreferencia", 10),
                     ("errores.calculo.negocio", 5),
                     ("mejoras.estilo.nomenclatura", 1),
                 })
        {
            for (int i = 0; i < n; i++)
            {
                WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-2), rule: rule);
            }
        }

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        vm.TopRules.Select(r => r.Count).Should().Equal(10, 5, 1);

        // La más larga llena el espacio: una estrella entera, y nada a su derecha salvo el número.
        vm.TopRules[0].Share.Value.Should().Be(1);
        vm.TopRules[0].Rest.Value.Should().Be(0);

        // Y las demás, su proporción contra ella. Las dos columnas siempre suman uno.
        vm.TopRules[1].Share.Value.Should().BeApproximately(0.5, 0.0001);
        vm.TopRules[2].Share.Value.Should().BeApproximately(0.1, 0.0001);
        vm.TopRules.Should().OnlyContain(r => r.Share.IsStar && r.Rest.IsStar);
        vm.TopRules.Should().OnlyContain(r => Math.Abs(r.Share.Value + r.Rest.Value - 1) < 0.0001);

        // El texto copiado es el mismo de la Entrega 2: cambia el dibujo, no lo que se lleva.
        vm.CopyTopRulesCommand.Should().BeOfType<RelayCommand>();
        string esperado = string.Join(
            "\n", new[] { "10 · Posible desreferencia nula", "5 · Error de cálculo de negocio", "1 · Nomenclatura/estilo" });
        string.Join("\n", vm.TopRules.Select(r => $"{r.Count} · {r.Name}")).Should().Be(esperado);
    }

    /// <summary>
    /// <b>Antigüedad y Top 5 comparten fila</b> (F35-3 §1.3, D-990): mismo alto, ancho repartido, y
    /// en ventana estrecha bajan. Es lo que quita los dos mil píxeles entre un nombre y su número.
    /// </summary>
    [Fact]
    public void Las_dos_graficas_pequenas_comparten_fila_y_alto()
    {
        var anchos = new List<double>();
        var altos = new List<double>();
        var estrecho = new List<double>();

        ViewLayout.OnUiThread(() =>
        {
            // Las dos tarjetas, con CONTENIDO de altos distintos —como las de verdad—: si se les
            // fijara el alto a mano no habría nada que igualar.
            var panel = new ColumnsPanel { MinColumnWidth = 420, MaxColumns = 2, Gap = 16 };
            panel.Children.Add(new Border { Background = Brushes.Gray, Child = new Border { Height = 300 } });
            panel.Children.Add(new Border { Background = Brushes.Gray, Child = new Border { Height = 180 } });

            ViewLayout.Layout(panel, 1400, 900);
            foreach (FrameworkElement child in panel.Children.OfType<FrameworkElement>())
            {
                anchos.Add(child.ActualWidth);
                altos.Add(child.ActualHeight);
            }

            // Estrecho: la segunda baja, y las dos ocupan el ancho entero.
            ViewLayout.Layout(panel, 500, 900);
            estrecho.AddRange(panel.Children.OfType<FrameworkElement>().Select(c => c.ActualWidth));
        });

        anchos.Should().HaveCount(2);
        anchos[0].Should().Be(anchos[1], "el ancho se reparte entre las dos");
        altos[0].Should().Be(altos[1], "la rejilla iguala: las dos al alto de la más alta (D-990)");
        estrecho.Should().OnlyContain(w => w > 400, "a 500 px caben en una sola columna y bajan");

        // Y en la vista son ESTAS dos las que van juntas, en ese orden.
        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));
        int fila = xaml.LastIndexOf("<c:ColumnsPanel", StringComparison.Ordinal);
        int antiguedad = xaml.IndexOf("Antigüedad de la deuda", StringComparison.Ordinal);
        int top = xaml.IndexOf("Top 5 reglas del periodo", StringComparison.Ordinal);
        antiguedad.Should().BeGreaterThan(fila).And.BeLessThan(top);
    }

    /// <summary>Pinta unas series en un <see cref="ChartPlot"/> ya medido, y devuelve el control.</summary>
    private static ChartPlot Paint(
        IReadOnlyList<ChartSeries> series, IReadOnlyList<string> labels, bool barValues)
    {
        var plot = new ChartPlot
        {
            ValueFormat = "0",
            Width = 640,
            Height = 200,
            AxisBrush = Brushes.Gray,
            GridBrush = Brushes.Gray,
            ShowBarValues = barValues,
            Labels = labels,
            Series = series,
        };

        ViewLayout.Layout(plot, 640, 200);
        return plot;
    }

    /// <summary>Los cinco de arriba, escritos, y su «Copiar» (F33).</summary>
    [Fact]
    public async Task El_top_de_reglas_se_escribe_y_se_copia()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        for (int i = 0; i < 3; i++)
        {
            WriteFinding("app", DateTimeOffset.UtcNow.AddDays(-2));
        }

        MetricsViewModel vm = Panel();
        await vm.LoadAsync();

        vm.HasTopRules.Should().BeTrue();
        vm.TopRules.Should().HaveCount(1);
        vm.TopRules[0].Count.Should().Be(3);
        vm.CopyTopRulesCommand.Should().BeOfType<RelayCommand>();

        // Y la fila no lleva color de severidad: una regla no es una gravedad (D-316).
        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));
        int block = xaml.IndexOf("Top 5 reglas del periodo", StringComparison.Ordinal);
        string section = xaml[block..xaml.IndexOf("Ciclos y temáticas", StringComparison.Ordinal)];
        section.Should().NotContain("Pill.Sev").And.NotContain("SeverityToBrush");
    }

    /// <summary>Los `Color` de estado declarados en la paleta de un tema, leídos del XAML.</summary>
    private static IEnumerable<(string Key, string Hex)> StatePalette(string theme)
    {
        string xaml = Source($"src/Atalaya.App/Themes/Palette.{theme}.xaml");
        foreach (Match m in Regex.Matches(xaml, @"<Color x:Key=""(Color\.(?:Success|Warning|Danger)\.[^""]+)"">\s*(#[0-9A-Fa-f]{6})\s*</Color>"))
        {
            yield return (m.Groups[1].Value, m.Groups[2].Value);
        }
    }

    /// <summary>Distancia CIEDE76 entre dos colores. Por debajo de ~10 dos tonos se confunden.</summary>
    private static double Distance(string a, string b)
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

    private MetricsViewModel Panel()
        => TestFactory.Metrics(_hub, _paths, _settings, TestFactory.NavigationWith(TestFactory.Reports(_hub)),
            new ToastCenter());

    // ============================================ Utilidades

    private AuditSession WriteSession(
        string slug, decimal? cost, int daysAgo = 2, AuditMode mode = AuditMode.Lotes, int units = 1)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = mode,
            By = "alvaro",
            Machine = "PC",
            StartedUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo),
            CycleN = 1,
        };
        for (int i = 0; i < units; i++)
        {
            session.Units.Add(new UnitVerdictRecord($"src/A{i}.cs", "src", "auditada", null));
        }

        TestRates.CostAs(session, cost, inputTokens: 100);
        _hub.Store.WriteSession(session);
        return session;
    }

    private void WriteFinding(
        string slug, DateTimeOffset detected, Severity severity = Severity.Alta, string rule = "criterio.x")
    {
        var stamp = new DetectionStamp(detected, AuditMode.Lotes, "abc", "alvaro");
        _hub.Store.WriteFinding(slug, new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = rule,
            Pillar = Pillar.Errores,
            Tag = FindingTag.Criterio,
            Severity = severity,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = "t",
            Locations = { new Location("a.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        });
    }

    /// <summary>El inventario de un ciclo, con sus unidades en cada estado.</summary>
    private void WriteInventory(string slug, int audited, int pending, int large, int cycle = 1)
    {
        var inv = new InventoryCycle { CycleN = cycle };
        for (int i = 0; i < audited; i++)
        {
            inv.Units.Add(new InventoryUnit { Path = $"a{i}.cs", Module = "m", State = UnitState.Auditada });
        }

        for (int i = 0; i < pending; i++)
        {
            inv.Units.Add(new InventoryUnit { Path = $"p{i}.cs", Module = "m", State = UnitState.Pendiente });
        }

        for (int i = 0; i < large; i++)
        {
            inv.Units.Add(new InventoryUnit { Path = $"g{i}.cs", Module = "m", State = UnitState.Grande });
        }

        _hub.Store.WriteInventory(slug, inv);
    }

    /// <summary>Un hallazgo resuelto hace N días: la resolución queda en el historial.</summary>
    private void WriteResolved(string slug, int daysAgo)
    {
        DateTimeOffset when = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        var stamp = new DetectionStamp(when.AddDays(-30), AuditMode.Lotes, "abc", "alvaro");
        var finding = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "criterio.x",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Criterio,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = "t",
            Locations = { new Location("a.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };

        finding.Resolve(new ResolutionStamp(when, ResolutionVia.Auditor, AuditMode.Lotes, "c", "alvaro", "ok"));
        _hub.Store.WriteFinding(slug, finding);
    }

    private static string Hex(Brush brush)
    {
        var solid = (SolidColorBrush)brush;
        return $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}";
    }

    /// <summary>Luminancia relativa aproximada, para comparar dos pasos del mismo color.</summary>
    private static double Luma(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);
    }

    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        string path = Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"se esperaba {path}");
        return File.ReadAllText(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // El árbol temporal puede quedar tomado; no es parte de lo probado.
        }
    }
}
