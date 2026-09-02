using System.Text.RegularExpressions;
using System.Windows.Media;
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

        vm.CostTotal.Should().Be(MetricsViewModel.Unknown);
        vm.CostPerUnit.Should().Contain("Se activará cuando");
        vm.CyclePct.Should().Be(MetricsViewModel.Unknown);
        vm.CycleDetail.Should().Contain("Se activará cuando");
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

        foreach (string tile in new[]
                 {
                     "Hallazgos activos", "Resueltos en el periodo", "Coste del periodo", "Cobertura del ciclo",
                 })
        {
            xaml.Should().Contain(tile);
        }

        foreach (string chart in new[]
                 {
                     "Coste en el tiempo", "Resoluciones en el tiempo", "Cobertura por aplicación",
                     "Severidad por aplicación", "Flujo de hallazgos", "Ciclos y temáticas",
                     "Actividad de sesiones",
                 })
        {
            xaml.Should().Contain(chart);
        }

        xaml.Should().Contain("{Binding Cumulative}", "el toggle «Acumulado» de la gráfica de coste");
        xaml.Should().Contain("{Binding CumulativeResolutions}", "y el suyo en la de resoluciones");
        Regex.Matches(xaml, "controls:ChartPlot").Count.Should()
            .Be(3, "coste, resoluciones y flujo; ni una gráfica de más");

        // La de resoluciones va JUSTO debajo de la de coste: se leen en pareja.
        xaml.IndexOf("Resoluciones en el tiempo", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.IndexOf("Coste en el tiempo", StringComparison.Ordinal))
            .And.BeLessThan(xaml.IndexOf("Cobertura por aplicación", StringComparison.Ordinal));
        Regex.Matches(xaml, "controls:DonutRing").Count.Should()
            .Be(2, "cobertura y severidad; cada fila es UNA plantilla repetida, no un rosco por app");
        Regex.Matches(xaml, "controls:CycleRibbon").Count.Should().Be(1, "la cinta de ciclos (F17 §6)");

        // La cinta va entre el flujo y el registro de sesiones: es historia, y el registro es el detalle.
        xaml.IndexOf("Ciclos y temáticas", StringComparison.Ordinal).Should()
            .BeGreaterThan(xaml.IndexOf("Flujo de hallazgos", StringComparison.Ordinal))
            .And.BeLessThan(xaml.IndexOf("Actividad de sesiones", StringComparison.Ordinal));

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
            .Should().Contain("aún no hay resoluciones en este periodo");
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

        // Ocho semanas → cubos semanales: el tramo dice más que la etiqueta.
        vm.CostRanges[^1].Should().Contain("–").And.NotBe(vm.CostLabels[^1]);

        string xaml = Markup(Source("src/Atalaya.App/Views/MetricsView.xaml"));
        Regex.Matches(xaml, "TooltipLabels=").Count.Should()
            .Be(3, "coste, resoluciones y flujo: las tres gráficas de eje temporal");
    }

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

    private void WriteFinding(string slug, DateTimeOffset detected, Severity severity = Severity.Alta)
    {
        var stamp = new DetectionStamp(detected, AuditMode.Lotes, "abc", "alvaro");
        _hub.Store.WriteFinding(slug, new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "criterio.x",
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
