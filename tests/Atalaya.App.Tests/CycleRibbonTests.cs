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

    /// <summary>
    /// F17.1 — una app sin apertura ni sesión no tiene tramo, pero SÍ banda: vacía y rotulada. Y
    /// sus hallazgos medidos no cuelgan ningún ciclo: en F17 lo hacían, y XBLAST —que nadie
    /// había auditado— salía con un «C1 · General» desde el día en que se midieron sus unidades
    /// grandes.
    /// </summary>
    [Fact]
    public void Una_app_sin_apertura_ni_sesiones_tiene_banda_vacia_y_ningun_tramo_inventado()
    {
        App("vacia", "Vacía", 1);
        Inventory("vacia", 1, theme: null, audited: 0, pending: 3, large: 1);
        Finding("vacia", daysAgo: 1); // un hallazgo medido no es una auditoría

        CycleTrack track = Tracks().Should().ContainSingle().Subject;

        track.Slug.Should().Be("vacia");
        track.Spans.Should().BeEmpty("ni apertura ni sesión: no hay ciclo que pintar, y no se inventa");
    }

    /// <summary>El caso real del parte de F17.1: lo que hay en el hub, lado a lado con lo que se pinta.</summary>
    [Fact]
    public void El_caso_del_parte_una_app_con_un_ciclo_de_dos_lupas_y_otra_sin_ninguno()
    {
        App("banco", "AtalayaBanco", 1);
        DateTimeOffset opened = Now.AddHours(-2);
        DateTimeOffset changed = Now.AddMinutes(-40);
        var inv = new InventoryCycle { CycleN = 1, OpenedUtc = opened, Units = { new InventoryUnit { Path = "a.cs", Module = "m", State = UnitState.Auditada } } };
        inv.OpenThemeHistory(AuditTheme.Rendimiento, opened, "alopezciller");
        inv.ChangeTheme(AuditTheme.Seguridad, changed, "alopezciller");
        _hub.Store.WriteInventory("banco", inv);
        Session("banco", 1, daysAgo: 0);
        App("xblast", "XBLAST", 1);
        Inventory("xblast", 1, theme: null, audited: 0, pending: 5);
        Finding("xblast", daysAgo: 1);

        IReadOnlyList<CycleTrack> tracks = Tracks(null, MetricsRange.Weeks8);

        tracks.Select(t => t.Slug).Should().Equal("xblast", "banco"); // el orden del Portafolio: la deuda viva primero
        CycleSpan c1 = tracks[1].Spans.Should().ContainSingle().Subject;
        c1.Label.Should().Be("C1 · Rendimiento → Seguridad");
        c1.Slices.Should().HaveCount(2);
        c1.Slices[0].Theme.Should().Be(AuditTheme.Rendimiento);
        c1.Slices[0].From.Should().Be(opened);
        c1.Slices[0].To.Should().Be(changed, "el corte está en la fecha del cambio");
        c1.Slices[1].Theme.Should().Be(AuditTheme.Seguridad);
        c1.Slices[1].From.Should().Be(changed);
        c1.Slices[1].By.Should().Be("alopezciller");
        tracks[0].Spans.Should().BeEmpty("XBLAST no tiene ningún ciclo auditado");
    }

    [Fact]
    public void Un_ciclo_con_una_sola_tematica_tiene_un_trozo_y_su_etiqueta_de_siempre()
    {
        App("app", "App", 1);
        Inventory("app", 1, theme: AuditTheme.Fiabilidad, opened: Now.AddDays(-3));
        Session("app", 1, daysAgo: 1);

        CycleSpan span = Tracks().Single().Spans.Single();

        span.Slices.Should().ContainSingle().Which.Theme.Should().Be(AuditTheme.Fiabilidad);
        span.Label.Should().Be("C1 · Fiabilidad");
        span.ChangedTheme.Should().BeFalse();
        MetricsViewModel.CycleTooltip(span)[0].Should().Be("Ciclo 1 · Fiabilidad");
    }

    /// <summary>Un ciclo de antes de F17.1 no tiene historial escrito: un trozo desde su inicio, con la temática vigente.</summary>
    [Fact]
    public void Un_ciclo_sin_historial_escrito_deriva_un_solo_periodo_desde_su_inicio()
    {
        App("app", "App", 1);
        Inventory("app", 1, theme: AuditTheme.Seguridad, opened: Now.AddDays(-3));
        InventoryCycle inv = _hub.Store.TryReadInventory("app", 1)!;
        inv.ThemeHistory.Should().BeEmpty();
        Session("app", 1, daysAgo: 1);

        CycleSpan span = Tracks().Single().Spans.Single();

        span.Slices.Should().ContainSingle();
        span.Slices[0].Theme.Should().Be(AuditTheme.Seguridad);
        span.Slices[0].From.Should().Be(span.From);
        span.Slices[0].To.Should().Be(span.To);
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

        // Cuatro semanas: el ciclo 1 de Alpha (hace 200-100 días) queda fuera; el 2 se ve, y se
        // DICE que hay uno anterior fuera del periodo (F17.2: recortar por pertenencia, no
        // fabricar un eje).
        IReadOnlyList<CycleTrack> recent = Tracks(null, MetricsRange.Weeks4);
        CycleTrack alpha = recent.Single(t => t.Slug == "a");
        alpha.Spans.Should().ContainSingle().Which.CycleN.Should().Be(2);
        alpha.HiddenEarlier.Should().Be(1);
        alpha.Notice.Should().Be("1 ciclo anterior fuera del periodo");
        recent.Single(t => t.Slug == "b").Notice.Should().BeEmpty();
        Tracks(null, MetricsRange.All).Single(t => t.Slug == "a").HiddenEarlier.Should().Be(0);
    }

    [Fact]
    public void Las_fechas_del_capitulo_se_escriben_cortas_y_honestas()
    {
        App("app", "App", 3);
        Inventory("app", 1, opened: new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));
        Inventory("app", 2, opened: new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero));
        Inventory("app", 3, opened: new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        Session("app", 1, daysAgo: 10);
        Session("app", 2, daysAgo: 3, AuditMode.Cierre, units: 0);
        Session("app", 3, daysAgo: 1, AuditMode.Cierre, units: 0);

        IReadOnlyList<CycleSpan> spans = Tracks().Single().Spans;

        MetricsViewModel.CycleDates(spans[0]).Should().Be("14 ago – 30 ago");
        MetricsViewModel.CycleDates(spans[1]).Should().Be("30 ago – 1 sept");
        MetricsViewModel.CycleDates(spans[2]).Should().Be("1 sept – en curso");
        MetricsViewModel.CycleDates(spans[0] with { To = spans[0].From.AddHours(2) }).Should().Be("14 ago", "empezó y acabó el mismo día");
        MetricsViewModel.CycleDates(spans[0] with { From = new DateTimeOffset(2025, 12, 20, 10, 0, 0, TimeSpan.Zero) })
            .Should().Be("20 dic 2025 – 30 ago 2026", "cruza el año");
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

    // ---------------------------------------------------------------- la vista y el clic

    /// <summary>
    /// F17.1 — la cinta lleva DENTRO su desplazamiento (y su columna fija de nombres): la vista ya
    /// no la envuelve en un ScrollViewer, porque un nombre que viaja con el lienzo acaba frente a
    /// la banda de otra app. Y jamás el scroll horizontal de la página.
    /// </summary>
    [Fact]
    public void La_cinta_lleva_dentro_su_desplazamiento_y_nunca_usa_el_de_la_pagina()
    {
        // Sin finales de linea: `core.autocrlf` esta en true, asi que el fichero llega con CRLF o
        // con LF segun quien lo haya escrito por ultima vez, y una regla de diseno no puede
        // depender de eso (UI-AUDIT-1: este test empezo a fallar al retocar la vista, sin que la
        // regla que protege hubiera cambiado).
        string xaml = File.ReadAllText(Source("src/Atalaya.App/Views/MetricsView.xaml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Regex.Matches(xaml, "<controls:CycleRibbon").Count.Should().Be(1);
        xaml.Should().NotContain("ViewportWidth=", "la cinta mide su propia ventana");
        xaml.Should().NotContain("RibbonFrom").And.NotContain("RibbonTo", "F17.2: ya no hay eje de calendario");
        // Las OTRAS gráficas de eje siguen ahí, y desde F35 §2.7 son cuatro: coste, resoluciones,
        // flujo y antigüedad de la deuda. Esto decía que el fichero no contenía
        // «controls:ChartPlot» seguido de un salto de línea, y solo pasaba porque el fichero tenía
        // CRLF: con LF, la misma vista intacta lo rompía. Una regla que depende de los finales de
        // línea no es una regla (UI-AUDIT-1).
        Regex.Matches(xaml, "<controls:ChartPlot").Count.Should().Be(4, "las demás gráficas no se tocan");
        xaml.Should().NotContain("x:Name=\"RibbonScroll\"", "el desplazamiento vive en el control, con la fila entera");
        xaml.Should().Contain("SpanCommand=\"{Binding OpenCycleCommand}\"");
        Regex.Matches(xaml, "Ciclos y temáticas").Count.Should().Be(1);
        typeof(CycleRibbon).Should().BeDerivedFrom<System.Windows.Controls.Grid>("columna fija de nombres + área desplazable");
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

    /// <summary>F17.1 — un ciclo que cambió de lupa llega a la cinta partido, con un tooltip por trozo y los dos colores.</summary>
    [Fact]
    public async Task Un_ciclo_que_cambio_de_lupa_llega_a_la_cinta_en_dos_trozos_con_sus_colores()
    {
        App("app", "App", 1);
        DateTimeOffset opened = Now.AddDays(-4);
        DateTimeOffset changed = Now.AddDays(-1);
        var inv = new InventoryCycle { CycleN = 1, OpenedUtc = opened };
        inv.OpenThemeHistory(AuditTheme.Rendimiento, opened, "ana");
        inv.ChangeTheme(AuditTheme.Seguridad, changed, "maría");
        _hub.Store.WriteInventory("app", inv);
        Session("app", 1, daysAgo: 2);
        MetricsViewModel vm = TestFactory.Metrics(_hub, _paths, _settings);
        await vm.LoadAsync();

        RibbonSpan span = vm.CycleTracks.Single().Spans.Single();

        span.Slices.Should().HaveCount(2);
        span.Slices[0].To.Should().Be(changed.ToLocalTime().DateTime, "el corte está en la fecha del cambio");
        Hex(span.Slices[0].Fill).Should().Be(ThemePalette.Hex(AuditTheme.Rendimiento, dark: true));
        Hex(span.Slices[1].Fill).Should().Be(ThemePalette.Hex(AuditTheme.Seguridad, dark: true));
        span.Slices[1].TooltipLines.Single().Should().StartWith("Temática Seguridad · desde el ").And.EndWith("cambiada por maría");
        span.Label.Should().Be("C1 · Rendimiento → Seguridad");
        span.TooltipLines[0].Should().Be("Ciclo 1 · Rendimiento → Seguridad");
        vm.CycleLegend.Select(l => l.Name).Should().Equal("Seguridad", "Rendimiento");
    }

    private static string Hex(Brush brush)
    {
        Color c = ((SolidColorBrush)brush).Color;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
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
