using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F10 §1 — el agregador del mapa de calor.
/// <para>
/// El test central no es ninguna suma: es que <b>lo que nadie ha auditado no tenga densidad</b>.
/// Un agregador que devolviera 0 ahí pintaría de «limpio» un inventario entero sin tocar, y el
/// mapa —que se va a enseñar en una reunión— diría exactamente lo contrario de la verdad.
/// </para>
/// </summary>
public sealed class HeatmapQueryTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public HeatmapQueryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f10", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
    }

    // ============================================================ Utilidades

    private void App(string slug = "app", string name = "App", int cycle = 1)
        => _hub.Store.WriteApp(new AppConfig { Slug = slug, Name = name, RepoUrl = $"u/{slug}", CurrentCycle = cycle });

    private void Inventory(int cycle, params InventoryUnit[] units)
        => _hub.Store.WriteInventory("app", new InventoryCycle { CycleN = cycle, Units = units.ToList() });

    private static InventoryUnit Unit(
        string path,
        string module,
        int loc,
        UnitState state = UnitState.Pendiente,
        Ulid? session = null,
        string? hash = null)
        => new()
        {
            Path = path,
            Module = module,
            Loc = loc,
            State = state,
            AuditedInSession = session,
            ContentHash = hash ?? $"sha256:{path.GetHashCode():x8}",
        };

    private Finding Finding(
        string path,
        Severity severity = Severity.Media,
        FindingStatus status = FindingStatus.Activo,
        params string[] extraPaths)
    {
        var locations = new List<Location> { new(path, 1) };
        locations.AddRange(extraPaths.Select(p => new Location(p, 1)));

        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "commit", "tester");
        var finding = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "R-1",
            Title = $"Algo en {path}",
            Severity = severity,
            Status = status,
            Locations = locations,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            Resolved = status == FindingStatus.Resuelto
                ? new ResolutionStamp(
                    DateTimeOffset.UtcNow, ResolutionVia.Manual, AuditMode.Lotes, "commit", "tester", null)
                : null,
        };

        _hub.Store.WriteFinding("app", finding);
        return finding;
    }

    private HeatmapView Build(string slug = "app") => new HeatmapQuery(_hub).Build(slug);

    private static HeatUnit UnitOf(HeatmapView view, string path)
        => view.Units.Single(u => u.Path == path);

    // ============================================ Los pesos y la densidad

    /// <summary>Una unidad auditada suma los pesos de sus activos y los divide entre sus KLOC.</summary>
    [Fact]
    public void La_deuda_pesa_por_severidad_y_la_densidad_es_por_KLOC()
    {
        App();
        Inventory(1, Unit("a.cs", "M", 500, UnitState.Auditada));
        Finding("a.cs", Severity.Critica);
        Finding("a.cs", Severity.Alta);
        Finding("a.cs", Severity.Baja);

        HeatUnit unit = UnitOf(Build(), "a.cs");

        unit.KnownDebt.Should().Be(10 + 5 + 1);
        unit.Findings.Should().Be(new SeverityChips(1, 1, 0, 1));
        unit.Density.Should().Be(32);
    }

    /// <summary>
    /// La razón de normalizar. La clase de 5 000 líneas tiene MÁS deuda absoluta y MENOS
    /// concentrada: sin dividir por tamaño, el mapa mandaría a todo el mundo a la clase grande
    /// solo por ser grande.
    /// </summary>
    [Fact]
    public void La_clase_enorme_no_es_la_peor_por_ser_enorme()
    {
        App();
        Inventory(
            1,
            Unit("grande.cs", "M", 5000, UnitState.Auditada),
            Unit("pequena.cs", "M", 100, UnitState.Auditada));

        for (int i = 0; i < 6; i++)
        {
            Finding("grande.cs", Severity.Media);
        }

        Finding("pequena.cs", Severity.Media);

        HeatmapView view = Build();
        HeatUnit big = UnitOf(view, "grande.cs");
        HeatUnit small = UnitOf(view, "pequena.cs");

        big.KnownDebt.Should().BeGreaterThan(small.KnownDebt, "en absoluto tiene más");
        big.Density.Should().BeLessThan(small.Density!.Value, "pero está mucho menos concentrada");
    }

    // ============================================ Lo que NO es deuda

    /// <summary>
    /// Resueltos y silenciados no cuentan. Uno se arregló y del otro se decidió que no se
    /// arregla: sumarlos pintaría de oscuro el trabajo ya hecho.
    /// </summary>
    [Fact]
    public void Los_resueltos_y_los_silenciados_no_son_deuda()
    {
        App();
        Inventory(1, Unit("a.cs", "M", 1000, UnitState.Auditada));
        Finding("a.cs", Severity.Critica, FindingStatus.Resuelto);
        Finding("a.cs", Severity.Critica, FindingStatus.Silenciado);
        Finding("a.cs", Severity.Media);

        HeatUnit unit = UnitOf(Build(), "a.cs");

        unit.KnownDebt.Should().Be(2);
        unit.Findings.Total.Should().Be(1);
        unit.Density.Should().Be(2);
    }

    /// <summary>
    /// Un defecto sistémico con varias ubicaciones pesa una vez EN CADA unidad donde está —quien
    /// arregle cualquiera de ellas tiene ese problema delante— y nunca dos veces en la misma.
    /// </summary>
    [Fact]
    public void Un_hallazgo_con_varias_ubicaciones_pesa_una_vez_en_cada_unidad()
    {
        App();
        Inventory(
            1,
            Unit("a.cs", "M", 1000, UnitState.Auditada),
            Unit("b.cs", "M", 1000, UnitState.Auditada));

        var finding = Finding("a.cs", Severity.Alta, FindingStatus.Activo, "b.cs", "a.cs");
        finding.Locations.Should().HaveCount(3, "dos de ellas apuntan a la misma unidad");

        HeatmapView view = Build();
        UnitOf(view, "a.cs").KnownDebt.Should().Be(5);
        UnitOf(view, "b.cs").KnownDebt.Should().Be(5);
    }

    // ============================================ Honestidad estructural

    /// <summary>
    /// <b>La regla no negociable.</b> Sin auditar, la densidad es <c>null</c> —desconocida—, no
    /// cero. Y la unidad no puede pasar por medida.
    /// </summary>
    [Fact]
    public void Una_unidad_sin_auditar_tiene_densidad_desconocida_y_no_cero()
    {
        App();
        Inventory(1, Unit("virgen.cs", "M", 400), Unit("limpia.cs", "M", 400, UnitState.Auditada));

        HeatmapView view = Build();
        HeatUnit untouched = UnitOf(view, "virgen.cs");
        HeatUnit audited = UnitOf(view, "limpia.cs");

        untouched.Knowledge.Should().Be(HeatKnowledge.NoAuditada);
        untouched.Density.Should().BeNull();
        untouched.IsMeasured.Should().BeFalse();
        untouched.Value(HeatMetric.Densidad).Should().BeNull();
        untouched.Value(HeatMetric.Deuda).Should().BeNull("tampoco en deuda absoluta se puede afirmar su total");

        // La auditada y limpia SÍ tiene densidad, y vale cero: son dos cosas distintas y el mapa
        // las pinta distinto.
        audited.Density.Should().Be(0);
        audited.IsMeasured.Should().BeTrue();
    }

    /// <summary>
    /// «Grande» es un motivo para NO auditar, no una forma de haber auditado. Sus hallazgos
    /// automáticos no la convierten en medida.
    /// </summary>
    [Fact]
    public void Una_unidad_excluida_por_tamano_sigue_sin_auditar()
    {
        App();
        Inventory(1, Unit("mole.cs", "M", 5000, UnitState.Grande));
        Finding("mole.cs", Severity.Media);

        HeatUnit unit = UnitOf(Build(), "mole.cs");

        unit.Knowledge.Should().Be(HeatKnowledge.NoAuditada);
        unit.Density.Should().BeNull();
        unit.KnownDebt.Should().Be(2, "lo que se le conoce sí se sabe, y es una cota inferior");
        unit.IsQualified.Should().BeTrue("gris con deuda conocida: el relleno no lo cuenta todo");
    }

    /// <summary>El gris sin nada conocido no lleva la marca: no hay nada que matizar.</summary>
    [Fact]
    public void El_gris_sin_hallazgos_conocidos_no_lleva_marca()
    {
        App();
        Inventory(1, Unit("virgen.cs", "M", 400));

        UnitOf(Build(), "virgen.cs").IsQualified.Should().BeFalse();
    }

    // ============================================ Agregación del módulo

    /// <summary>
    /// El módulo agrega deuda total, densidad ponderada por LOC, unidades y cobertura. La
    /// densidad divide <b>solo lo auditado entre lo auditado</b>: meter en el denominador lo que
    /// nadie ha mirado diluye la densidad en proporción a lo poco que se ha mirado.
    /// </summary>
    [Fact]
    public void El_modulo_agrega_deuda_densidad_unidades_y_cobertura()
    {
        App();
        Inventory(
            1,
            Unit("m/a.cs", "M", 1000, UnitState.Auditada),
            Unit("m/b.cs", "M", 1000, UnitState.Auditada),
            Unit("m/c.cs", "M", 8000));

        Finding("m/a.cs", Severity.Critica);   // 10
        Finding("m/b.cs", Severity.Media);     // 2
        Finding("m/c.cs", Severity.Alta);      // 5, en la que nadie auditó

        HeatModule module = Build().Modules.Single(m => m.Name == "M");

        module.UnitCount.Should().Be(3);
        module.AuditedUnits.Should().Be(2);
        module.Coverage.Should().BeApproximately(2 / 3.0, 1e-9);
        module.KnownDebt.Should().Be(17, "la deuda total es toda la conocida");
        module.AuditedDebt.Should().Be(12);
        module.UnauditedDebt.Should().Be(5);
        module.Loc.Should().Be(10000);
        module.AuditedLoc.Should().Be(2000);

        // 12 puntos sobre 2 KLOC auditadas = 6. Con las 10 KLOC del módulo entero saldría 1,7:
        // seis veces más limpio por no haber mirado.
        module.Density.Should().Be(6);
        module.IsMeasured.Should().BeTrue();
    }

    /// <summary>Sin una sola unidad auditada, el módulo entero es desconocido.</summary>
    [Fact]
    public void Un_modulo_sin_nada_auditado_no_tiene_densidad()
    {
        App();
        Inventory(1, Unit("m/a.cs", "M", 1000), Unit("m/b.cs", "M", 1000));
        Finding("m/a.cs", Severity.Critica);

        HeatModule module = Build().Modules.Single();

        module.IsMeasured.Should().BeFalse();
        module.Density.Should().BeNull();
        module.Value(HeatMetric.Densidad).Should().BeNull();
        module.KnownDebt.Should().Be(10, "lo conocido se sigue diciendo");
        module.IsQualified.Should().BeTrue();
    }

    /// <summary>Los módulos salen ordenados por tamaño: es el orden en que se van a dibujar.</summary>
    [Fact]
    public void Los_modulos_salen_de_mayor_a_menor_tamano()
    {
        App();
        Inventory(
            1,
            Unit("p/a.cs", "Pequeno", 100),
            Unit("g/a.cs", "Grande", 9000),
            Unit("m/a.cs", "Medio", 2000));

        Build().Modules.Select(m => m.Name).Should().Equal("Grande", "Medio", "Pequeno");
    }

    // ============================================ «Cambiada»

    /// <summary>
    /// Una unidad auditada en un ciclo anterior cuyo contenido ya no es el mismo: la medida existe
    /// y es de otro código. Se marca, y se dice en el tooltip; no se borra el dato.
    /// </summary>
    [Fact]
    public void Una_unidad_auditada_cuyo_codigo_cambio_despues_se_marca()
    {
        App(cycle: 2);
        Ulid session = _ulids.NewUlid();
        _hub.Store.WriteSession(new AuditSession
        {
            Id = session,
            AppSlug = "app",
            By = "tester",
            Machine = "m",
            Mode = AuditMode.Lotes,
            CycleN = 1,
            StartedUtc = DateTimeOffset.UtcNow.AddDays(-3),
        });

        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = new List<InventoryUnit>
            {
                Unit("a.cs", "M", 400, UnitState.Auditada, session, "sha256:viejo"),
                Unit("b.cs", "M", 400, UnitState.Auditada, session, "sha256:quieto"),
            },
        });

        Inventory(
            2,
            Unit("a.cs", "M", 420, UnitState.Auditada, session, "sha256:nuevo"),
            Unit("b.cs", "M", 400, UnitState.Auditada, session, "sha256:quieto"));

        HeatmapView view = Build();

        UnitOf(view, "a.cs").Knowledge.Should().Be(HeatKnowledge.Cambiada);
        UnitOf(view, "a.cs").IsQualified.Should().BeTrue();
        UnitOf(view, "b.cs").Knowledge.Should().Be(HeatKnowledge.Auditada);
    }

    /// <summary>
    /// Sin el dato con el que afirmarlo —sin sesión registrada— no se marca nada. «Cambiada» es
    /// una afirmación, y la norma de la casa es no hacerlas a ciegas (N-2).
    /// </summary>
    [Fact]
    public void Sin_traza_del_ciclo_anterior_no_se_afirma_que_cambio()
    {
        App(cycle: 2);
        Inventory(2, Unit("a.cs", "M", 400, UnitState.Auditada, _ulids.NewUlid(), "sha256:nuevo"));

        UnitOf(Build(), "a.cs").Knowledge.Should().Be(HeatKnowledge.Auditada);
    }

    // ============================================ Bordes

    /// <summary>Sin inventario no hay mapa, y la vista lo sabe en vez de dibujar un lienzo vacío.</summary>
    [Fact]
    public void Sin_inventario_el_mapa_esta_vacio_pero_sabe_de_quien_es()
    {
        App(name: "Aplicación");

        HeatmapView view = Build();

        view.IsEmpty.Should().BeTrue();
        view.AppName.Should().Be("Aplicación");
        view.AppOptions.Should().ContainSingle(o => o.Slug == "app");
    }

    [Fact]
    public void Una_app_que_no_existe_devuelve_el_mapa_vacio_con_el_selector_puesto()
    {
        App();

        HeatmapView view = Build("no-existe");

        view.IsEmpty.Should().BeTrue();
        view.Slug.Should().BeEmpty();
        view.AppOptions.Should().ContainSingle(o => o.Slug == "app");
    }

    /// <summary>Los hallazgos de OTRA aplicación no entran en este mapa.</summary>
    [Fact]
    public void El_mapa_es_de_una_sola_aplicacion()
    {
        App();
        App("otra", "Otra");
        Inventory(1, Unit("a.cs", "M", 500, UnitState.Auditada));

        _hub.Store.WriteInventory("otra", new InventoryCycle
        {
            CycleN = 1,
            Units = new List<InventoryUnit> { Unit("a.cs", "M", 500, UnitState.Auditada) },
        });

        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "commit", "tester");
        _hub.Store.WriteFinding("otra", new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "R-9",
            Title = "De la otra app",
            Severity = Severity.Critica,
            Locations = new List<Location> { new("a.cs", 1) },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        });

        UnitOf(Build(), "a.cs").KnownDebt.Should().Be(0);
    }

    /// <summary>El total de la aplicación se lee igual que el de un módulo: sobre lo auditado.</summary>
    [Fact]
    public void El_total_de_la_aplicacion_dice_cobertura_y_densidad_de_lo_auditado()
    {
        App();
        Inventory(
            1,
            Unit("a/x.cs", "A", 1000, UnitState.Auditada),
            Unit("b/y.cs", "B", 3000));

        Finding("a/x.cs", Severity.Alta);
        Finding("b/y.cs", Severity.Critica);

        HeatmapView view = Build();

        view.TotalUnits.Should().Be(2);
        view.AuditedUnits.Should().Be(1);
        view.Coverage.Should().Be(0.5);
        view.TotalLoc.Should().Be(4000);
        view.KnownDebt.Should().Be(15);
        view.Density.Should().Be(5, "5 puntos auditados sobre 1 KLOC auditada");
    }

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
            // Un fichero todavía abierto por el runner no puede tumbar la serie.
        }
    }
}
