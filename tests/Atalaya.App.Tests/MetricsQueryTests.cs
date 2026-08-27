using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.9 §3 y §4 — la agregación del panel de mando.
/// <para>
/// Todo lo de aquí sale de datos primarios ya persistidos: hallazgos con su historial, sesiones
/// con su coste y el inventario del ciclo. No hay ni un fichero derivado en el hub, y esta clase
/// es la que lo mantiene así: si algún agregado dejara de poder calcularse, estos tests lo dirían
/// antes de que alguien se sintiera tentado de guardarlo.
/// </para>
/// </summary>
public sealed class MetricsQueryTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    public MetricsQueryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-metrics", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(_paths);
        settings.Load();
        _hub = TestFactory.Hub(_paths, settings);
        App("app", "App");
    }

    private MetricsQuery Query() => new(_hub, new FakeTime(Now));

    private MetricsDashboard Build(string? slug = null, MetricsRange range = MetricsRange.Weeks8)
        => Query().Build(new MetricsFilter(slug, range));

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void App(string slug, string name, int cycle = 1)
        => _hub.Store.WriteApp(new AppConfig { Slug = slug, Name = name, RepoUrl = $"u/{slug}", CurrentCycle = cycle });

    private Finding Finding(
        FindingStatus status,
        DateTimeOffset detected,
        DateTimeOffset? resolvedAt = null,
        Severity severity = Severity.Alta)
    {
        var stamp = new DetectionStamp(detected, AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
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
        };

        if (status == FindingStatus.Resuelto)
        {
            f.Resolve(new ResolutionStamp(
                resolvedAt ?? detected, ResolutionVia.Manual, AuditMode.Lotes, "c", "alvaro", "fixed"));
        }
        else if (status == FindingStatus.Silenciado)
        {
            f.MarkSilenced(resolvedAt ?? detected, "alvaro", "deuda aceptada");
        }

        return f;
    }

    private AuditSession Session(string slug, DateTimeOffset when, decimal? cost, int units = 1, string by = "alvaro")
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = AuditMode.Lotes,
            By = by,
            Machine = "PC",
            StartedUtc = when,
            EndedUtc = when.AddHours(1),
            CycleN = 1,
        };

        for (int i = 0; i < units; i++)
        {
            session.Units.Add(new UnitVerdictRecord($"src/U{i}.cs", "src", "auditada", null));
        }

        session.Usage.Add(1000, 200, cost);
        _hub.Store.WriteSession(session);
        return session;
    }

    private void Inventory(string slug, int audited, int pending, int large, int cycle = 1)
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

    // =============================================================== Tiles

    [Fact]
    public void Los_activos_se_desglosan_por_severidad_y_no_dependen_del_periodo()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-3), severity: Severity.Critica));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-400), severity: Severity.Baja));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-2)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks4);

        // «Cuántos hallazgos tenemos abiertos» es un estado de HOY, no del periodo: recortarlo
        // por el filtro daría un número más pequeño que la deuda real.
        d.ActiveTotal.Should().Be(2);
        d.Active.Critica.Should().Be(1);
        d.Active.Baja.Should().Be(1);
        d.Active.Alta.Should().Be(0);
    }

    [Fact]
    public void Los_resueltos_se_comparan_contra_el_periodo_anterior_de_la_misma_longitud()
    {
        // Dos en las últimas 4 semanas, tres en las 4 anteriores: el delta baja.
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-60), Now.AddDays(-3)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-60), Now.AddDays(-10)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-90), Now.AddDays(-35)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-90), Now.AddDays(-40)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-90), Now.AddDays(-50)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks4);

        d.ResolvedInPeriod.Should().Be(2);
        d.ResolvedPreviousPeriod.Should().Be(3);
        d.ResolvedDelta.Should().Be(-1);
    }

    [Fact]
    public void El_coste_del_periodo_suma_sesiones_y_saca_la_media_por_unidad_auditada()
    {
        Session("app", Now.AddDays(-2), cost: 30m, units: 3);
        Session("app", Now.AddDays(-9), cost: 10m, units: 2);
        Session("app", Now.AddDays(-200), cost: 999m, units: 1);

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.CostInPeriod.Should().Be(40m, "la sesión de hace 200 días está fuera del periodo");
        d.UnitsAuditedInPeriod.Should().Be(5);
        d.CostPerAuditedUnit.Should().Be(8m);
        d.CostUnit.Should().Be(CostEstimator.DefaultCostUnit, "el SDK no declaró unidad en estas sesiones");
    }

    /// <summary>
    /// La regla del §2 llevada al dato: sin coste medido el agregado es NULO, no cero. Es lo que
    /// permite a la vista escribir «—» en vez de un «0 unidades SDK» que se lee como una medida.
    /// </summary>
    [Fact]
    public void Sin_coste_medido_el_tile_no_vale_cero_sino_nada()
    {
        Session("app", Now.AddDays(-2), cost: null, units: 3);

        MetricsDashboard d = Build();

        d.CostInPeriod.Should().BeNull();
        d.CostPerAuditedUnit.Should().BeNull();
        d.HasCost.Should().BeFalse();
    }

    [Fact]
    public void La_cobertura_del_ciclo_excluye_las_grandes_y_agrega_todas_las_apps()
    {
        App("otra", "Otra");
        Inventory("app", audited: 3, pending: 1, large: 2);
        Inventory("otra", audited: 1, pending: 5, large: 0);

        MetricsDashboard todas = Build();

        todas.CycleAudited.Should().Be(4);
        todas.CyclePending.Should().Be(6);
        todas.CycleLarge.Should().Be(2);
        todas.CyclePct.Should().BeApproximately(0.4, 0.001, "4 auditadas de 10 auditables; las grandes no cuentan");

        // Y el mismo porcentaje que enseñará el rosco de esa app: nunca dos cifras distintas.
        MetricsDashboard una = Build("app");
        una.CyclePct.Should().BeApproximately(0.75, 0.001);
        una.Coverage.Single().Pct.Should().BeApproximately(0.75, 0.001);
    }

    [Fact]
    public void Sin_inventario_la_cobertura_no_finge_un_cero_por_ciento()
    {
        MetricsDashboard d = Build();

        d.HasCycleData.Should().BeFalse();
    }

    // =============================================================== Gráfica 1

    [Fact]
    public void El_coste_por_cubo_reparte_cada_sesion_en_su_tramo()
    {
        Session("app", Now.AddDays(-1), cost: 5m);
        Session("app", Now.AddDays(-2), cost: 7m);
        Session("app", Now.AddDays(-20), cost: 3m);

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.Granularity.Should().Be(MetricsGranularity.Semanal);
        d.Cost.Sum(p => p.Of("app")).Should().Be(15m);
        d.Cost.Count(p => p.Of("app") > 0).Should().Be(2, "dos semanas distintas");
        d.Cost.Single(p => p.Of("app") == 12m).Should().NotBeNull("las dos sesiones de la misma semana se suman");
    }

    /// <summary>
    /// Más de seis aplicaciones: solo seis llevan nombre propio y el resto se suma en «Otras».
    /// Lo que NO puede pasar es que la suma se pierda por el camino.
    /// </summary>
    [Fact]
    public void Mas_de_seis_apps_se_agrupan_en_otras_sin_perder_ni_una_unidad()
    {
        for (int i = 0; i < 9; i++)
        {
            string slug = $"app{i}";
            App(slug, $"App {i}");
            // Coste decreciente: las seis primeras son las que se nombran.
            Session(slug, Now.AddDays(-2), cost: 100m - i);
        }

        MetricsDashboard d = Build();

        d.CostSeriesHasOthers.Should().BeTrue();
        d.CostSeries.Should().HaveCount(SeriesPalette.MaxNamedSeries + 1);
        d.CostSeries.Last().Should().Be(MetricsDashboard.OthersSlug);
        d.NameOf(MetricsDashboard.OthersSlug).Should().Be("Otras");

        decimal dibujado = d.Cost.Sum(p => d.CostSeries.Sum(p.Of));
        dibujado.Should().Be(d.CostInPeriod, "lo agrupado sigue sumando: «Otras» no es un redondeo");
    }

    [Fact]
    public void Con_seis_apps_o_menos_no_hay_serie_agrupada()
    {
        for (int i = 0; i < 6; i++)
        {
            App($"app{i}", $"App {i}");
            Session($"app{i}", Now.AddDays(-2), cost: 10m);
        }

        MetricsDashboard d = Build();

        d.CostSeriesHasOthers.Should().BeFalse();
        d.CostSeries.Should().NotContain(MetricsDashboard.OthersSlug);
    }

    // =============================================================== Gráfica 2

    /// <summary>
    /// El dato de la gráfica: resoluciones por tramo y por aplicación, con el mismo grano que la
    /// de coste. Dos resoluciones de la misma semana caen en el mismo punto; las de otra app van
    /// a su propia línea.
    /// </summary>
    [Fact]
    public void Las_resoluciones_se_reparten_por_semana_y_por_aplicacion()
    {
        App("otra", "Otra");
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-1)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-2)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-20)));
        _hub.Store.WriteFinding("otra", Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-2)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.Granularity.Should().Be(MetricsGranularity.Semanal);
        d.HasResolutions.Should().BeTrue();
        d.ResolutionSeries.Should().BeEquivalentTo(new[] { "app", "otra" });
        d.Resolutions.Sum(p => p.Of("app")).Should().Be(3m);
        d.Resolutions.Sum(p => p.Of("otra")).Should().Be(1m);
        d.Resolutions.Count(p => p.Of("app") > 0).Should().Be(2, "dos semanas distintas");
        d.Resolutions.Should().ContainSingle(p => p.Of("app") == 2m, "las dos de la misma semana se suman");

        // Y comparte el eje X con la de coste: las dos gráficas se leen una debajo de otra.
        d.Resolutions.Select(p => p.Label).Should().Equal(d.Cost.Select(p => p.Label));
    }

    /// <summary>
    /// La gráfica cuenta EVENTOS de resolución, no el neto. Un hallazgo que se resolvió, se
    /// reabrió y se volvió a resolver saldó deuda dos veces, y la reapertura no borra el pasado
    /// —el neto ya lo da el burndown del flujo—. Es además el caso que el campo <c>resolved</c>
    /// por sí solo no sabría contar: al reabrir se pone a null.
    /// </summary>
    [Fact]
    public void Una_reapertura_no_resta_del_pasado_y_la_segunda_resolucion_cuenta_aparte()
    {
        Finding f = Finding(FindingStatus.Resuelto, Now.AddDays(-40), Now.AddDays(-25));
        f.Reopen(Now.AddDays(-18), "alvaro", "volvió a aparecer");
        f.Resolve(new ResolutionStamp(
            Now.AddDays(-3), ResolutionVia.Auditor, AuditMode.Lotes, "c", "alvaro", "arreglado de verdad"));
        _hub.Store.WriteFinding("app", f);

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.Resolutions.Sum(p => p.Of("app")).Should().Be(2m, "dos veces se saldó, dos puntos");
        d.Resolutions.Count(p => p.Of("app") > 0).Should().Be(2, "en tramos distintos");

        // Un hallazgo, dos resoluciones: la gráfica no las colapsa en el estado de hoy.
        d.ResolvedInPeriod.Should().Be(1, "el tile sigue contando el estado, que es lo suyo");
    }

    /// <summary>
    /// Todas las vías cuentan: el veredicto del auditor, la mano de una persona y la medida de la
    /// aplicación. La gráfica mide deuda saldada, no de quién fue el mérito.
    /// </summary>
    [Fact]
    public void Cuentan_las_tres_vias_de_resolucion()
    {
        foreach (ResolutionVia via in new[] { ResolutionVia.Auditor, ResolutionVia.Manual, ResolutionVia.Medida })
        {
            Finding f = Finding(FindingStatus.Activo, Now.AddDays(-30));
            f.Resolve(new ResolutionStamp(Now.AddDays(-2), via, AuditMode.Lotes, "c", "alvaro", "x"));
            _hub.Store.WriteFinding("app", f);
        }

        Build().Resolutions.Sum(p => p.Of("app")).Should().Be(3m);
    }

    [Fact]
    public void Las_resoluciones_obedecen_el_filtro_de_app_y_el_de_rango()
    {
        App("otra", "Otra");
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-60), Now.AddDays(-2)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-90), Now.AddDays(-45)));
        _hub.Store.WriteFinding("otra", Finding(FindingStatus.Resuelto, Now.AddDays(-60), Now.AddDays(-2)));

        MetricsDashboard solo = Build("app");
        solo.ResolutionSeries.Should().Equal(new[] { "app" }, "la otra app no pinta línea aquí");
        solo.Resolutions.Sum(p => p.Of("otra")).Should().Be(0m);

        // Cuatro semanas: la resolución de hace 45 días queda fuera de la ventana.
        MetricsDashboard corto = Build("app", MetricsRange.Weeks4);
        corto.Resolutions.Sum(p => p.Of("app")).Should().Be(1m);
        corto.Resolutions.Should().HaveCount(28, "grano diario, el mismo que la de coste");

        MetricsDashboard largo = Build("app", MetricsRange.Weeks26);
        largo.Resolutions.Sum(p => p.Of("app")).Should().Be(2m);
    }

    /// <summary>
    /// Sin ninguna resolución en el periodo no se dibuja un eje mudo: la vista escribe su nota.
    /// Y «sin resoluciones» no es «sin datos»: puede haber hallazgos activos y sesiones.
    /// </summary>
    [Fact]
    public void Sin_resoluciones_en_el_periodo_no_hay_grafica_que_dibujar()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-3)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-400), Now.AddDays(-380)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.HasResolutions.Should().BeFalse();
        d.ResolutionSeries.Should().BeEmpty();
        d.IsEmpty.Should().BeFalse("hay hallazgos: el panel entero no está vacío");
    }

    /// <summary>
    /// Más de seis aplicaciones con resoluciones: solo seis llevan nombre propio y el resto se
    /// agrupa, con el mismo criterio que la gráfica de coste. Lo agrupado sigue sumando.
    /// </summary>
    [Fact]
    public void Mas_de_seis_apps_resolviendo_se_agrupan_en_otras_sin_perder_ninguna()
    {
        for (int i = 0; i < 9; i++)
        {
            string slug = $"app{i}";
            App(slug, $"App {i}");

            // Resoluciones decrecientes: las seis primeras son las que se nombran.
            for (int k = 0; k <= 9 - i; k++)
            {
                _hub.Store.WriteFinding(slug, Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-2)));
            }
        }

        MetricsDashboard d = Build();

        d.ResolutionSeriesHasOthers.Should().BeTrue();
        d.ResolutionSeries.Should().HaveCount(SeriesPalette.MaxNamedSeries + 1);
        d.ResolutionSeries.Last().Should().Be(MetricsDashboard.OthersSlug);

        decimal dibujado = d.Resolutions.Sum(p => d.ResolutionSeries.Sum(p.Of));
        dibujado.Should().Be(54m, "10+9+…+2 resoluciones: «Otras» no es un redondeo");
    }

    /// <summary>
    /// Un hallazgo traído de V4 no tiene historial: su única prueba de que se resolvió es el
    /// sello. Sin esa reserva, un hub importado dibujaría una gráfica vacía teniendo resoluciones.
    /// </summary>
    [Fact]
    public void Un_resuelto_sin_historial_cuenta_por_su_sello()
    {
        Finding f = Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-2));
        f.History.Clear();
        _hub.Store.WriteFinding("app", f);

        Build().Resolutions.Sum(p => p.Of("app")).Should().Be(1m);
    }

    // =============================================================== Gráfica 3b

    /// <summary>
    /// El dato de la fila: los hallazgos ACTIVOS de cada app repartidos por severidad. Es la foto
    /// de la deuda viva, así que ni los resueltos ni los silenciados cuentan — uno se arregló y
    /// del otro se decidió que no se arregla; sumarlos convertiría la lista de lo que queda por
    /// hacer en un histórico de todo lo que hubo.
    /// </summary>
    [Fact]
    public void Los_roscos_de_severidad_solo_cuentan_los_activos()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Critica));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Alta));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Alta));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Baja));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-9), Now.AddDays(-2),
            severity: Severity.Critica));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Silenciado, Now.AddDays(-3), severity: Severity.Critica));

        SeverityDonut donut = Build().Severity.Single();

        donut.Slug.Should().Be("app");
        donut.Name.Should().Be("App");
        donut.Of(Severity.Critica).Should().Be(1, "el resuelto y el silenciado no son deuda viva");
        donut.Of(Severity.Alta).Should().Be(2);
        donut.Of(Severity.Media).Should().Be(0);
        donut.Of(Severity.Baja).Should().Be(1);
        donut.Total.Should().Be(4);
        donut.HasData.Should().BeTrue();
    }

    /// <summary>
    /// Una aplicación sin activos NO se omite: se dibuja vacía. Que esté limpia es un dato, y
    /// esconderla la haría indistinguible de una que nadie ha auditado nunca.
    /// </summary>
    [Fact]
    public void Una_app_sin_activos_sale_igual_pero_vacia()
    {
        App("limpia", "Limpia");
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Alta));

        var roscos = Build().Severity.ToDictionary(d => d.Slug);

        roscos.Should().HaveCount(2, "las dos aplicaciones salen en la fila");
        roscos["limpia"].Total.Should().Be(0);
        roscos["limpia"].HasData.Should().BeFalse();
        roscos["app"].HasData.Should().BeTrue();
    }

    /// <summary>
    /// Los activos son la foto de HOY, igual que el tile de arriba: el rango temporal no los
    /// recorta. Un hallazgo abierto hace dos años sigue siendo deuda aunque se mire «4 semanas».
    /// </summary>
    [Fact]
    public void El_rango_temporal_no_recorta_los_roscos_de_severidad()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-800), severity: Severity.Critica));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-2), severity: Severity.Media));

        foreach (MetricsRange range in Enum.GetValues<MetricsRange>())
        {
            MetricsDashboard d = Build(range: range);
            d.Severity.Single().Total.Should().Be(2, $"con el rango {range}");
            d.Severity.Single().Of(Severity.Critica).Should().Be(1, $"con el rango {range}");
        }
    }

    /// <summary>El selector de aplicación SÍ manda: es el otro filtro del panel.</summary>
    [Fact]
    public void Los_roscos_de_severidad_obedecen_el_filtro_de_aplicacion()
    {
        App("otra", "Otra");
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Alta));
        _hub.Store.WriteFinding("otra", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Critica));

        Build("app").Severity.Should().ContainSingle().Which.Slug.Should().Be("app");
        Build().Severity.Should().HaveCount(2);
    }

    /// <summary>
    /// La fila se ordena como la de cobertura —de más a menos— para que las dos se lean en el
    /// mismo sentido. Las limpias quedan al final, que es donde estorban menos.
    /// </summary>
    [Fact]
    public void La_fila_va_de_la_app_con_mas_deuda_a_la_que_menos()
    {
        App("media", "Media");
        App("limpia", "Limpia");
        for (int i = 0; i < 3; i++)
        {
            _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Alta));
        }

        _hub.Store.WriteFinding("media", Finding(FindingStatus.Activo, Now.AddDays(-1), severity: Severity.Baja));

        Build().Severity.Select(d => d.Slug).Should().Equal("app", "media", "limpia");
    }

    // =============================================================== Gráfica 4

    /// <summary>
    /// El burndown de verdad: los activos de cada cubo se RECONSTRUYEN a esa fecha. Repetir el
    /// estado de hoy en todos los cubos daría una recta horizontal que no responde a nada.
    /// </summary>
    [Fact]
    public void Los_activos_acumulados_se_reconstruyen_a_la_fecha_de_cada_cubo()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-40)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-40), Now.AddDays(-10)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.Flow.Should().HaveCount(8);
        d.Flow.First().ActiveAtEnd.Should().Be(0, "hace 8 semanas no existía ninguno de los dos");
        d.Flow[3].ActiveAtEnd.Should().Be(2, "creados y todavía sin resolver");
        d.Flow.Last().ActiveAtEnd.Should().Be(1, "uno se resolvió por el camino");
        d.Flow.Sum(b => b.New).Should().Be(2);
        d.Flow.Sum(b => b.Resolved).Should().Be(1);
    }

    // =============================================================== Gráfica 5

    [Fact]
    public void El_registro_de_sesiones_va_de_la_mas_reciente_a_la_mas_antigua_y_esta_acotado()
    {
        for (int i = 0; i < MetricsQuery.MaxSessionRows + 5; i++)
        {
            Session("app", Now.AddDays(-i), cost: 1m, by: $"quien{i}");
        }

        MetricsDashboard d = Build(range: MetricsRange.Weeks26);

        d.Sessions.Should().HaveCount(MetricsQuery.MaxSessionRows);
        d.Sessions.Should().BeInDescendingOrder(s => s.When);
        d.Sessions[0].By.Should().Be("quien0");
        d.Sessions[0].AppName.Should().Be("App", "el registro dice el nombre, no el slug");
    }

    // =============================================================== Filtros y periodo

    [Fact]
    public void Filtrar_por_app_deja_fuera_todo_lo_de_las_demas()
    {
        App("otra", "Otra");
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1)));
        _hub.Store.WriteFinding("otra", Finding(FindingStatus.Activo, Now.AddDays(-1)));
        Session("otra", Now.AddDays(-1), cost: 50m);

        MetricsDashboard solo = Build("app");

        solo.ActiveTotal.Should().Be(1);
        solo.CostInPeriod.Should().BeNull("la única sesión con coste es de la otra app");
        solo.Coverage.Should().OnlyContain(c => c.Slug == "app");

        // Y el selector sigue ofreciendo TODAS: filtrar no puede esconder el camino de vuelta.
        solo.AppOptions.Should().HaveCount(3);
        solo.AppOptions[0].IsAll.Should().BeTrue();
    }

    [Theory]
    [InlineData(MetricsRange.Weeks4, MetricsGranularity.Diaria, 28)]
    [InlineData(MetricsRange.Weeks8, MetricsGranularity.Semanal, 8)]
    [InlineData(MetricsRange.Weeks26, MetricsGranularity.Semanal, 26)]
    public void Cada_rango_trae_su_grano_y_su_numero_de_cubos(
        MetricsRange range, MetricsGranularity grain, int buckets)
    {
        MetricsDashboard d = Build(range: range);

        d.Granularity.Should().Be(grain);
        d.Flow.Should().HaveCount(buckets);
        d.Cost.Should().HaveCount(buckets);
    }

    [Fact]
    public void Todo_empieza_en_el_dato_mas_antiguo_y_pasa_a_meses_cuando_pasa_de_un_ano()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-800)));

        MetricsDashboard d = Build(range: MetricsRange.All);

        d.From.Should().BeCloseTo(Now.AddDays(-800), TimeSpan.FromDays(1));
        d.Granularity.Should().Be(MetricsGranularity.Mensual, "un eje con 115 semanas no es más información");
        d.Flow.Should().HaveCountGreaterThan(24).And.HaveCountLessThan(30);
    }

    [Fact]
    public void Un_hub_sin_nada_lo_dice_en_vez_de_ensenar_ceros()
    {
        Build().IsEmpty.Should().BeTrue();

        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1)));
        Query().Invalidate();

        Build().IsEmpty.Should().BeFalse();
    }

    // =============================================================== §4 · la caché

    /// <summary>
    /// El agregado se cachea EN MEMORIA. No se comprueba que sea rápido —eso no es una aserción—
    /// sino lo que de verdad importa: que la caché existe (dos <c>Build</c> seguidos no releen el
    /// disco) y que se puede tirar, que es lo que hace el evento de sync.
    /// </summary>
    [Fact]
    public void El_agregado_se_cachea_y_se_tira_a_mano_o_con_el_evento_de_sync()
    {
        MetricsQuery query = Query();
        query.Build(MetricsFilter.Default).ActiveTotal.Should().Be(0);

        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1)));

        query.Build(MetricsFilter.Default).ActiveTotal
            .Should().Be(0, "la lectura estaba cacheada: el panel no vuelve a recorrer el hub por render");

        query.Invalidate();
        query.Build(MetricsFilter.Default).ActiveTotal.Should().Be(1);
    }

    /// <summary>
    /// La norma del §4: en el hub solo datos primarios. Ningún agregado del panel toca el disco.
    /// </summary>
    [Fact]
    public void Agregar_no_escribe_ni_un_fichero_en_el_hub()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1)));
        Inventory("app", audited: 1, pending: 1, large: 0);
        Session("app", Now.AddDays(-1), cost: 4m);

        string[] antes = Snapshot();
        foreach (MetricsRange range in Enum.GetValues<MetricsRange>())
        {
            Build(range: range);
            Build("app", range);
        }

        Snapshot().Should().BeEquivalentTo(antes, "el panel calcula; no deja nada derivado en el hub");
    }

    private string[] Snapshot()
        => Directory.Exists(_paths.Hub)
            ? Directory.GetFiles(_paths.Hub, "*", SearchOption.AllDirectories)
                .Select(f => $"{Path.GetRelativePath(_paths.Hub, f)}|{new FileInfo(f).Length}")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // El árbol temporal puede quedar tomado por el antivirus; no es parte de lo probado.
        }
    }
}
