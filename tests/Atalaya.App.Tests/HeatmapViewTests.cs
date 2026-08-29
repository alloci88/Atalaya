using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Media;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Copilot;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F10 §2 y §3 — la vista del mapa de calor: la escala, la leyenda, la tabla equivalente y la
/// exportación.
/// <para>
/// Las reglas que se fijan aquí no son de estilo. Un arcoíris sobre una magnitud continua inventa
/// fronteras; un color de severidad como relleno hace que «densidad alta» se lea «hay una
/// crítica»; un gris que fuera el paso frío de la escala convertiría «no mirado» en «limpio»; y
/// un mapa sin tabla deja fuera a quien no distinga dos tonos.
/// </para>
/// </summary>
public sealed class HeatmapViewTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly RecordingSaver _saver = new();
    private readonly ToastCenter _toasts = new();

    public HeatmapViewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f10-vista", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Contoso" });
    }

    // ============================================================ Utilidades

    /// <summary>El <c>IFileSaver</c> de mentira: guarda lo que se le propuso y devuelve una ruta.</summary>
    private sealed class RecordingSaver : IFileSaver
    {
        public string? Suggested { get; private set; }

        public string? Answer { get; set; }

        public string? Pick(string title, string suggestedFileName, string filter)
        {
            Suggested = suggestedFileName;
            return Answer;
        }
    }

    private HeatmapViewModel Vm(NavigationService? navigation = null)
        => new(
            new HeatmapQuery(_hub),
            navigation ?? new NavigationService(new NoServices()),
            _settings,
            _hub,
            _toasts,
            _saver,
            TimeProvider.System);

    private void Seed(string slug = "app", string name = "XBLAST")
    {
        _hub.Store.WriteApp(new AppConfig { Slug = slug, Name = name, RepoUrl = $"u/{slug}", CurrentCycle = 1 });
        _hub.Store.WriteInventory(slug, new InventoryCycle
        {
            CycleN = 1,
            Units = new List<InventoryUnit>
            {
                Unit("Core/Sucia.cs", "Core", 200, UnitState.Auditada),
                Unit("Core/Limpia.cs", "Core", 800, UnitState.Auditada),
                Unit("Core/Virgen.cs", "Core", 3000),
                Unit("Utils/Mole.cs", "Utils", 4000, UnitState.Grande),
            },
        });

        Finding(slug, "Core/Sucia.cs", Severity.Critica);
        Finding(slug, "Core/Sucia.cs", Severity.Alta);
        Finding(slug, "Utils/Mole.cs", Severity.Media);
    }

    private static InventoryUnit Unit(string path, string module, int loc, UnitState state = UnitState.Pendiente)
        => new() { Path = path, Module = module, Loc = loc, State = state, ContentHash = $"sha256:{path.Length:x4}" };

    private void Finding(string slug, string path, Severity severity)
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "commit", "tester");
        _hub.Store.WriteFinding(slug, new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "R-1",
            Title = $"Algo en {path}",
            Severity = severity,
            Locations = new List<Location> { new(path, 1) },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        });
    }

    /// <summary>
    /// Un módulo con una unidad grande y cuarenta migajas: el caso que obliga a agrupar.
    /// </summary>
    private void Crumbs(bool audited = true, bool hotOne = false)
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "Migas", RepoUrl = "u/app", CurrentCycle = 1 });

        UnitState state = audited ? UnitState.Auditada : UnitState.Pendiente;
        var units = new List<InventoryUnit> { Unit("M/Gorda.cs", "M", 40000, state) };
        for (int i = 0; i < 40; i++)
        {
            units.Add(Unit($"M/Miga{i:00}.cs", "M", 30, state));
        }

        if (hotOne)
        {
            units.Add(Unit("M/Ardiendo.cs", "M", 30, state));
        }

        _hub.Store.WriteInventory("app", new InventoryCycle { CycleN = 1, Units = units });

        if (hotOne)
        {
            Finding("app", "M/Ardiendo.cs", Severity.Critica);
            Finding("app", "M/Miga00.cs", Severity.Baja);
        }
    }

    private static string Hex(Brush? brush)
        => brush is SolidColorBrush solid ? solid.Color.ToString().ToUpperInvariant() : string.Empty;

    private static string ViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views");
    }

    // ============================================ La escala

    /// <summary>
    /// <b>La propiedad que hace secuencial a una escala no es tener un solo tono: es que la
    /// claridad sea monótona.</b> Magma gira el matiz de violeta a ámbar mientras la claridad sube
    /// sin volver atrás en ningún paso — que es exactamente lo que un arcoíris no hace, y por lo
    /// que un arcoíris no se puede leer sin mirar la leyenda.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void La_claridad_de_la_rampa_es_monotona_en_los_dos_temas(bool dark)
    {
        var steps = DensityScale.Density;
        steps.Should().HaveCount(5);

        var lightness = steps.Select(s => Luminance(s.For(dark))).ToList();

        // Sobre papel, de pálido a profundo; sobre negro, de profundo a brillante. Cada rampa se
        // eligió para SU fondo: no es la otra invertida por código.
        var expected = dark ? lightness.OrderBy(v => v) : lightness.OrderByDescending(v => v);
        lightness.Should().Equal(expected, "sin claridad monótona la escala no se ordena sola");

        // Y monótona de verdad: ningún paso repite ni retrocede.
        lightness.Zip(lightness.Skip(1)).Should().OnlyContain(p => Math.Abs(p.First - p.Second) > 0.02,
            "dos pasos que se parecen tanto son un paso");
    }

    /// <summary>
    /// Los valores exactos de la rampa, fijados. Están verificados uno a uno (claridad y
    /// contraste); un retoque «para que combine» tiene que romper este test y pasar otra vez por
    /// esa verificación, no colarse en un commit de maquetado.
    /// </summary>
    [Fact]
    public void La_rampa_es_la_verificada_y_esta_en_un_solo_sitio()
    {
        DensityScale.Density.Select(s => s.Dark.ToUpperInvariant())
            .Should().Equal("#5B2A78", "#8C2981", "#C43C75", "#F1605D", "#FEA772");

        DensityScale.Density.Select(s => s.Light.ToUpperInvariant())
            .Should().Equal("#FEC98D", "#F1605D", "#C43C75", "#8C2981", "#4B1D6F");

        // La misma rampa para las dos métricas: lo que cambia entre densidad y deuda absoluta son
        // los umbrales, no los colores.
        DensityScale.Debt.Select(s => s.Dark).Should().Equal(DensityScale.Density.Select(s => s.Dark));

        DensityScale.Surface.Dark.ToUpperInvariant().Should().Be("#12151D");
        DensityScale.Surface.Light.ToUpperInvariant().Should().Be("#F6F7FA");
    }

    /// <summary>
    /// La tinta va POR PASO y es la que más contrasta. La regla vieja la calculaba con una
    /// fórmula de luminancia y elegía blanco sobre el coral del paso 4 — donde el blanco da 3,2 de
    /// contraste y el negro 5,9—, así que la etiqueta más importante del mapa era la peor leída.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void La_tinta_de_cada_paso_contrasta_y_gana_a_la_alternativa(bool dark)
    {
        foreach (HeatStep step in DensityScale.Density)
        {
            string fill = step.For(dark);
            string ink = step.InkFor(dark);
            string other = Luminance(ink) > 0.5 ? "#101014" : "#FFFFFF";

            Contrast(fill, ink).Should().BeGreaterThan(4.5,
                $"el texto sobre {fill} tiene que cumplir AA");
            Contrast(fill, ink).Should().BeGreaterThan(Contrast(fill, other),
                $"sobre {fill} se eligió la tinta que MENOS contrasta");
        }
    }

    /// <summary>Y cada paso se distingue de la superficie sobre la que se dibuja.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cada_paso_se_despega_de_su_superficie(bool dark)
    {
        string surface = DensityScale.Surface.For(dark);

        foreach (HeatStep step in DensityScale.Density)
        {
            Contrast(step.For(dark), surface).Should().BeGreaterThan(1.3,
                $"{step.For(dark)} se funde con el fondo");
        }
    }

    // La luminancia relativa y el contraste de la WCAG, que es la definición con la que se
    // verificó la rampa. Se calculan aquí para poder AFIRMARLO, no para elegir colores.
    private static double Luminance(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(string a, string b)
    {
        double x = Luminance(a);
        double y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>
    /// Los colores de severidad están RESERVADOS: significan crítica/alta/media/baja en toda la
    /// aplicación. Ninguno puede ser el relleno de una celda, donde significaría magnitud.
    /// </summary>
    [Fact]
    public void Ningun_paso_de_la_escala_usa_un_color_de_severidad()
    {
        var reserved = SeverityPalette.All.Select(h => h.ToUpperInvariant()).ToList();

        foreach (HeatStep step in DensityScale.Density.Concat(DensityScale.Debt))
        {
            reserved.Should().NotContain(step.Light.ToUpperInvariant());
            reserved.Should().NotContain(step.Dark.ToUpperInvariant());
        }

        reserved.Should().NotContain(DensityScale.Unknown.Light.ToUpperInvariant());
        reserved.Should().NotContain(DensityScale.Unknown.Dark.ToUpperInvariant());
    }

    /// <summary>
    /// El gris de «no auditada» NO es el paso frío de la escala. Si lo fuera, lo no mirado se
    /// leería como lo limpio, que es la mentira que esta vista existe para no contar.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void El_gris_de_no_auditada_no_es_el_paso_frio_de_la_escala(bool dark)
    {
        string unknown = DensityScale.Unknown.For(dark).ToUpperInvariant();

        DensityScale.Density.Select(s => s.For(dark).ToUpperInvariant()).Should().NotContain(unknown);

        var grey = (Color)ColorConverter.ConvertFromString(unknown);
        var coldest = (Color)ColorConverter.ConvertFromString(DensityScale.Density[0].For(dark));
        Math.Abs(grey.B - grey.G).Should().BeLessThan(12, "el gris es neutro, no del tono de la escala");
        (grey.ToString() == coldest.ToString()).Should().BeFalse();
    }

    /// <summary>Los umbrales son fijos y contiguos: cada valor cae en un paso y solo en uno.</summary>
    [Fact]
    public void Los_umbrales_cubren_la_recta_sin_huecos_ni_solapes()
    {
        foreach (HeatMetric metric in new[] { HeatMetric.Densidad, HeatMetric.Deuda })
        {
            var steps = DensityScale.For(metric);
            steps[0].From.Should().Be(0);
            steps[^1].To.Should().BeNull("el último paso no tiene techo");

            for (int i = 1; i < steps.Count; i++)
            {
                steps[i].From.Should().Be(steps[i - 1].To!.Value, "los tramos se tocan");
            }

            foreach (double value in new[] { 0, 0.5, 4.9, 5, 14.9, 15, 39.9, 40, 99.9, 100, 5000 })
            {
                steps.Count(s => s.Contains(value)).Should().BeLessThanOrEqualTo(1, $"{value} cae en un solo paso");
            }
        }
    }

    /// <summary>Un valor desconocido no tiene paso: quien pregunte se lleva null y decide.</summary>
    [Fact]
    public void Lo_desconocido_no_tiene_paso_en_la_escala()
    {
        DensityScale.StepOf(null, HeatMetric.Densidad).Should().BeNull();
        DensityScale.StepOf(0, HeatMetric.Densidad)!.Index.Should().Be(0);
        DensityScale.StepOf(206.3, HeatMetric.Densidad)!.Index.Should().Be(4);
    }

    // ============================================ La leyenda

    /// <summary>
    /// La leyenda escribe los cinco umbrales —para poder decir «esto es un módulo del paso 4»—,
    /// la entrada de «no auditada» con su advertencia y los pesos con los que se suma la deuda.
    /// </summary>
    [Fact]
    public async Task La_leyenda_escribe_los_umbrales_los_pesos_y_la_advertencia_del_gris()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        var labels = vm.Legend.Select(l => l.Label).ToList();

        labels.Should().HaveCount(DensityScale.StepCount + 3);
        labels.Should().Contain(l => l.Contains("< 5"));
        labels.Should().Contain(l => l.Contains("5 – 15"));
        labels.Should().Contain(l => l.Contains("15 – 40"));
        labels.Should().Contain(l => l.Contains("40 – 100"));
        labels.Should().Contain(l => l.Contains("≥ 100"));
        labels.Should().Contain(l => l.Contains("DESCONOCIDA"), "el gris se explica, no se adivina");
        labels.Should().Contain(l => l.Contains("Crítica 10") && l.Contains("Baja 1"));

        // Y la entrada del gris lleva el pincel tramado, que es el otro canal.
        vm.Legend.Single(l => l.Label.Contains("DESCONOCIDA")).Fill.Should().BeOfType<DrawingBrush>();
    }

    /// <summary>Al cambiar de métrica cambian los umbrales de la leyenda, no solo el rótulo.</summary>
    [Fact]
    public async Task La_leyenda_de_deuda_absoluta_trae_sus_propios_umbrales()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        vm.SelectedMetric = vm.MetricOptions.Single(o => o.Metric == HeatMetric.Deuda);

        vm.Legend.Select(l => l.Label).Should().Contain(l => l.Contains("< 2") && l.Contains("puntos"));
        vm.ScaleCaption.Should().Contain("deuda total");
    }

    // ============================================ El mapa

    /// <summary>
    /// El relleno de una celda sin medir es <c>null</c>, que es lo que hace que el control pinte
    /// el gris tramado; el de una medida es el color de su paso.
    /// </summary>
    [Fact]
    public async Task La_celda_sin_auditar_va_sin_color_y_la_auditada_con_el_de_su_paso()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        HeatGroup core = vm.Groups.Single(g => g.Name == "Core");
        HeatCell dirty = core.Cells.Single(c => c.Label == "Sucia.cs");
        HeatCell untouched = core.Cells.Single(c => c.Label == "Virgen.cs");

        // 15 puntos sobre 0,2 KLOC = 75 → paso 4 (40–100).
        Hex(dirty.Fill).Should().Be(Hex(HeatBrushes.Solid(DensityScale.Density[3].Dark)));
        untouched.Fill.Should().BeNull("sin color: el control pinta el gris tramado");
        untouched.Qualified.Should().BeFalse();

        HeatCell mole = vm.Groups.Single(g => g.Name == "Utils").Cells.Single();
        mole.Fill.Should().BeNull("excluida por tamaño sigue siendo no auditada");
        mole.Qualified.Should().BeTrue("gris con deuda conocida: el relleno no lo cuenta todo");
    }

    /// <summary>El área de la celda es el tamaño, nunca la deuda. Son dos canales distintos.</summary>
    [Fact]
    public async Task El_peso_de_una_celda_son_sus_lineas()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        HeatGroup core = vm.Groups.Single(g => g.Name == "Core");
        core.Weight.Should().Be(4000, "200 + 800 + 3000");
        core.Cells.Single(c => c.Label == "Sucia.cs").Weight.Should().Be(200);
    }

    /// <summary>El tooltip de una celda dice unidad, LOC, severidades, deuda, densidad y estado.</summary>
    [Fact]
    public async Task El_tooltip_de_una_unidad_lo_dice_todo()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        var core = vm.Groups.Single(g => g.Name == "Core");
        string dirty = core.Cells.Single(c => c.Label == "Sucia.cs").Tooltip;

        dirty.Should().Contain("Core/Sucia.cs");
        dirty.Should().Contain("200 líneas");
        dirty.Should().Contain("Auditada");
        dirty.Should().Contain("C1 · A1");
        dirty.Should().Contain("Deuda conocida: 15");
        dirty.Should().Contain("Densidad: 75");

        string untouched = core.Cells.Single(c => c.Label == "Virgen.cs").Tooltip;
        untouched.Should().Contain("No auditada");
        untouched.Should().Contain("DESCONOCIDA");
        untouched.Should().NotContain("Densidad: 0");

        string mole = vm.Groups.Single(g => g.Name == "Utils").Cells.Single().Tooltip;
        mole.Should().Contain("excluida por tamaño");
        mole.Should().Contain("cota inferior");
    }

    /// <summary>El tooltip del módulo lleva la cobertura, que es lo que matiza su densidad.</summary>
    [Fact]
    public async Task El_tooltip_de_un_modulo_lleva_su_cobertura()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        string core = vm.Groups.Single(g => g.Name == "Core").Tooltip;

        core.Should().Contain("Cobertura: 2 auditadas de 3");
        core.Should().Contain("Densidad (sobre lo auditado)");
        core.Should().Contain("4.000 líneas");
    }

    /// <summary>
    /// El aviso de cobertura no es decoración: es la frase que impide leer el gris como limpio.
    /// Y desaparece cuando no hay nada gris.
    /// </summary>
    [Fact]
    public async Task El_aviso_de_cobertura_esta_mientras_quede_algo_sin_auditar()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        vm.CoverageWarning.Should().Contain("DESCONOCIDA");
        vm.CoverageWarning.Should().Contain("2 de 4");
        vm.Summary.Should().Contain("2 auditadas");

        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = new List<InventoryUnit> { Unit("Core/Sucia.cs", "Core", 200, UnitState.Auditada) },
        });

        await vm.LoadAsync();
        vm.CoverageWarning.Should().BeEmpty("con todo auditado no hay nada que advertir");
    }

    // ============================================ Zoom y migas

    [Fact]
    public async Task Un_clic_en_un_modulo_amplia_y_la_miga_devuelve()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        vm.IsZoomed.Should().BeFalse();
        vm.Groups.Should().HaveCount(2);

        await vm.TileCommand.ExecuteAsync(vm.Groups.Single(g => g.Name == "Core").Payload);

        vm.IsZoomed.Should().BeTrue();
        vm.ZoomedModule.Should().Be("Core");
        vm.Groups.Should().ContainSingle().Which.Name.Should().Be("Core");
        vm.Rows.Should().OnlyContain(r => r.Module == "Core", "la tabla acompaña al zoom");

        vm.ZoomOutCommand.Execute(null);

        vm.IsZoomed.Should().BeFalse();
        vm.Groups.Should().HaveCount(2);
    }

    /// <summary>
    /// En la vista completa, un clic en una celda de dos píxeles amplía su módulo: pedirle a
    /// alguien que acierte una celda diminuta para llegar a sus hallazgos sería un gesto que no se
    /// puede ejecutar.
    /// </summary>
    [Fact]
    public async Task En_la_vista_completa_un_clic_en_una_celda_amplia_su_modulo()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        object? cell = vm.Groups.Single(g => g.Name == "Utils").Cells.Single().Payload;
        await vm.TileCommand.ExecuteAsync(cell);

        vm.ZoomedModule.Should().Be("Utils");
    }

    /// <summary>El zoom sobrevive a una recarga: sincronizar no puede devolverte a la portada.</summary>
    [Fact]
    public async Task El_zoom_sobrevive_a_una_recarga()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();
        await vm.TileCommand.ExecuteAsync(vm.Groups.Single(g => g.Name == "Core").Payload);

        await vm.LoadAsync();

        vm.ZoomedModule.Should().Be("Core");
    }

    // ============================================ La tabla equivalente

    /// <summary>
    /// La alternativa accesible trae las MISMAS unidades y las mismas columnas. Sin ella el color
    /// sería el único canal, y hay quien no lo tiene.
    /// </summary>
    [Fact]
    public async Task La_tabla_trae_las_mismas_unidades_con_todas_sus_columnas()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        vm.Rows.Should().HaveCount(4);

        HeatRow dirty = vm.Rows.Single(r => r.Unit == "Sucia.cs");
        dirty.Module.Should().Be("Core");
        dirty.Loc.Should().Be(200);
        dirty.Critica.Should().Be(1);
        dirty.Alta.Should().Be(1);
        dirty.Debt.Should().Be(15);
        dirty.DensityText.Should().Be("75");
        dirty.State.Should().Be("Auditada");

        HeatRow untouched = vm.Rows.Single(r => r.Unit == "Virgen.cs");
        untouched.DensityText.Should().Be("—", "«—» y no «0,0»: no es una medida");
        untouched.State.Should().Be("No auditada");
    }

    [Fact]
    public async Task La_tabla_ordena_por_cualquier_columna_y_repetir_invierte()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        vm.SortByCommand.Execute(HeatSort.Loc);
        vm.Rows.Select(r => r.Loc).Should().BeInDescendingOrder();

        vm.SortByCommand.Execute(HeatSort.Loc);
        vm.Rows.Select(r => r.Loc).Should().BeInAscendingOrder();
        vm.HeaderLoc.Should().Contain("▴");

        vm.SortByCommand.Execute(HeatSort.Unit);
        vm.Rows.Select(r => r.Unit).Should().BeInAscendingOrder("los nombres empiezan de la A a la Z");
    }

    /// <summary>
    /// Las densidades desconocidas van al final en LOS DOS sentidos. Un «—» no es ni el máximo ni
    /// el mínimo, y colarlo arriba al invertir diría que son las unidades más limpias.
    /// </summary>
    [Fact]
    public async Task Al_ordenar_por_densidad_lo_desconocido_va_al_final_en_los_dos_sentidos()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        vm.SortByCommand.Execute(HeatSort.Density);
        vm.Rows.Take(2).Should().OnlyContain(r => r.Density != null);
        vm.Rows.Skip(2).Should().OnlyContain(r => r.Density == null);

        vm.SortByCommand.Execute(HeatSort.Density);
        vm.Rows.Take(2).Should().OnlyContain(r => r.Density != null);
        vm.Rows.Skip(2).Should().OnlyContain(r => r.Density == null);
    }

    // ============================================ Navegación

    [Fact]
    public async Task Un_clic_en_una_unidad_ampliada_abre_sus_hallazgos_filtrados()
    {
        Seed();
        var findings = new FindingsViewModel(
            _hub, new NavigationService(new NoServices()), _settings, new GroupExpansionMemory());
        NavigationService navigation = TestFactory.NavigationWith(findings);
        HeatmapViewModel vm = Vm(navigation);
        await vm.LoadAsync();

        await vm.TileCommand.ExecuteAsync(vm.Groups.Single(g => g.Name == "Core").Payload);
        object? cell = vm.Groups.Single().Cells.Single(c => c.Label == "Sucia.cs").Payload;
        await vm.TileCommand.ExecuteAsync(cell);

        navigation.Current.Should().BeSameAs(findings);
        findings.SelectedApp!.Slug.Should().Be("app");
        findings.SearchText.Should().Be("Core/Sucia.cs");
        findings.ResultCount.Should().Be(2, "las dos de esa unidad, ni la de Utils");
    }

    /// <summary>
    /// Auditar desde el mapa lleva al inventario con la unidad marcada. NO lanza la sesión: el
    /// sitio donde se confirma el gasto sigue siendo uno solo (F5.13).
    /// </summary>
    [Fact]
    public async Task Auditar_desde_el_mapa_marca_la_unidad_en_el_inventario_sin_lanzar_nada()
    {
        Seed();
        InventoryViewModel inventory = TestInventory();
        NavigationService navigation = TestFactory.NavigationWith(inventory);
        HeatmapViewModel vm = Vm(navigation);
        await vm.LoadAsync();

        object? cell = vm.Groups.Single(g => g.Name == "Core").Cells.Single(c => c.Label == "Sucia.cs").Payload;
        await vm.ActivateCommand.ExecuteAsync(cell);

        navigation.Current.Should().BeSameAs(inventory);
        inventory.Slug.Should().Be("app");
        inventory.SelectedUnits().Should().Equal("Core/Sucia.cs");
        inventory.SelectedCount.Should().Be(1, "una y solo una: el gasto no se hereda de nadie");
    }

    // ============================================ La exportación

    /// <summary>El nombre propuesto dice qué es y de cuándo.</summary>
    [Fact]
    public void El_nombre_del_fichero_dice_que_es_y_de_cuando()
        => HeatmapImage.FileName("xblast", new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero))
            .Should().Be("atalaya-xblast-mapa-de-calor-2026-08-29.png");

    /// <summary>
    /// La imagen se explica sola: título con la app y la fecha, la frase de qué mide el color, la
    /// leyenda entera y el pie con la organización.
    /// </summary>
    [Fact]
    public async Task La_imagen_lleva_titulo_leyenda_y_pie()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        HeatmapImageRequest request = vm.ImageRequest(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero));

        request.Title.Should().Be("XBLAST · mapa de calor · 29 agosto 2026");
        request.Footer.Should().Be("Atalaya · Contoso");
        request.Subtitle.Should().Contain("el color, la deuda por cada mil líneas");
        request.Subtitle.Should().Contain("2 auditadas");
        request.Legend.Should().HaveCount(DensityScale.StepCount + 3);
        request.Groups.Should().HaveCount(2);
    }

    /// <summary>
    /// Ampliado a un módulo, la lámina habla de ESE módulo: el título lo nombra y el pie de foto
    /// cuenta sus unidades, no las de la aplicación entera. Un pie con las cifras de toda la app
    /// debajo de un mapa que solo enseña un módulo es una nota que contradice la figura.
    /// </summary>
    [Fact]
    public async Task Ampliado_la_lamina_habla_del_modulo_y_no_de_la_aplicacion_entera()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();
        vm.Summary.Should().Contain("4 unidades");

        await vm.TileCommand.ExecuteAsync(vm.Groups.Single(g => g.Name == "Core").Payload);

        HeatmapImageRequest request = vm.ImageRequest(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero));
        request.Title.Should().StartWith("XBLAST · Core · mapa de calor");
        request.Subtitle.Should().Contain("3 unidades", "las de Core, no las cuatro de la app");
        request.Subtitle.Should().Contain("4.000 líneas");
        vm.CoverageWarning.Should().Contain("1 de 3");
    }

    /// <summary>Cancelar el diálogo no escribe nada y no avisa de nada.</summary>
    [Fact]
    public async Task Cancelar_la_exportacion_no_escribe_nada()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();
        _saver.Answer = null;

        vm.ExportImageCommand.Execute(null);

        _saver.Suggested.Should().Be("atalaya-app-mapa-de-calor-" + DateTimeOffset.Now.ToString("yyyy-MM-dd") + ".png");
    }

    /// <summary>
    /// Y el PNG se escribe de verdad. Es el único test que renderiza píxeles: la composición usa
    /// colores explícitos y ningún control de WPF-UI justamente para poder hacerlo sin ventana.
    /// </summary>
    [Fact]
    public async Task La_exportacion_escribe_un_PNG_con_el_lienzo_completo()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        string target = Path.Combine(_root, "mapa.png");
        Directory.CreateDirectory(_root);
        HeatmapImageRequest request = vm.ImageRequest(DateTimeOffset.Now);

        StaRunner.Run(() => HeatmapImage.Save(request, target));

        File.Exists(target).Should().BeTrue();
        new FileInfo(target).Length.Should().BeGreaterThan(4096, "un PNG vacío pesaría casi nada");

        byte[] header = File.ReadAllBytes(target).Take(8).ToArray();
        header.Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
    }

    // ============================================ El control, con tamaño de verdad

    /// <summary>
    /// El treemap colocado sobre un lienzo real: las áreas son proporcionales a las LOC y ninguna
    /// celda se pisa con otra. Es la misma invariante que <see cref="TreemapLayoutTests"/>, pero
    /// pasando por el control —incluidos sus márgenes y su banda de cabecera—.
    /// </summary>
    [Fact]
    public async Task El_control_coloca_los_modulos_sin_solapes_y_en_proporcion()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();
        IReadOnlyList<HeatGroup> groups = vm.Groups;

        StaRunner.Run(() =>
        {
            var map = new Treemap { Groups = groups, Width = 1000, Height = 600 };
            map.Measure(new Size(1000, 600));
            map.Arrange(new Rect(0, 0, 1000, 600));
            map.UpdateLayout();

            var placed = map.Placed;
            placed.Should().NotBeEmpty("el control tiene que haber dibujado algo");

            var cells = placed.Where(p => !p.IsGroup).ToList();
            cells.Should().HaveCount(4, "las cuatro unidades");

            for (int i = 0; i < cells.Count; i++)
            {
                for (int j = i + 1; j < cells.Count; j++)
                {
                    cells[i].Rect.Overlaps(cells[j].Rect).Should().BeFalse();
                }
            }

            // Dentro de Core, 3000 líneas ocupan mucho más que 200. La proporción exacta se
            // comprueba en TreemapLayoutTests; aquí basta el orden, que es lo que el control
            // podría estropear con sus márgenes.
            double virgen = Area(cells, "Core/Virgen.cs");
            double sucia = Area(cells, "Core/Sucia.cs");
            virgen.Should().BeGreaterThan(sucia * 5);
        });
    }

    private static double Area(IReadOnlyList<(TreemapRect Rect, object? Payload, bool IsGroup)> cells, string path)
        => cells.Single(c => c.Payload is HeatUnit u && u.Path == path).Rect.Area;

    // ============================================ Celdas diminutas: «+N unidades»

    /// <summary>
    /// Con muchas unidades pequeñas, las que no llegarían a verse se funden en UNA celda con su
    /// cuenta. Cuarenta rectángulos de tres píxeles no son cuarenta datos: son ruido con borde.
    /// </summary>
    [Fact]
    public async Task Las_celdas_que_no_llegarian_a_verse_se_funden_en_una_sola()
    {
        Crumbs();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();
        IReadOnlyList<HeatGroup> groups = vm.Groups;
        Func<IReadOnlyList<HeatCell>, HeatCell>? factory = vm.ClusterFactory;

        StaRunner.Run(() =>
        {
            var map = new Treemap { Groups = groups, ClusterFactory = factory, Width = 300, Height = 200 };
            map.Measure(new Size(300, 200));
            map.Arrange(new Rect(0, 0, 300, 200));
            map.UpdateLayout();

            var cells = map.Placed.Where(p => !p.IsGroup).ToList();
            cells.Should().HaveCountLessThan(41, "las diminutas se han fundido");
            cells.Should().ContainSingle(c => c.Payload is HeatCluster);

            var cluster = (HeatCluster)cells.Single(c => c.Payload is HeatCluster).Payload!;
            cluster.Units.Should().HaveCountGreaterThan(2);
            cells.Single(c => c.Payload is HeatCluster).Rect.Area.Should().BeGreaterThan(0);
        });
    }

    /// <summary>
    /// Con sitio de sobra no se funde nada: agrupar es una respuesta al tamaño de la celda, no una
    /// regla sobre el número de unidades.
    /// </summary>
    [Fact]
    public async Task Con_sitio_de_sobra_no_se_funde_nada()
    {
        Crumbs();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();
        IReadOnlyList<HeatGroup> groups = vm.Groups;
        Func<IReadOnlyList<HeatCell>, HeatCell>? factory = vm.ClusterFactory;

        StaRunner.Run(() =>
        {
            var map = new Treemap { Groups = groups, ClusterFactory = factory, Width = 2400, Height = 1400 };
            map.Measure(new Size(2400, 1400));
            map.Arrange(new Rect(0, 0, 2400, 1400));
            map.UpdateLayout();

            map.Placed.Should().NotContain(p => p.Payload is HeatCluster);
        });
    }

    /// <summary>
    /// <b>El agregado toma el color de su PEOR unidad.</b> Con la media, una clase pequeña y
    /// podrida desaparecería dentro de cuarenta tranquilas — el mapa escondería justo lo que
    /// existe para enseñar. Exagerar en una celda que dice «+N unidades» invita a ampliar, que es
    /// lo que hay que hacer con ella.
    /// </summary>
    [Fact]
    public async Task El_agregado_se_pinta_con_su_peor_unidad_y_no_con_la_media()
    {
        Crumbs(hotOne: true);
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        var tiny = vm.Groups.Single().Cells.Where(c => c.Label != "Gorda.cs").ToList();
        HeatCell cluster = vm.ClusterFactory!(tiny);

        cluster.Label.Should().Be($"+{tiny.Count} unidades");
        cluster.Weight.Should().Be(tiny.Sum(c => c.Weight), "el área es la suma de lo que absorbe");
        cluster.Qualified.Should().BeTrue("el relleno de N unidades nunca lo cuenta todo");
        cluster.Tooltip.Should().Contain("La peor: Ardiendo.cs");

        HeatCell worst = tiny.Single(c => c.Label == "Ardiendo.cs");
        Hex(cluster.Fill).Should().Be(Hex(worst.Fill));
        Hex(cluster.Ink).Should().Be(Hex(worst.Ink));
    }

    /// <summary>Y sin una sola auditada, el agregado tampoco inventa un color.</summary>
    [Fact]
    public async Task Un_agregado_de_unidades_sin_auditar_sigue_sin_color()
    {
        Crumbs(audited: false);
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        HeatCell cluster = vm.ClusterFactory!(vm.Groups.Single().Cells);

        cluster.Fill.Should().BeNull("gris tramado: la honestidad no cambia por agrupar");
        cluster.Tooltip.Should().Contain("Ninguna está auditada");
    }

    // ============================================ La tinta de cada celda

    /// <summary>
    /// La tinta sale del PASO y no de una fórmula sobre el color. La fórmula elegía blanco sobre
    /// el coral del paso 4 —donde el negro contrasta el doble—, así que la celda más caliente del
    /// mapa era la peor leída.
    /// </summary>
    [Fact]
    public async Task Cada_celda_lleva_la_tinta_de_su_paso()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        HeatCell dirty = vm.Groups.Single(g => g.Name == "Core").Cells.Single(c => c.Label == "Sucia.cs");

        // 15 puntos sobre 0,2 KLOC = 75 → paso 4, que en tema oscuro es coral con tinta NEGRA.
        Hex(dirty.Fill).Should().Be(Hex(HeatBrushes.Solid(DensityScale.Density[3].Dark)));
        Hex(dirty.Ink).Should().Be(Hex(HeatBrushes.Solid(DensityScale.Density[3].DarkInk)));

        // La celda gris no trae tinta: usa la del tema, que es la que contrasta con el tramado.
        vm.Groups.Single(g => g.Name == "Core").Cells.Single(c => c.Label == "Virgen.cs")
            .Ink.Should().BeNull();
    }

    // ============================================ La tabla

    /// <summary>
    /// Cada fila lleva su franja de densidad: el MISMO color que su celda del mapa. Sin ella,
    /// distinguir una fila auditada de una que no lo está exigía leerse la columna «Estado»
    /// palabra por palabra, novecientas veces.
    /// </summary>
    [Fact]
    public async Task Cada_fila_de_la_tabla_lleva_la_franja_de_su_densidad()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        HeatRow dirty = vm.Rows.Single(r => r.Unit == "Sucia.cs");
        HeatRow untouched = vm.Rows.Single(r => r.Unit == "Virgen.cs");

        Hex(dirty.Stripe).Should().Be(Hex(HeatBrushes.Solid(DensityScale.Density[3].Dark)));
        untouched.Stripe.Should().BeOfType<DrawingBrush>("sin auditar, la franja va tramada como la leyenda");
    }

    /// <summary>La franja acompaña a la métrica: cambiar de pregunta repinta también la tabla.</summary>
    [Fact]
    public async Task La_franja_de_la_tabla_sigue_a_la_metrica_elegida()
    {
        Seed();
        HeatmapViewModel vm = Vm();
        await vm.LoadAsync();

        vm.SelectedMetric = vm.MetricOptions.Single(o => o.Metric == HeatMetric.Deuda);

        string debt = Hex(vm.Rows.Single(r => r.Unit == "Sucia.cs").Stripe);
        DensityScale.Debt.Select(st => Hex(HeatBrushes.Solid(st.Dark))).Should().Contain(debt);
    }

    /// <summary>
    /// El texto de la tabla se acorta por el MEDIO y avisa con su tooltip. «No auditada · excluida
    /// por ta…» era lo que salía en la captura del usuario.
    /// </summary>
    [Fact]
    public void El_texto_de_una_celda_estrecha_se_acorta_por_el_medio_y_avisa()
    {
        StaRunner.Run(() =>
        {
            var cell = new MiddleEllipsisText
            {
                Text = "No auditada · excluida por tamaño",
                FontSize = 12,
                Width = 90,
                Height = 20,
            };
            cell.Measure(new Size(90, 20));
            cell.Arrange(new Rect(0, 0, 90, 20));
            cell.UpdateLayout();

            cell.Rendered.Should().NotBeNull();
            cell.Rendered!.Should().Contain("…");
            cell.Rendered.Should().EndWith("tamaño", "el final es justo lo que la captura se comía");
            cell.ToolTip.Should().Be("No auditada · excluida por tamaño");
        });
    }

    /// <summary>Y con sitio de sobra no se toca el texto ni se pone un tooltip que sobra.</summary>
    [Fact]
    public void Con_sitio_de_sobra_el_texto_va_entero_y_sin_tooltip()
    {
        StaRunner.Run(() =>
        {
            var cell = new MiddleEllipsisText { Text = "Auditada", FontSize = 12, Width = 300, Height = 20 };
            cell.Measure(new Size(300, 20));
            cell.Arrange(new Rect(0, 0, 300, 20));
            cell.UpdateLayout();

            cell.Rendered.Should().Be("Auditada");
            cell.ToolTip.Should().BeNull("un tooltip que repite lo que ya se lee enseña a ignorarlos");
        });
    }

    // ============================================ El inventario comparte la rampa

    /// <summary>
    /// La franja del inventario sale de la MISMA consulta y de la MISMA rampa que el mapa: es el
    /// mismo dato, y dos cálculos parecidos en dos vistas es cómo acaban discrepando.
    /// </summary>
    [Fact]
    public async Task El_inventario_pinta_la_franja_con_la_misma_rampa_que_el_mapa()
    {
        Seed();
        InventoryViewModel inventory = TestInventory();
        inventory.SetApp("app");
        await inventory.LoadAsync();

        var units = inventory.Modules.SelectMany(m => m.Units).ToList();
        UnitNode dirty = units.Single(u => u.FileName == "Sucia.cs");
        UnitNode untouched = units.Single(u => u.FileName == "Virgen.cs");

        Hex(dirty.DensityBrush).Should().Be(Hex(HeatBrushes.Solid(DensityScale.Density[3].Dark)));
        dirty.DensityTooltip.Should().Contain("75");

        Hex(untouched.DensityBrush).Should().Be(Hex(HeatBrushes.Solid(DensityScale.Unknown.Dark)));
        untouched.DensityTooltip.Should().Contain("DESCONOCIDA");
    }

    // ============================================ El XAML

    /// <summary>
    /// La tabla equivalente existe en la vista y su interruptor está a la vista, no escondido en
    /// un menú: es la alternativa obligatoria, no una preferencia avanzada.
    /// </summary>
    [Fact]
    public void La_vista_ofrece_la_tabla_y_la_exportacion()
    {
        string xaml = File.ReadAllText(Path.Combine(ViewsDir(), "HeatmapView.xaml"));
        string markup = Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        markup.Should().Contain("Ver como tabla");
        markup.Should().Contain("Exportar imagen");
        markup.Should().Contain("ExportImageCommand");
        markup.Should().Contain("SortByCommand");
        markup.Should().Contain("ZoomOutCommand");
        markup.Should().Contain("controls:Treemap");
    }

    /// <summary>
    /// <b>UN SOLO SCROLL.</b> Ningún <c>ScrollViewer</c> vive dentro de otro. Anidados salían dos
    /// barras solapadas y la rueda no sabía a quién obedecer, que es lo que el usuario reportó
    /// como «desplazarse es engorroso». Se comprueba sobre el ÁRBOL del XAML y no con una búsqueda
    /// de texto: el anidamiento es una relación entre elementos, no una cadena de caracteres.
    /// </summary>
    [Fact]
    public void Ningun_scroll_vive_dentro_de_otro()
    {
        XElement root = XDocument.Load(Path.Combine(ViewsDir(), "HeatmapView.xaml")).Root!;

        var scrolls = root.Descendants().Where(e => e.Name.LocalName == "ScrollViewer").ToList();
        scrolls.Should().NotBeEmpty("algo tiene que desplazarse");

        foreach (XElement scroll in scrolls)
        {
            scroll.Ancestors().Should().NotContain(a => a.Name.LocalName == "ScrollViewer",
                "un scroll dentro de otro deja dos barras y una rueda que no sabe a quién obedecer");
        }

        // Y la lista de 925 filas está DENTRO de un scroll, no colgando del alto de la página.
        XElement rows = root.Descendants()
            .Single(e => e.Name.LocalName == "ItemsControl"
                         && (string?)e.Attribute("ItemsSource") == "{Binding Rows}");

        rows.Ancestors().Should().ContainSingle(a => a.Name.LocalName == "ScrollViewer");
    }

    /// <summary>
    /// La cabecera de una columna se alinea como sus celdas. El desajuste —cabecera a la
    /// izquierda, cifra a la derecha— es lo que hacía que la tabla no pareciera una tabla aunque
    /// las columnas estuvieran perfectamente puestas.
    /// </summary>
    [Fact]
    public void Las_cabeceras_numericas_se_alinean_como_sus_numeros()
    {
        XElement root = XDocument.Load(Path.Combine(ViewsDir(), "HeatmapView.xaml")).Root!;

        Style(root, "SortHeaderRight").Should().Contain(("HorizontalContentAlignment", "Right"));
        Style(root, "SortHeader").Should().Contain(("HorizontalContentAlignment", "Left"));

        // Los números, a la derecha y con cifras de ancho fijo: con las proporcionales de serie la
        // columna baila fila a fila aunque esté alineada.
        var number = Style(root, "Number");
        number.Should().Contain(("HorizontalAlignment", "Right"));
        number.Should().Contain(("Typography.NumeralAlignment", "Tabular"));

        // Y las columnas numéricas usan la cabecera derecha, no la de texto.
        var headers = root.Descendants()
            .Where(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Content") is { } c
                        && c.Contains("Header", StringComparison.Ordinal))
            .ToList();

        foreach (string numeric in new[] { "Loc", "Critica", "Alta", "Media", "Baja", "Debt", "Density" })
        {
            headers.Should().Contain(
                e => (string?)e.Attribute("Content") == $"{{Binding Header{numeric}}}"
                     && (string?)e.Attribute("Style") == "{StaticResource SortHeaderRight}",
                $"la cabecera de {numeric} tiene que alinearse con sus cifras");
        }
    }

    /// <summary>
    /// Ni un texto de la tabla se recorta en seco: los tres que pueden no caber —módulo, unidad y
    /// estado— van con el control que acorta por el medio y pone su tooltip.
    /// </summary>
    [Fact]
    public void Los_textos_largos_de_la_tabla_se_acortan_por_el_medio()
    {
        XElement root = XDocument.Load(Path.Combine(ViewsDir(), "HeatmapView.xaml")).Root!;

        var fitted = root.Descendants()
            .Where(e => e.Name.LocalName == "MiddleEllipsisText")
            .Select(e => (string?)e.Attribute("Text"))
            .ToList();

        fitted.Should().Contain("{Binding Module}");
        fitted.Should().Contain("{Binding Unit}");
        fitted.Should().Contain("{Binding State}");

        // Y ninguna celda de la tabla se queda con el recorte trasero de serie de WPF.
        root.Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Should().NotContain(e => (string?)e.Attribute("TextTrimming") == "CharacterEllipsis");
    }

    /// <summary>Los pares (propiedad, valor) que fija un estilo del XAML, por su clave.</summary>
    private static IReadOnlyList<(string Property, string Value)> Style(XElement root, string key)
        => root.Descendants()
            .Single(e => e.Name.LocalName == "Style"
                         && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == key))
            .Descendants()
            .Where(e => e.Name.LocalName == "Setter")
            .Select(e => ((string)e.Attribute("Property")!, (string)e.Attribute("Value")!))
            .ToList();

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
            // Un fichero todavía abierto no puede tumbar la serie.
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Un inventario de verdad, con todo lo que necesita apagado: ningún diálogo, ningún agente y
    /// ninguna ventana. Solo se le pregunta por lo que el mapa le deja hecho.
    /// </summary>
    private InventoryViewModel TestInventory()
        => new(
            _hub,
            _ulids,
            new NavigationService(new NoServices()),
            new LiveSessionService(
                () => throw new NotSupportedException("ningún test del mapa lanza una sesión"),
                new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>()),
                new OpenSessionStore(_paths)),
            _settings,
            new CostEstimator(_hub),
            new NeverConfirms(),
            new GroupExpansionMemory(),
            _toasts,
            TestFactory.Links(_hub, _paths),
            TestFactory.LinkFlow(_hub, _paths, _toasts),
            new InventoryRescanService(_hub, new InventoryScanner()),
            new GovernanceService(_hub, _ulids),
            new TestFactory.NoPatternSilencesDialog(),
            new DirectiveService(_hub, new DirectiveScanner(), _ulids),
            new TestFactory.NoDirectivesDialog(),
            new HeatmapQuery(_hub));

    /// <summary>El diálogo de lanzamiento que siempre dice que no: aquí no se lanza nada.</summary>
    private sealed class NeverConfirms : IAuditLaunchConfirmer
    {
        public bool Confirm(AuditLaunchConfirmation confirmation) => false;
    }
}
