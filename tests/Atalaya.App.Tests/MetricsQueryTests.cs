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
        TestRates.Seed(_hub);
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

        TestRates.CostAs(session, cost, inputTokens: 1000);
        _hub.Store.WriteSession(session);
        return session;
    }

    /// <summary>
    /// Una sesión de un tipo cualquiera —arreglo, verificación, cierre—. Las de arreglo y las de
    /// verificación no auditan unidades: esa es justamente la diferencia que el panel tenía que
    /// aprender a contar sin perderles el coste.
    /// </summary>
    private AuditSession Session(string slug, DateTimeOffset when, AuditMode mode, decimal? cost, int units = 0)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = mode,
            By = "alvaro",
            Machine = "PC",
            StartedUtc = when,
            EndedUtc = when.AddMinutes(20),
            CycleN = 1,
        };

        for (int i = 0; i < units; i++)
        {
            session.Units.Add(new UnitVerdictRecord($"src/U{i}.cs", "src", "auditada", null));
        }

        TestRates.CostAs(session, cost, inputTokens: 1000);
        _hub.Store.WriteSession(session);
        return session;
    }

    /// <summary>
    /// El hub que reproduce lo que el usuario tenía delante: una auditoría que costó, dos
    /// arreglos y una verificación que también costaron, y hallazgos resueltos HOY.
    /// </summary>
    private void FixtureDeLosTresTipos()
    {
        Session("app", Now.AddDays(-2), cost: 105m, units: 1);
        Session("app", Now, AuditMode.Fix, cost: 22.5m);
        Session("app", Now, AuditMode.Fix, cost: 67.5m);
        Session("app", Now, AuditMode.Verify, cost: 12m);
        Session("app", Now.AddDays(-1), AuditMode.Cierre, cost: null);
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

    /// <summary>
    /// F35 §2.6 — <b>en qué se le fue el gasto a cada aplicación, por acción</b>. Sustituye al
    /// reparto por fase de F18: la misma pregunta, contestada por aplicación y con porcentajes.
    /// <para>
    /// <b>Y los tramos suman el coste del periodo de esa aplicación</b>, que es la misma cifra que
    /// la tarjeta de coste con esa aplicación en el filtro. No es una coincidencia: es la misma
    /// función (D-591, D-597).
    /// </para>
    /// </summary>
    [Fact]
    public void El_coste_por_accion_reparte_y_suma_exactamente_el_coste_del_periodo()
    {
        FixtureDeLosTresTipos();

        MetricsDashboard d = Build();
        ActionCostDonut donut = d.ByAction.Single(x => x.Slug == "app");

        donut.Slices.Select(s => s.Action).Should().Equal(
            AuditAction.Auditoria, AuditAction.Verificacion, AuditAction.Arreglo);
        donut.Slices.Single(s => s.Action == AuditAction.Auditoria).Usd.Should().Be(105m);
        donut.Slices.Single(s => s.Action == AuditAction.Verificacion).Usd.Should().Be(12m);
        ActionSlice arreglo = donut.Slices.Single(s => s.Action == AuditAction.Arreglo);
        arreglo.Usd.Should().Be(90m);
        arreglo.Sessions.Should().Be(2, "los dos arreglos");

        // El cuadre: los tramos son el coste del periodo, ni un credit de más ni de menos.
        donut.Total.Should().Be(d.CostInPeriod!.Value);
    }

    /// <summary>
    /// El tramo de <b>gestión</b> existe aunque casi siempre salga a cero: cierre y reset no
    /// llaman a ningún modelo hoy. Está para que la partición sea COMPLETA — el día que una de
    /// esas sesiones gaste algo, el rosco lo enseñará en vez de perderlo, y el total seguirá
    /// cuadrando con la tarjeta.
    /// </summary>
    [Fact]
    public void Ninguna_sesion_se_cae_del_reparto_por_accion()
    {
        foreach (AuditMode mode in Enum.GetValues<AuditMode>())
        {
            AuditActions.All.Should().Contain(AuditActions.Of(mode), $"{mode} tiene que caer en una acción");
        }

        Session("app", Now.AddDays(-2), cost: 105m, units: 1);
        Session("app", Now.AddDays(-1), AuditMode.Cierre, cost: 30m);

        MetricsDashboard d = Build();
        ActionCostDonut donut = d.ByAction.Single(x => x.Slug == "app");

        donut.Slices.Single(s => s.Action == AuditAction.Gestion).Usd.Should()
            .Be(30m, "un cierre que costara algo no puede desaparecer del reparto");
        donut.Total.Should().Be(d.CostInPeriod!.Value);
    }

    /// <summary>
    /// Una sesión de una casa que <b>no factura</b> no aporta credits a ningún tramo — no los
    /// tiene—, y el rosco sigue cuadrando con la tarjeta. Por qué el gasto no cubre toda la
    /// actividad lo dice el aviso de encima de las cifras, no un tramo inventado.
    /// </summary>
    [Fact]
    public void Una_sesion_que_no_factura_no_inventa_un_tramo()
    {
        Session("app", Now.AddDays(-2), cost: 105m, units: 1);
        AuditSession s = Session("app", Now, AuditMode.Verify, cost: 4m);
        s.Provider = ClaudeCode.ClaudeCodeProvider.Id;
        _hub.Store.WriteSession(s);

        MetricsDashboard d = Build();
        ActionCostDonut donut = d.ByAction.Single(x => x.Slug == "app");

        donut.Slices.Should().NotContain(x => x.Action == AuditAction.Verificacion);
        donut.Total.Should().Be(d.CostInPeriod!.Value).And.Be(105m);
        d.UntariffedSessions.Should().Be(1, "y eso es lo que lo explica");
    }

    /// <summary>
    /// Una app sin gasto en el periodo conserva su rosco, vacío: una fila que solo trae a los que
    /// gastaron hace creer que las demás no están (la misma regla que el rosco de severidad).
    /// </summary>
    [Fact]
    public void Una_app_sin_gasto_conserva_su_rosco_vacio()
    {
        App("otra", "Otra");
        Session("app", Now.AddDays(-2), cost: 105m, units: 1);

        MetricsDashboard d = Build();

        d.ByAction.Should().HaveCount(2);
        d.ByAction.Single(x => x.Slug == "otra").HasData.Should().BeFalse();
        d.ByAction.Single(x => x.Slug == "otra").Slices.Should().BeEmpty();
    }

    // =============================================================== F35 §2.7 — antigüedad

    /// <summary>
    /// <b>Cada activo cae en exactamente un cubo, y la suma es la deuda activa</b> (F35 §2.7). Es
    /// el cuadre que impide que la gráfica y la tarjeta 2 cuenten deudas distintas.
    /// </summary>
    [Fact]
    public void Cada_activo_cae_en_un_cubo_y_la_suma_es_la_deuda_activa()
    {
        // Uno en cada cubo: 3 días, 15, 60 y 200. Y un resuelto, que no es deuda.
        foreach (int days in new[] { 3, 15, 60, 200 })
        {
            _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-days)));
        }

        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-90), Now.AddDays(-1)));

        MetricsDashboard d = Build();
        DebtAgeRow row = d.ByAge.Single(r => r.Slug == "app");

        row.Counts.Should().Equal(1, 1, 1, 1);
        row.Total.Should().Be(d.ActiveTotal, "la suma de las barras ES la deuda activa");
        d.ByAge.Sum(r => r.Total).Should().Be(d.ActiveTotal);
    }

    /// <summary>
    /// Los cubos no se solapan ni dejan hueco: son [0,7) [7,28) [28,84) [84,∞). Los bordes son
    /// donde se equivoca una implementación, así que se prueban los bordes.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(6.9, 0)]
    [InlineData(7, 1)]
    [InlineData(27.9, 1)]
    [InlineData(28, 2)]
    [InlineData(83.9, 2)]
    [InlineData(84, 3)]
    [InlineData(5000, 3)]
    public void Cada_edad_cae_en_exactamente_un_cubo(double days, int bucket)
        => AgeBucket.IndexOf(days).Should().Be(bucket);

    /// <summary>
    /// <b>El periodo NO recorta la antigüedad</b> (D-320). Recortarla vaciaría por definición los
    /// cubos de más de cuatro semanas cada vez que alguien eligiera «4 semanas», que es justo la
    /// pregunta que la gráfica existe para contestar.
    /// </summary>
    [Fact]
    public void La_antiguedad_no_la_recorta_el_periodo()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-200)));

        Build(range: MetricsRange.Weeks4).ByAge.Single().Counts.Should().Equal(0, 0, 0, 1);
        Build(range: MetricsRange.Weeks26).ByAge.Single().Counts.Should().Equal(0, 0, 0, 1);
    }

    // =============================================================== F35 §2.8 — top 5 reglas

    /// <summary>
    /// <b>Las cinco que más produjeron, en orden, y solo del periodo</b> (F35 §2.8).
    /// </summary>
    [Fact]
    public void El_top_de_reglas_ordena_cuenta_solo_el_periodo_y_se_queda_en_cinco()
    {
        // Seis reglas dentro del periodo, con recuentos distintos, y una vieja que queda fuera.
        var counts = new (string Rule, int N)[]
        {
            ("errores.null.desreferencia", 6),
            ("errores.calculo.negocio", 5),
            ("mejoras.estilo.nomenclatura", 4),
            ("optimizacion.consulta.n-mas-1", 3),
            ("errores.async.mal-usado", 2),
            ("mejoras.mantenibilidad.complejidad", 1),
        };

        foreach ((string rule, int n) in counts)
        {
            for (int i = 0; i < n; i++)
            {
                Finding f = Finding(FindingStatus.Activo, Now.AddDays(-3));
                f.RuleId = rule;
                _hub.Store.WriteFinding("app", f);
            }
        }

        Finding vieja = Finding(FindingStatus.Activo, Now.AddDays(-200));
        vieja.RuleId = "errores.null.desreferencia";
        _hub.Store.WriteFinding("app", vieja);

        IReadOnlyList<RuleCount> top = Build(range: MetricsRange.Weeks4).Rules;

        top.Should().HaveCount(MetricsQuery.TopRuleCount);
        top.Select(r => r.Count).Should().Equal(6, 5, 4, 3, 2);
        top[0].RuleId.Should().Be("errores.null.desreferencia");
        top[0].Count.Should().Be(6, "el hallazgo de hace 200 días es de otro periodo y no suma");

        // Se lee el TÍTULO del catálogo, no el id.
        top[0].Name.Should().Be("Posible desreferencia nula");
        top.Should().NotContain(r => r.RuleId == "mejoras.mantenibilidad.complejidad", "la sexta no cabe");
    }

    /// <summary>
    /// <b>El empate se rompe por el nombre, siempre igual.</b> Sin desempate, dos reglas con el
    /// mismo recuento se intercambiarían según en qué orden devolviera el disco los ficheros, y la
    /// lista bailaría entre dos cargas sin que nada hubiera cambiado.
    /// </summary>
    [Fact]
    public void Un_empate_se_rompe_por_el_nombre_y_no_baila()
    {
        foreach (string rule in new[]
                 {
                     "mejoras.estilo.nomenclatura",           // «Nomenclatura/estilo»
                     "mejoras.mantenibilidad.unidad-grande",  // «Unidad demasiado grande»
                     "errores.async.mal-usado",               // «async/await mal usado»
                 })
        {
            for (int i = 0; i < 3; i++)
            {
                Finding f = Finding(FindingStatus.Activo, Now.AddDays(-3));
                f.RuleId = rule;
                _hub.Store.WriteFinding("app", f);
            }
        }

        IReadOnlyList<RuleCount> top = Build().Rules;

        top.Select(r => r.Name).Should().Equal(
            "Nomenclatura/estilo", "Unidad demasiado grande", "async/await mal usado");
        top.Select(r => r.Name).Should().BeEquivalentTo(Build().Rules.Select(r => r.Name),
            "dos agregaciones del mismo hub dan el mismo orden");
    }

    /// <summary>
    /// Una regla que el catálogo no conoce —un <c>criterio.&lt;área&gt;</c>— se enseña con su id.
    /// Esconder la fila sería peor: esos hallazgos existen y producen trabajo igual.
    /// </summary>
    [Fact]
    public void Una_regla_fuera_del_catalogo_se_ensena_con_su_id()
    {
        Finding f = Finding(FindingStatus.Activo, Now.AddDays(-3));
        f.RuleId = "criterio.dominio";
        _hub.Store.WriteFinding("app", f);

        Build().Rules.Single().Name.Should().Be("criterio.dominio");
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

        // Y el TILE dice lo mismo que la gráfica. Contaba el estado de hoy —una sola resolución,
        // porque el campo `resolved` solo guarda la última—, así que la misma pregunta tenía dos
        // respuestas en la misma pantalla. Cuenta eventos, como la gráfica y como el burndown.
        d.ResolvedInPeriod.Should().Be(2, "el tile cuenta los mismos eventos que la gráfica");
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
        corto.Granularity.Should().Be(MetricsGranularity.Diaria, "grano diario, el mismo que la de coste");
        corto.Resolutions.Should().HaveCount(
            7, "la única actividad del periodo es de hace dos días: el eje empieza ahí y el mínimo son 7 días");

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

        // Una sesión en el primer tramo: sin ella el eje empezaría en el cubo del primer hallazgo
        // (F35 §1.2) y este test no podría comprobar que hace ocho semanas no había ninguno.
        Session("app", Now.AddDays(-55), cost: 1m);

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
    [InlineData(MetricsRange.Week1, MetricsGranularity.Diaria, 7, 6)]
    [InlineData(MetricsRange.Weeks4, MetricsGranularity.Diaria, 28, 27)]
    [InlineData(MetricsRange.Weeks8, MetricsGranularity.Semanal, 8, 55)]
    [InlineData(MetricsRange.Weeks26, MetricsGranularity.Semanal, 26, 181)]
    public void Cada_rango_trae_su_grano_y_su_numero_de_cubos(
        MetricsRange range, MetricsGranularity grain, int buckets, int daysAgo)
    {
        // Con actividad en el PRIMER tramo del periodo el eje no recorta nada (F35 §1.2), que es
        // la condición en la que se puede comprobar el grano y el número de cubos de cada rango.
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-daysAgo)));

        MetricsDashboard d = Build(range: range);

        d.Granularity.Should().Be(grain);
        d.Flow.Should().HaveCount(buckets);
        d.Cost.Should().HaveCount(buckets);
        d.AxisIsTrimmed.Should().BeFalse("hubo actividad en el primer tramo");
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
    /// El agregado se cachea EN MEMORIA, pero la caché NO puede sobrevivir a un cambio en el hub.
    /// <para>
    /// Antes solo se tiraba con el evento de sync, y ese evento lo levanta un <b>pull</b> del
    /// remoto: todo lo que escribe esta máquina —una auditoría, un arreglo, una verificación— no
    /// pasaba por ahí, así que el panel seguía enseñando la foto anterior hasta reiniciar la
    /// aplicación. Justo la sesión que el usuario acababa de terminar era la que faltaba.
    /// </para>
    /// </summary>
    [Fact]
    public void La_cache_se_rinde_ante_un_cambio_en_el_hub_aunque_no_haya_habido_sync()
    {
        MetricsQuery query = Query();
        query.Build(MetricsFilter.Default).ActiveTotal.Should().Be(0);

        // Nadie llama a Invalidate y nadie sincroniza: es lo que pasa cuando ESTA máquina escribe.
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1)));

        query.Build(MetricsFilter.Default).ActiveTotal
            .Should().Be(1, "lo escrito en el hub aparece al volver a la vista, sin reiniciar");
    }

    /// <summary>
    /// Y con el hub quieto la caché sigue ahí: no se relee un solo fichero por render. Se mide por
    /// lo único observable sin cronómetro —que el objeto devuelto es EL MISMO—, porque «tardó
    /// menos» no es una aserción.
    /// </summary>
    [Fact]
    public void Con_el_hub_quieto_no_se_vuelve_a_leer_el_disco()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-1)));
        MetricsQuery query = Query();

        MetricsDashboard first = query.Build(MetricsFilter.Default);
        MetricsDashboard second = query.Build(MetricsFilter.Default);

        first.Sessions.Should().BeEquivalentTo(second.Sessions);
        second.ActiveTotal.Should().Be(1);

        query.Invalidate();
        query.Build(MetricsFilter.Default).ActiveTotal.Should().Be(1, "tirar la caché no cambia el dato");
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

    // ====================================================== El cuadre de la vista Métricas
    //
    // Todo lo de aquí sale del parte del 28/08/2026: el gasto de los arreglos de ese día no se
    // veía en ninguna parte del tile de coste, las resoluciones de ese día se dibujaban seis días
    // antes, y el día de hoy no aparecía en el eje de ninguna gráfica.

    /// <summary>
    /// TODA sesión con coste cuenta en el gasto: auditar, arreglar y verificar. Y el tile y la
    /// gráfica dan el MISMO número porque salen de la misma función, no de dos sumas parecidas.
    /// </summary>
    [Fact]
    public void El_coste_suma_auditorias_arreglos_y_verificaciones_y_el_tile_cuadra_con_la_grafica()
    {
        FixtureDeLosTresTipos();

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.CostInPeriod.Should().Be(207m, "105 de auditar + 22,5 + 67,5 de arreglar + 12 de verificar");
        d.Cost.Sum(p => p.Of("app")).Should().Be(207m, "la gráfica no puede discrepar del tile");
    }

    /// <summary>
    /// El ratio «por unidad auditada» divide lo que costó AUDITAR, no el gasto entero. Un arreglo
    /// no audita ninguna unidad: metiéndolo en el numerador, el ratio subía sin que hubiera
    /// cambiado nada de lo auditado.
    /// </summary>
    [Fact]
    public void El_coste_por_unidad_auditada_no_se_infla_con_arreglos_ni_verificaciones()
    {
        FixtureDeLosTresTipos();

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.UnitsAuditedInPeriod.Should().Be(1);
        d.CostPerAuditedUnit.Should().Be(105m, "auditar esa unidad costó 105, no 207");
    }

    /// <summary>
    /// El caso exacto del parte: un hallazgo resuelto HOY se dibuja en el cubo de HOY, y ese cubo
    /// es el último del eje.
    /// </summary>
    [Fact]
    public void Una_resolucion_de_hoy_cae_en_el_cubo_de_hoy()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-10), Now));

        foreach (MetricsRange range in new[] { MetricsRange.Weeks4, MetricsRange.Weeks8, MetricsRange.Weeks26 })
        {
            MetricsDashboard d = Build(range: range);

            d.ResolvedInPeriod.Should().Be(1);
            d.Resolutions[^1].Of("app").Should().Be(1m, $"la resolución de hoy va en el último cubo ({range})");
            d.Resolutions.SkipLast(1).Should().OnlyContain(p => p.Of("app") == 0m);
        }
    }

    /// <summary>
    /// El eje llega SIEMPRE a hoy, en las tres gráficas de eje temporal y con el hub vacío. Un eje
    /// que termina en el pasado afirma que desde entonces no ha pasado nada.
    /// </summary>
    [Fact]
    public void El_eje_de_todas_las_graficas_llega_hasta_hoy()
    {
        FixtureDeLosTresTipos();
        string hoy = Now.ToLocalTime().ToString("d MMM");

        foreach (MetricsRange range in new[] { MetricsRange.Weeks4, MetricsRange.Weeks8, MetricsRange.All })
        {
            MetricsDashboard d = Build(range: range);

            d.Cost[^1].Label.Should().Be(hoy, $"coste ({range})");
            d.Resolutions[^1].Label.Should().Be(hoy, $"resoluciones ({range})");
            d.Flow[^1].Label.Should().Be(hoy, $"flujo ({range})");
            d.To.Should().BeAfter(Now, "el extremo derecho es la medianoche de mañana");
        }
    }

    /// <summary>
    /// Un cubo semanal se rotula por su último día —hoy— y su tooltip dice el tramo entero. Con la
    /// etiqueta de inicio, lo hecho hoy se leía como actividad de hace seis días: es lo que pasó
    /// el 28/08/2026, cuando el eje terminaba en «22 ago».
    /// </summary>
    [Fact]
    public void El_cubo_semanal_se_rotula_por_su_ultimo_dia_y_el_tooltip_dice_el_tramo()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-10), Now));

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.Granularity.Should().Be(MetricsGranularity.Semanal);
        d.Resolutions[^1].Label.Should().Be(Now.ToLocalTime().ToString("d MMM"));
        d.Resolutions[^1].Range.Should().Be(
            MetricsQuery.RangeText(Now.ToLocalTime().DateTime.Date.AddDays(-6), Now.ToLocalTime().DateTime.Date));
    }

    /// <summary>
    /// Un cubo diario cubre el DÍA LOCAL, no el día UTC. Cortando por medianoche UTC, en UTC+2 lo
    /// hecho entre las 00:00 y las 02:00 se dibujaba en el día anterior.
    /// </summary>
    [Fact]
    public void Un_cubo_diario_cubre_el_dia_local_y_no_el_dia_utc()
    {
        // Las 00:30 de HOY en la hora del usuario, sea cual sea su huso.
        DateTime hoyLocal = Now.ToLocalTime().DateTime.Date;
        DateTimeOffset madrugada = MetricsQuery.Instant(hoyLocal).AddMinutes(30);

        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-10), madrugada));

        MetricsDashboard d = Build(range: MetricsRange.Weeks4);

        d.Granularity.Should().Be(MetricsGranularity.Diaria);
        d.Resolutions[^1].Label.Should().Be(hoyLocal.ToString("d MMM"));
        d.Resolutions[^1].Of("app").Should().Be(1m, "las 00:30 de hoy son hoy");
    }

    /// <summary>
    /// El burndown y el rosco de severidad no pueden enseñar dos deudas distintas de la misma app.
    /// Un hallazgo SILENCIADO no es deuda viva —de él se decidió que no se arregla—, y antes
    /// engordaba el burndown porque nunca lleva sello <c>resolved</c>.
    /// </summary>
    [Fact]
    public void Un_hallazgo_silenciado_no_cuenta_como_deuda_viva_en_el_burndown()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-30)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Silenciado, Now.AddDays(-30), Now.AddDays(-20)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.ActiveTotal.Should().Be(1);
        d.Flow[^1].ActiveAtEnd.Should().Be(1, "el burndown cuenta la misma deuda que el rosco");
    }

    /// <summary>
    /// Y el burndown reconstruye el pasado de verdad: mientras estuvo cerrado no era deuda, y al
    /// reabrirse volvió a serlo. El sello <c>resolved</c> por sí solo no puede contar esto —se
    /// pone a null al reabrir—, así que declaraba el hallazgo vivo también cuando estaba cerrado.
    /// </summary>
    [Fact]
    public void Un_hallazgo_reabierto_no_es_deuda_viva_mientras_estuvo_cerrado()
    {
        Finding f = Finding(FindingStatus.Resuelto, Now.AddDays(-40), Now.AddDays(-30));
        f.Reopen(Now.AddDays(-5), "alvaro", "volvió a aparecer");
        _hub.Store.WriteFinding("app", f);

        MetricsQuery.AliveAt(f, Now.AddDays(-35)).Should().BeTrue("todavía no se había resuelto");
        MetricsQuery.AliveAt(f, Now.AddDays(-20)).Should().BeFalse("estuvo cerrado ese tramo");
        MetricsQuery.AliveAt(f, Now).Should().BeTrue("se reabrió");
    }

    /// <summary>
    /// El registro de operaciones incluye las sesiones que NO auditan —arreglos y verificaciones—
    /// con su coste y su tipo. Sin el tipo, un arreglo y una auditoría vacía son la misma fila.
    /// </summary>
    [Fact]
    public void El_registro_de_sesiones_incluye_arreglos_y_verificaciones_con_su_tipo_y_su_coste()
    {
        FixtureDeLosTresTipos();

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.Sessions.Should().HaveCount(5);
        d.Sessions.Should().Contain(r => r.Mode == AuditMode.Fix && r.Cost == 22.5m);
        d.Sessions.Should().Contain(r => r.Mode == AuditMode.Fix && r.Cost == 67.5m);
        d.Sessions.Should().Contain(r => r.Mode == AuditMode.Verify && r.Cost == 12m);
        d.Sessions.Should().Contain(r => r.Mode == AuditMode.Lotes && r.Cost == 105m);
        d.Sessions.Should().Contain(r => r.Mode == AuditMode.Cierre && r.Cost == null);
    }

    // =============================================================== F35 §1 — las cuatro cifras

    /// <summary>
    /// El hub del cuadre de F35, con TODO calculado a mano en el comentario. Es la misma forma de
    /// trabajar de H9.2: primero se escribe qué tiene que salir, y después se comprueba.
    /// <para>
    /// Reloj: 21/08/2026 12:00 UTC. Periodo (4 semanas): 25/07 → 22/08. Anterior: 27/06 → 25/07.
    /// </para>
    /// </summary>
    private void FixtureDeLasCuatroCifras()
    {
        // --- Sesiones ---
        // S1 (12/07, periodo ANTERIOR): 100 credits, audita 2 unidades.
        // S2 (18/08, en periodo): 200 credits, audita 2 unidades.
        // S3 (20/08, en periodo): 100 credits, arreglo — no audita ninguna.
        AuditSession s1 = Session("app", Now.AddDays(-40), cost: 100m, units: 2);
        AuditSession s2 = Session("app", Now.AddDays(-3), cost: 200m, units: 2);
        Session("app", Now.AddDays(-1), AuditMode.Fix, cost: 100m);

        // --- Inventario del ciclo 3: 4 auditadas, 6 pendientes, 2 grandes ---
        // Dos las auditó S1 (antes del periodo) y dos S2 (dentro), así que la cobertura al empezar
        // el periodo era 2/10 = 20 % y hoy es 4/10 = 40 %.
        var inv = new InventoryCycle { CycleN = 3 };
        for (int i = 0; i < 2; i++)
        {
            inv.Units.Add(new InventoryUnit
            {
                Path = $"vieja{i}.cs", Module = "m", State = UnitState.Auditada, AuditedInSession = s1.Id,
            });
        }

        for (int i = 0; i < 2; i++)
        {
            inv.Units.Add(new InventoryUnit
            {
                Path = $"nueva{i}.cs", Module = "m", State = UnitState.Auditada, AuditedInSession = s2.Id,
            });
        }

        for (int i = 0; i < 6; i++)
        {
            inv.Units.Add(new InventoryUnit { Path = $"p{i}.cs", Module = "m", State = UnitState.Pendiente });
        }

        for (int i = 0; i < 2; i++)
        {
            inv.Units.Add(new InventoryUnit { Path = $"g{i}.cs", Module = "m", State = UnitState.Grande });
        }

        _hub.Store.WriteInventory("app", inv);
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u/app", CurrentCycle = 3 });

        // --- Hallazgos ---
        // F1, F2: activos desde hace 100 días → vivos al empezar el periodo y vivos hoy.
        // F3: detectado hace 100 días, resuelto hace 2 → vivo al empezar, no hoy.
        // F4: detectado hace 100 días, resuelto hace 40 → resolución del periodo ANTERIOR.
        // F5, F6: detectados dentro del periodo y activos → nuevos.
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-100)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-100)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-100), Now.AddDays(-2)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-100), Now.AddDays(-40)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-10)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-5)));
    }

    /// <summary>
    /// <b>Las cuatro cifras, contra lo calculado a mano</b> (F35 §1.3). Cada una con su tendencia
    /// contra el periodo anterior y con el signo que le toca: subir la cobertura es bueno, subir la
    /// deuda es malo, y el coste no lleva juicio.
    /// </summary>
    [Fact]
    public void Las_cuatro_cifras_y_sus_tendencias_salen_de_los_ficheros()
    {
        FixtureDeLasCuatroCifras();

        MetricsDashboard d = Build(range: MetricsRange.Weeks4);

        d.HasPreviousPeriod.Should().BeTrue("hubo una sesión y hallazgos antes del periodo");

        // 1 · Cobertura: 4 de 10 auditables hoy, 2 de 10 al empezar. Las 2 grandes quedan fuera.
        d.CycleAudited.Should().Be(4);
        d.CyclePending.Should().Be(6);
        d.CycleLarge.Should().Be(2);
        d.CoveragePct.Should().BeApproximately(0.4, 0.0001);
        d.CoveragePctBefore.Should().BeApproximately(0.2, 0.0001);
        d.CoverageTrend.Percent.Should().BeApproximately(100.0, 0.0001, "del 20 % al 40 %");
        d.CoverageTrend.IsGood.Should().BeTrue("subir la cobertura es bueno");
        d.ScopeCycle.Should().Be(3, "una sola aplicación en el filtro");

        // 2 · Deuda activa: 4 vivos hoy, 3 al empezar el periodo; +2 nuevos y −1 resuelto.
        d.ActiveTotal.Should().Be(4);
        d.ActiveAtPeriodStart.Should().Be(3);
        d.NewInPeriod.Should().Be(2);
        d.ResolvedInPeriod.Should().Be(1);
        d.DebtTrend.Percent.Should().BeApproximately(100.0 / 3.0, 0.0001, "de 3 a 4");
        d.DebtTrend.IsBad.Should().BeTrue("subir la deuda es malo");

        // 3 · Coste: 300 en el periodo (200 + 100) contra 100 del anterior; 2 sesiones, 2 unidades
        // auditadas, y el «por unidad» divide SOLO lo que costó auditar (D-592): 200 / 2 = 100.
        d.CostInPeriod.Should().Be(300m);
        d.CostPreviousPeriod.Should().Be(100m);
        d.SessionsInPeriod.Should().Be(2);
        d.UnitsAuditedInPeriod.Should().Be(2);
        d.CostPerAuditedUnit.Should().Be(100m);
        d.CostTrend.Percent.Should().BeApproximately(200.0, 0.0001);
        d.CostTrend.IsGood.Should().BeFalse("el coste no lleva juicio de color");
        d.CostTrend.IsBad.Should().BeFalse("ni bueno ni malo: gastar más no es malo por sí");

        // 4 · Coste por hallazgo resuelto: 300 / 1 hoy, 100 / 1 antes.
        d.ResolvedPreviousPeriod.Should().Be(1);
        d.CostPerResolution.Should().Be(300m);
        d.CostPerResolutionBefore.Should().Be(100m);
        d.CostPerResolutionTrend.Percent.Should().BeApproximately(200.0, 0.0001);
        d.CostPerResolutionTrend.IsBad.Should().BeTrue("que cada arreglo cueste más es peor");
    }

    /// <summary>
    /// <b>La cobertura se agrega en UNIDADES, y con dos apps no se enseña ningún ciclo</b>
    /// (F35, definiciones). Dos aplicaciones en ciclos distintos suman sus unidades; el número de
    /// ciclo desaparece porque un ciclo sobre una suma de ciclos distintos no significa nada.
    /// </summary>
    [Fact]
    public void La_cobertura_de_dos_apps_suma_unidades_y_no_ensena_ciclo()
    {
        App("otra", "Otra", cycle: 7);
        Inventory("app", audited: 4, pending: 6, large: 2);
        Inventory("otra", audited: 1, pending: 9, large: 0, cycle: 7);

        MetricsDashboard todas = Build();

        todas.ScopeApps.Should().Be(2);
        todas.ScopeCycle.Should().BeNull("nunca un ciclo sobre una suma de ciclos distintos");
        todas.CycleAudited.Should().Be(5, "4 + 1, en unidades");
        todas.CyclePending.Should().Be(15, "6 + 9");
        todas.CoveragePct.Should().BeApproximately(0.25, 0.0001, "5 de 20 auditables, no la media de 40 % y 10 %");

        // Y con una sola en el filtro vuelve a haber ciclo, el suyo.
        Build("otra").ScopeCycle.Should().Be(7);
        Build("app").ScopeCycle.Should().Be(1);
    }

    /// <summary>
    /// <b>Sin periodo anterior no hay tendencia</b> (D-318). No es que no se haya movido: es que no
    /// hay con qué comparar, y las cuatro flechas se callan a la vez.
    /// </summary>
    [Fact]
    public void Sin_actividad_antes_del_periodo_no_hay_ninguna_tendencia()
    {
        Inventory("app", audited: 1, pending: 1, large: 0);
        Session("app", Now.AddDays(-3), cost: 50m, units: 1);
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-3)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks4);

        d.HasPreviousPeriod.Should().BeFalse("no hay un solo sello anterior al periodo");
        d.CoverageTrend.HasValue.Should().BeFalse();
        d.DebtTrend.HasValue.Should().BeFalse();
        d.CostTrend.HasValue.Should().BeFalse();
        d.CostPerResolutionTrend.HasValue.Should().BeFalse();
    }

    /// <summary>
    /// Sin resueltos, el coste por hallazgo resuelto <b>no es cero ni infinito</b>: es una división
    /// que no se puede hacer, y el agregado devuelve null para que la tarjeta escriba «—» (D-318).
    /// </summary>
    [Fact]
    public void Sin_resueltos_no_hay_coste_por_resuelto()
    {
        Session("app", Now.AddDays(-3), cost: 50m, units: 1);

        MetricsDashboard d = Build(range: MetricsRange.Weeks4);

        d.ResolvedInPeriod.Should().Be(0);
        d.CostInPeriod.Should().Be(50m, "sí hubo gasto");
        d.CostPerResolution.Should().BeNull("no se reparte un gasto entre cero");
    }

    /// <summary>
    /// Una tendencia contra CERO tampoco es un porcentaje: es una división por cero. Hay periodo
    /// anterior —pasaron cosas—, pero la cifra de entonces era cero y no hay nada por lo que
    /// dividir.
    /// </summary>
    [Fact]
    public void Una_tendencia_contra_cero_no_se_escribe()
    {
        // Actividad antes del periodo (así que HAY periodo anterior), pero sin coste ninguno.
        Session("app", Now.AddDays(-40), AuditMode.Verify, cost: null);
        Session("app", Now.AddDays(-3), cost: 50m, units: 1);

        MetricsDashboard d = Build(range: MetricsRange.Weeks4);

        d.HasPreviousPeriod.Should().BeTrue();
        d.CostPreviousPeriod.Should().Be(0m);
        d.CostTrend.HasValue.Should().BeFalse("no se divide por cero para decir «∞ %»");
    }

    // =============================================================== F35 §1.2 — el eje

    /// <summary>
    /// <b>El eje empieza en el primer cubo con actividad</b> y el último sigue conteniendo hoy
    /// (D-593). Las semanas de delante no son una línea plana de dato: son las semanas en las que
    /// no había nada que medir.
    /// </summary>
    [Fact]
    public void El_eje_empieza_en_el_primer_cubo_con_actividad_y_termina_en_hoy()
    {
        // Ocho semanas de periodo y la primera actividad hace 20 días: sobran cinco semanas.
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-20)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks8);

        d.AxisIsTrimmed.Should().BeTrue();
        d.Flow.Should().HaveCount(3, "el cubo del hallazgo y los dos que quedan hasta hoy");
        d.Flow[0].New.Should().Be(1, "el primer cubo dibujado es el que trae la actividad");

        // El último cubo contiene HOY: es el ancla de quien mira el panel (D-593).
        d.Cost[^1].From.Should().BeOnOrBefore(Now);
        MetricsQuery.Instant(Now.ToLocalTime().Date.AddDays(1)).Should().BeAfter(d.Cost[^1].From);

        // Y el periodo NO se ha movido: las cifras siguen siendo de las ocho semanas.
        (d.To - d.From).TotalDays.Should().Be(56);
    }

    /// <summary>
    /// <b>El eje nunca baja de 7 días ni de dos cubos.</b> Con una sola tarde de actividad, un eje
    /// que empezara ahí dibujaría un punto — y un punto no dice si algo sube o baja.
    /// </summary>
    [Fact]
    public void El_eje_nunca_mide_menos_de_una_semana_ni_ensena_un_solo_punto()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now));

        MetricsDashboard diario = Build(range: MetricsRange.Weeks4);
        diario.Granularity.Should().Be(MetricsGranularity.Diaria);
        diario.Flow.Should().HaveCount(MetricsQuery.MinAxisDays, "el mínimo son 7 días");

        MetricsDashboard semanal = Build(range: MetricsRange.Weeks8);
        semanal.Granularity.Should().Be(MetricsGranularity.Semanal);
        semanal.Flow.Should().HaveCount(2, "un cubo semanal ya mide 7 días, pero un punto no es una gráfica");
    }

    /// <summary>
    /// Sin ninguna actividad en el periodo el eje cae al mínimo por la cola. Las gráficas enseñan
    /// su estado vacío; lo que no puede quedar detrás es un eje de ocho semanas de nada.
    /// </summary>
    [Fact]
    public void Un_periodo_entero_sin_actividad_deja_el_eje_en_el_minimo()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-300)));

        MetricsDashboard d = Build(range: MetricsRange.Weeks4);

        d.Flow.Should().HaveCount(MetricsQuery.MinAxisDays);
        d.Flow.Sum(b => b.New).Should().Be(0);
    }

    /// <summary>
    /// El periodo por defecto son CUATRO semanas (F35 §1.1). Es lo que decide, además del eje, qué
    /// es «el periodo anterior» de las cuatro tendencias.
    /// </summary>
    [Fact]
    public void El_periodo_por_defecto_son_cuatro_semanas()
    {
        MetricsFilter.Default.Range.Should().Be(MetricsRange.Weeks4);
        MetricsFilter.Default.Slug.Should().BeNull("todas las aplicaciones");

        (Build(range: MetricsRange.Weeks4).To - Build(range: MetricsRange.Weeks4).From)
            .TotalDays.Should().Be(28);
    }

    /// <summary>
    /// <b>Una semana: siete días, siete cubos diarios, y el último contiene HOY</b> (D-593). La
    /// regla del eje se cumple entera con esos cubos —siete días y más de dos—, así que aquí no
    /// recorta nada, ni siquiera cuando la única actividad es de hoy mismo.
    /// </summary>
    [Fact]
    public void Una_semana_son_siete_cubos_diarios_y_el_ultimo_contiene_hoy()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now));

        MetricsDashboard d = Build(range: MetricsRange.Week1);

        (d.To - d.From).TotalDays.Should().Be(7);
        d.Granularity.Should().Be(MetricsGranularity.Diaria);
        d.Flow.Should().HaveCount(7, "siete cubos de un día");
        d.AxisIsTrimmed.Should().BeFalse("siete cubos diarios ya cumplen el mínimo: no hay qué recortar");

        // El último cubo CONTIENE hoy: empieza en la medianoche de hoy y termina en la de mañana.
        d.Cost[^1].From.Should().Be(MetricsQuery.Instant(Now.ToLocalTime().Date));
        d.To.Should().Be(MetricsQuery.Instant(Now.ToLocalTime().Date.AddDays(1)));
        d.Flow[^1].New.Should().Be(1, "el hallazgo de hoy cae en el último cubo");

        // Y el periodo anterior son los SIETE días de antes —del 8 al 15—, no otra ventana.
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-15)));
        Build(range: MetricsRange.Week1).ResolvedPreviousPeriod.Should()
            .Be(0, "hace quince días queda por delante de los siete anteriores");

        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-30), Now.AddDays(-10)));
        Build(range: MetricsRange.Week1).ResolvedPreviousPeriod.Should()
            .Be(1, "hace diez días cae dentro de los siete anteriores a los siete últimos");
    }
}
