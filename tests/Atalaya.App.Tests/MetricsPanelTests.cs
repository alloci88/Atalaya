using System.Text.RegularExpressions;
using System.Windows.Media;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
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

        Source("src/Atalaya.App/Controls/DonutRing.cs")
            .Should().Contain("ToolTip = Tip(", "cada tramo del rosco dice cuántas unidades es");
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
    public void La_vista_trae_los_filtros_los_cuatro_tiles_y_las_cuatro_graficas()
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
                     "Coste en el tiempo", "Cobertura por aplicación", "Flujo de hallazgos", "Actividad de sesiones",
                 })
        {
            xaml.Should().Contain(chart);
        }

        xaml.Should().Contain("{Binding Cumulative}", "el toggle «Acumulado» de la gráfica de coste");
        Regex.Matches(xaml, "controls:ChartPlot").Count.Should().Be(2, "coste y flujo; ni una gráfica de más");
        Regex.Matches(xaml, "controls:DonutRing").Count.Should().Be(1, "los roscos son una plantilla repetida");
    }

    // ============================================ Gestos

    [Fact]
    public async Task Un_clic_en_una_sesion_sin_informe_lo_dice_en_vez_de_no_hacer_nada()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        WriteSession("app", cost: 2m);

        var opener = new TestFactory.RecordingFileOpener();
        var toasts = new ToastCenter();
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, opener: opener, toasts: toasts);
        await vm.LoadAsync();

        SessionLine line = vm.Sessions.Single();
        line.HasReport.Should().BeFalse("esta sesión no dejó markdown");
        vm.OpenSessionCommand.Execute(line);

        opener.Opened.Single().Should().Be(vm.ReportPathFor(line.Slug, line.SessionId));
        toasts.Items.Should().ContainSingle(t => t.Text.Contains("no dejó informe"));
    }

    [Fact]
    public async Task Un_clic_en_una_sesion_con_informe_lo_abre()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        AuditSession session = WriteSession("app", cost: 2m);
        _hub.Store.WriteReport("app", session.Id.ToString(), "# Informe");

        var opener = new TestFactory.RecordingFileOpener();
        var toasts = new ToastCenter();
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, opener: opener, toasts: toasts);
        await vm.LoadAsync();

        SessionLine line = vm.Sessions.Single();
        line.HasReport.Should().BeTrue();
        vm.OpenSessionCommand.Execute(line);

        opener.Opened.Should().ContainSingle();
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

    // ============================================ Utilidades

    private AuditSession WriteSession(string slug, decimal? cost, int daysAgo = 2)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = AuditMode.Lotes,
            By = "alvaro",
            Machine = "PC",
            StartedUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo),
            CycleN = 1,
        };
        session.Units.Add(new UnitVerdictRecord("src/A.cs", "src", "auditada", null));
        session.Usage.Add(100, 20, cost);
        _hub.Store.WriteSession(session);
        return session;
    }

    private void WriteFinding(string slug, DateTimeOffset detected)
    {
        var stamp = new DetectionStamp(detected, AuditMode.Lotes, "abc", "alvaro");
        _hub.Store.WriteFinding(slug, new Finding
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
        });
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
