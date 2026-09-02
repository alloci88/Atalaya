using System.Text.RegularExpressions;
using System.Windows.Media;
using Atalaya.App.Controls;
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
/// F17 §6 — <b>la cinta de ciclos</b>: la historia de auditoría de cada aplicación, un tramo por
/// ciclo coloreado por su temática. Lo que se fija: de dónde sale cada fecha (y que lo que no se
/// sabe se dice), que los ciclos anteriores a F17 son General, que el filtro recorta y no inventa,
/// que las bandas van como el Portafolio, que la paleta se midió, y que el clic lleva al informe
/// del cierre o al inventario. Nada se pinta aquí: la geometría se afirma sobre el cálculo.
/// </summary>
public sealed class CycleRibbonTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public CycleRibbonTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-ribbon", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        TestRates.Seed(_hub);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
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

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void App(string slug, string name, int cycle)
        => _hub.Store.WriteApp(new AppConfig { Slug = slug, Name = name, RepoUrl = $"u/{slug}", CurrentCycle = cycle });

    private AuditSession Session(string slug, int cycle, int daysAgo, AuditMode mode = AuditMode.Lotes, decimal? cost = null, int units = 1)
    {
        var s = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = mode,
            By = "alvaro",
            Machine = "PC",
            StartedUtc = Now.AddDays(-daysAgo),
            EndedUtc = Now.AddDays(-daysAgo).AddHours(1),
            CycleN = cycle,
        };
        for (int i = 0; i < units; i++)
        {
            s.Units.Add(new UnitVerdictRecord($"src/U{i}.cs", "src", "auditada", null));
        }

        if (cost is not null)
        {
            TestRates.CostAs(s, cost, inputTokens: 100);
        }

        _hub.Store.WriteSession(s);
        return s;
    }

    private void Inventory(string slug, int cycle, AuditTheme? theme = null, DateTimeOffset? opened = null, int audited = 1, int pending = 0, int large = 0)
    {
        var inv = new InventoryCycle { CycleN = cycle, OpenedUtc = opened };
        if (theme is { } t)
        {
            inv.Theme = t;
        }

        int i = 0;
        foreach ((UnitState state, int n) in new[] { (UnitState.Auditada, audited), (UnitState.Pendiente, pending), (UnitState.Grande, large) })
        {
            for (int k = 0; k < n; k++)
            {
                inv.Units.Add(new InventoryUnit { Path = $"src/U{i++}.cs", Module = "src", Loc = 10, State = state });
            }
        }

        _hub.Store.WriteInventory(slug, inv);
    }

    private void Finding(string slug, int daysAgo, Severity severity = Severity.Alta, int? resolvedDaysAgo = null)
    {
        var stamp = new DetectionStamp(Now.AddDays(-daysAgo), AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "criterio.x",
            Pillar = Pillar.Errores,
            Severity = severity,
            Confidence = Confidence.Media,
            Title = "t",
            Locations = { new Location("src/U0.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        if (resolvedDaysAgo is { } r)
        {
            f.Resolve(new ResolutionStamp(Now.AddDays(-r), ResolutionVia.Auditor, AuditMode.Lotes, "abc", "alvaro", "ok"));
        }

        _hub.Store.WriteFinding(slug, f);
    }

    private IReadOnlyList<CycleTrack> Tracks(string? slug = null, MetricsRange range = MetricsRange.All)
        => new MetricsQuery(_hub, new FakeTime(Now)).Build(new MetricsFilter(slug, range)).Cycles;

    // ---------------------------------------------------------------- las fechas y el pasado

    /// <summary>
    /// Un ciclo anterior a F17: sin temática en su fichero (General), sin fecha de apertura
    /// (inferida de su primera sesión) y cerrado por una sesión de cierre (fecha exacta, con
    /// informe). El ciclo 2, abierto, llega hasta hoy.
    /// </summary>
    [Fact]
    public void Un_ciclo_legado_es_General_empieza_en_su_primera_sesion_y_termina_en_su_cierre()
    {
        App("app", "App", 2);
        Inventory("app", 1, theme: null, audited: 3, pending: 0, large: 1);
        Inventory("app", 2, theme: AuditTheme.Seguridad, opened: Now.AddDays(-10), audited: 1, pending: 2);
        Session("app", 1, daysAgo: 40);
        Session("app", 1, daysAgo: 20);
        AuditSession close = Session("app", 2, daysAgo: 10, AuditMode.Cierre, units: 0);

        CycleTrack track = Tracks().Single();
        track.Spans.Should().HaveCount(2);

        CycleSpan c1 = track.Spans[0];
        c1.Theme.Should().Be(AuditTheme.General, "antes de F17 no había temática");
        c1.From.Should().Be(Now.AddDays(-40));
        c1.StartEdge.Should().Be(CycleEdge.Inferred);
        c1.To.Should().Be(close.StartedUtc);
        c1.EndEdge.Should().Be(CycleEdge.Exact);
        c1.IsOpen.Should().BeFalse();
        c1.ReportSessionId.Should().Be(close.Id.ToString(), "el informe del cierre es el de esa sesión");
        c1.Audited.Should().Be(3);
        c1.Auditable.Should().Be(3, "las grandes no son auditables");
        c1.Label.Should().Be("C1 · General");

        CycleSpan c2 = track.Spans[1];
        c2.Theme.Should().Be(AuditTheme.Seguridad);
        c2.From.Should().Be(Now.AddDays(-10));
        c2.StartEdge.Should().Be(CycleEdge.Exact);
        c2.IsOpen.Should().BeTrue();
        c2.To.Should().Be(Now, "el ciclo abierto llega hasta hoy");
        c2.ReportSessionId.Should().BeNull();
        c2.Label.Should().Be("C2 · Seguridad");
    }

    /// <summary>Sin cierre recuperable, el tramo termina en la última sesión y lo dice. Nada se rellena.</summary>
    [Fact]
    public void Sin_fecha_de_cierre_recuperable_el_tramo_termina_donde_alcanza_el_dato_y_lo_dice()
    {
        App("app", "App", 2);
        Inventory("app", 1);
        Session("app", 1, daysAgo: 30);
        AuditSession last = Session("app", 1, daysAgo: 15);
        Session("app", 2, daysAgo: 5); // el ciclo 2 tiene sesiones pero nadie registró su apertura

        CycleTrack track = Tracks().Single();

        CycleSpan c1 = track.Spans[0];
        c1.To.Should().Be(last.EndedUtc);
        c1.EndEdge.Should().Be(CycleEdge.Inferred);
        c1.EndIsKnown.Should().BeFalse();
        MetricsViewModel.CycleTooltip(c1).Should().Contain(l => l.Contains("Sin fecha de cierre recuperable"));

        CycleSpan c2 = track.Spans[1];
        c2.StartEdge.Should().Be(CycleEdge.Inferred, "su primera sesión");
        c2.HasInventory.Should().BeFalse();
        MetricsViewModel.CycleTooltip(c2).Should().Contain("Cobertura: sin inventario conservado para este ciclo.");
    }

    [Fact]
    public void Un_ciclo_cerrado_por_reinicio_no_tiene_informe_que_abrir()
    {
        App("app", "App", 2);
        Inventory("app", 1);
        Session("app", 1, daysAgo: 30);
        AuditSession reset = Session("app", 2, daysAgo: 10, AuditMode.Reset, units: 0);

        CycleSpan c1 = Tracks().Single().Spans[0];

        c1.To.Should().Be(reset.StartedUtc);
        c1.EndEdge.Should().Be(CycleEdge.Exact);
        c1.ReportSessionId.Should().BeNull("el reinicio no escribe informe");
        MetricsViewModel.CycleTooltip(c1).Should().Contain("Este ciclo no dejó informe de cierre.");
    }

    [Fact]
    public void Una_app_sin_ningun_dato_no_tiene_tramo()
    {
        App("vacia", "Vacía", 1);

        Tracks().Should().BeEmpty("ni sesiones, ni hallazgos, ni fecha: no hay de qué colgar un tramo");
    }

    // ---------------------------------------------------------------- la foto de cada tramo

    [Fact]
    public void El_tooltip_lleva_cobertura_hallazgos_y_coste_facturable_del_ciclo()
    {
        App("app", "App", 2);
        Inventory("app", 1, audited: 4, pending: 1, large: 1);
        Session("app", 1, daysAgo: 40, cost: 10m);
        Session("app", 1, daysAgo: 30, cost: 5.5m);
        AuditSession close = Session("app", 2, daysAgo: 20, AuditMode.Cierre, units: 0);
        Finding("app", daysAgo: 35);
        Finding("app", daysAgo: 33, resolvedDaysAgo: 25);
        Finding("app", daysAgo: 10); // del ciclo 2, no del 1

        CycleSpan c1 = Tracks().Single().Spans[0];

        c1.NewFindings.Should().Be(2);
        c1.ResolvedFindings.Should().Be(1);
        c1.Cost.Should().Be(15.5m);
        c1.Audited.Should().Be(4);
        c1.Auditable.Should().Be(5);

        IReadOnlyList<string> tip = MetricsViewModel.CycleTooltip(c1);
        tip[0].Should().Be("Ciclo 1 · General");
        tip.Should().Contain(l => l.StartsWith("Del ") && l.Contains(" al "));
        tip.Should().Contain("Auditadas al cierre: 4 / 5 auditables");
        tip.Should().Contain("Hallazgos: +2 nuevos · −1 resueltos");
        tip.Should().Contain(l => l.StartsWith("Coste: 15,5 AI credits"));
        tip.Should().Contain("Pulsa para abrir el informe del cierre.");
        close.Should().NotBeNull();
    }

    /// <summary>Solo lo facturable: un ciclo auditado con Claude Code no tiene coste que enseñar.</summary>
    [Fact]
    public void Un_ciclo_sin_nada_facturable_no_inventa_un_cero()
    {
        App("app", "App", 1);
        Inventory("app", 1);
        AuditSession s = Session("app", 1, daysAgo: 5);
        s.Provider = "claude-code";
        s.Usage = new UsageTotals { InputTokens = 1000, OutputTokens = 100 };
        _hub.Store.WriteSession(s);

        CycleSpan open = Tracks().Single().Spans.Single();

        open.Cost.Should().BeNull();
        MetricsViewModel.CycleTooltip(open).Should().Contain("Coste: — (nada facturable en este ciclo)");
        MetricsViewModel.CycleTooltip(open).Should().Contain(l => l.StartsWith("Auditadas: 1 / 1 auditables (ahora)"));
    }

    // ---------------------------------------------------------------- filtros y orden

    [Fact]
    public void El_filtro_de_aplicacion_deja_una_banda_y_el_de_periodo_recorta_sin_inventar()
    {
        App("a", "Alpha", 2);
        App("b", "Beta", 1);
        Inventory("a", 1);
        Inventory("a", 2, opened: Now.AddDays(-3));
        Session("a", 1, daysAgo: 200);
        Session("a", 2, daysAgo: 100, AuditMode.Cierre, units: 0);
        Inventory("b", 1);
        Session("b", 1, daysAgo: 2);

        Tracks("b").Should().ContainSingle().Which.Name.Should().Be("Beta");
        Tracks(null, MetricsRange.All).Should().HaveCount(2);

        // Cuatro semanas: el ciclo 1 de Alpha (hace 200-100 días) queda fuera; el 2 se ve.
        IReadOnlyList<CycleTrack> recent = Tracks(null, MetricsRange.Weeks4);
        recent.Single(t => t.Slug == "a").Spans.Should().ContainSingle().Which.CycleN.Should().Be(2);
    }

    [Fact]
    public void Las_bandas_van_en_el_orden_del_portafolio()
    {
        App("limpia", "Aaa limpia", 1);
        App("critica", "Zzz crítica", 1);
        App("deuda", "Mmm deuda", 1);
        foreach (string slug in new[] { "limpia", "critica", "deuda" })
        {
            Inventory(slug, 1);
            Session(slug, 1, daysAgo: 3);
        }

        Finding("critica", daysAgo: 2, Severity.Critica);
        Finding("deuda", daysAgo: 2, Severity.Media);

        Tracks().Select(t => t.Slug).Should().Equal("critica", "deuda", "limpia");
    }

    // ---------------------------------------------------------------- la paleta

    [Fact]
    public void Cada_tematica_tiene_dos_pasos_medidos_contra_su_superficie()
    {
        foreach (AuditTheme theme in ThemeCatalog.All)
        {
            ThemeColor c = ThemePalette.Of(theme);
            Contrast(c.Light, ThemePalette.LightSurface).Should().BeGreaterThanOrEqualTo(3.0, $"{theme} claro sobre su fondo");
            Contrast(c.Dark, ThemePalette.DarkSurface).Should().BeGreaterThanOrEqualTo(3.0, $"{theme} oscuro sobre su fondo");
            Luma(c.Dark).Should().BeGreaterThan(Luma(c.Light), $"{theme}: el paso del tema oscuro es el claro de los dos");
        }
    }

    [Fact]
    public void Las_seis_tematicas_se_distinguen_entre_si_en_los_dos_temas()
    {
        foreach (bool dark in new[] { false, true })
        {
            var hexes = ThemeCatalog.All.Select(t => ThemePalette.Hex(t, dark)).ToList();
            hexes.Should().OnlyHaveUniqueItems();
            for (int i = 0; i < hexes.Count; i++)
            {
                for (int j = i + 1; j < hexes.Count; j++)
                {
                    Distance(hexes[i], hexes[j]).Should().BeGreaterThanOrEqualTo(40,
                        $"{ThemeCatalog.All[i]} y {ThemeCatalog.All[j]} ({(dark ? "oscuro" : "claro")})");
                }
            }
        }
    }

    /// <summary>Los colores de severidad siguen reservados a chips y roscos: ninguna temática se les acerca.</summary>
    [Fact]
    public void Ninguna_tematica_colisiona_con_un_color_de_severidad()
    {
        foreach (AuditTheme theme in ThemeCatalog.All)
        {
            foreach (string severity in SeverityPalette.All)
            {
                ThemePalette.Hex(theme, false).Should().NotBe(severity);
                ThemePalette.Hex(theme, true).Should().NotBe(severity);
                Distance(ThemePalette.Hex(theme, false), severity).Should().BeGreaterThanOrEqualTo(40, $"{theme} claro vs {severity}");
                Distance(ThemePalette.Hex(theme, true), severity).Should().BeGreaterThanOrEqualTo(40, $"{theme} oscuro vs {severity}");
            }
        }
    }

    /// <summary>La tinta de la etiqueta va por paso (D-649): blanco sobre los oscuros, negro sobre los claros, y las dos legibles.</summary>
    [Fact]
    public void La_etiqueta_del_tramo_se_lee_sobre_cada_paso()
    {
        foreach (AuditTheme theme in ThemeCatalog.All)
        {
            foreach (bool dark in new[] { false, true })
            {
                string hex = ThemePalette.Hex(theme, dark);
                var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                Brush ink = CycleRibbon.InkFor(fill);
                string inkHex = ReferenceEquals(ink, Brushes.Black) ? "#000000" : "#FFFFFF";
                Contrast(hex, inkHex).Should().BeGreaterThanOrEqualTo(4.5, $"{theme} ({(dark ? "oscuro" : "claro")}) con tinta {inkHex}");
            }
        }
    }

    [Fact]
    public void General_es_el_neutro_y_va_en_gris()
    {
        foreach (bool dark in new[] { false, true })
        {
            Color c = (Color)ColorConverter.ConvertFromString(ThemePalette.Hex(AuditTheme.General, dark));
            (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B))).Should().BeLessThan(30, "un gris sobrio");
        }
    }

    // ---------------------------------------------------------------- la geometría de la cinta

    [Fact]
    public void La_cinta_nunca_es_mas_estrecha_que_su_ventana_y_respeta_el_ancho_minimo_por_tramo()
    {
        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 9, 3);
        RibbonSpan Span(DateTime a, DateTime b) => new("C", "C", Brushes.Gray, a, b, false, true, new[] { "t" });
        var tracks = new[]
        {
            new RibbonTrack("App", new[]
            {
                Span(from, from.AddDays(3)),          // un ciclo de TRES días en un eje de ocho meses
                Span(from.AddDays(3), to),
            }),
        };

        (double wide, _, _, _) = CycleRibbon.Geometry(tracks, from, to, viewport: 3000, minSpan: 28, gutter: 60);
        wide.Should().Be(3000, "cabe: la cinta ocupa su ventana y no más");

        (double narrow, double height, double scale, _) = CycleRibbon.Geometry(tracks, from, to, viewport: 900, minSpan: 28, gutter: 60);
        narrow.Should().BeGreaterThan(900, "el tramo de tres días no llega a 28 px a esa escala: la cinta crece y su ventana se desplaza");
        (scale * 3).Should().BeGreaterThanOrEqualTo(28, "el tramo de tres días mide al menos el mínimo");
        height.Should().BeGreaterThan(24, "una banda más el eje");
    }

    [Fact]
    public void Las_marcas_del_eje_se_anclan_al_ultimo_dia_y_cambian_de_grano_con_el_periodo()
    {
        var today = new DateTime(2026, 9, 2);
        DateTime to = today.AddDays(1);

        IReadOnlyList<(DateTime When, string Label)> weeks = CycleRibbon.Ticks(to.AddDays(-28), to);
        weeks.Last().When.Should().Be(today, "hoy siempre tiene marca");
        weeks.Select(t => t.When).Should().BeInAscendingOrder();
        weeks.Zip(weeks.Skip(1)).Should().OnlyContain(p => (p.Second.When - p.First.When).TotalDays == 7);

        IReadOnlyList<(DateTime When, string Label)> months = CycleRibbon.Ticks(to.AddDays(-180), to);
        months.Last().When.Should().Be(today);
        months.Take(months.Count - 1).Should().OnlyContain(t => t.When.Day == 1, "marcas de mes");

        IReadOnlyList<(DateTime When, string Label)> quarters = CycleRibbon.Ticks(to.AddDays(-800), to);
        quarters.Take(quarters.Count - 1).Should().OnlyContain(t => t.When.Day == 1 && (t.When.Month - 1) % 3 == 0, "trimestres");
    }

    // ---------------------------------------------------------------- la vista y el clic

    [Fact]
    public void La_cinta_vive_en_su_propio_desplazamiento_horizontal_y_nunca_en_el_de_la_pagina()
    {
        string xaml = File.ReadAllText(Source("src/Atalaya.App/Views/MetricsView.xaml"));
        int ribbon = xaml.IndexOf("<controls:CycleRibbon", StringComparison.Ordinal);
        ribbon.Should().BePositive();

        string before = xaml[..ribbon];
        int scroll = before.LastIndexOf("<ScrollViewer", StringComparison.Ordinal);
        string opener = xaml[scroll..ribbon];
        opener.Should().Contain("HorizontalScrollBarVisibility=\"Auto\"");
        xaml.Should().Contain("ViewportWidth=\"{Binding ViewportWidth, ElementName=RibbonScroll}\"");
        xaml.Should().Contain("SpanCommand=\"{Binding OpenCycleCommand}\"");
        Regex.Matches(xaml, "Ciclos y temáticas").Count.Should().Be(1);
    }

    [Fact]
    public async Task Un_clic_en_un_tramo_cerrado_abre_el_informe_de_su_cierre()
    {
        App("app", "App", 2);
        Inventory("app", 1);
        Session("app", 1, daysAgo: 30);
        AuditSession close = Session("app", 2, daysAgo: 10, AuditMode.Cierre, units: 0);
        _hub.Store.WriteReport("app", close.Id.ToString(), "# Cierre de ciclo 1 — App\n");
        var reports = TestFactory.Reports(_hub);
        var navigation = TestFactory.NavigationWith(reports);
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, navigation);
        await vm.LoadAsync();

        vm.HasCycles.Should().BeTrue();
        vm.CycleTracks.Single().Spans.Should().HaveCount(2);
        var closed = (CycleSpanRef)vm.CycleTracks.Single().Spans[0].Payload!;
        await vm.OpenCycleCommand.ExecuteAsync(closed);

        navigation.Current.Should().BeSameAs(reports);
        reports.IsViewing.Should().BeTrue();
        reports.OpenReport!.Entry.ReportId.Should().Be(close.Id.ToString(), "el informe de ESE cierre");
    }

    [Fact]
    public async Task Un_clic_en_el_ciclo_abierto_lleva_al_inventario_de_la_app()
    {
        App("app", "App", 1);
        Inventory("app", 1);
        Session("app", 1, daysAgo: 3);
        var inventory = TestFactory.Inventory(_hub, _paths, new MachineConfigStore(_paths.MachinesJson), _ulids, _settings, new ToastCenter());
        var navigation = TestFactory.NavigationWith(inventory);
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, navigation);
        await vm.LoadAsync();

        var open = (CycleSpanRef)vm.CycleTracks.Single().Spans.Single().Payload!;
        open.IsOpen.Should().BeTrue();
        await vm.OpenCycleCommand.ExecuteAsync(open);

        navigation.Current.Should().BeSameAs(inventory);
        inventory.Slug.Should().Be("app");
    }

    [Fact]
    public async Task Un_clic_en_un_ciclo_sin_informe_lo_dice_en_vez_de_no_hacer_nada()
    {
        App("app", "App", 2);
        Inventory("app", 1);
        Session("app", 1, daysAgo: 30);
        Session("app", 2, daysAgo: 10, AuditMode.Reset, units: 0);
        var toasts = new ToastCenter();
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings, toasts: toasts);
        await vm.LoadAsync();

        await vm.OpenCycleCommand.ExecuteAsync((CycleSpanRef)vm.CycleTracks.Single().Spans[0].Payload!);

        toasts.Items.Should().ContainSingle().Which.Text.Should().Contain("no dejó informe de cierre");
    }

    [Fact]
    public async Task La_leyenda_nombra_solo_las_tematicas_que_se_ven_y_en_el_orden_del_catalogo()
    {
        App("app", "App", 2);
        Inventory("app", 1, theme: AuditTheme.Rendimiento);
        Inventory("app", 2, theme: AuditTheme.Seguridad, opened: Now.AddDays(-2));
        Session("app", 1, daysAgo: 30);
        Session("app", 2, daysAgo: 2, AuditMode.Cierre, units: 0);
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        vm.CycleLegend.Select(l => l.Name).Should().Equal("Seguridad", "Rendimiento");
        vm.ShowCycleLegend.Should().BeTrue();
        vm.CycleTracks.Single().Spans[0].Label.Should().Be("C1 · Rendimiento");
        vm.CycleTracks.Single().Spans[1].IsOpen.Should().BeTrue();
    }

    // ---------------------------------------------------------------- utilidades

    private static double Luma(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Lin(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static double Contrast(string a, string b)
    {
        double la = Luma(a);
        double lb = Luma(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Distance(string a, string b)
    {
        var x = (Color)ColorConverter.ConvertFromString(a);
        var y = (Color)ColorConverter.ConvertFromString(b);
        return Math.Sqrt(Math.Pow(x.R - y.R, 2) + Math.Pow(x.G - y.G, 2) + Math.Pow(x.B - y.B, 2));
    }

    private static string Source(string relative)
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Atalaya.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("Atalaya.sln");
        }

        return Path.Combine(dir, relative);
    }
}
