using System.Text.RegularExpressions;
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
    /// Un solo tono, cinco pasos, del claro al oscuro. Se comprueba sobre la LUMINANCIA porque es
    /// la propiedad que hace que una escala secuencial se ordene sola: un arcoíris tiene cinco
    /// tonos igual de claros y no se puede leer sin mirar la leyenda.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void La_escala_es_de_un_solo_tono_y_ordena_por_luminancia(bool dark)
    {
        var steps = DensityScale.Density;
        steps.Should().HaveCount(5);

        var luminance = steps
            .Select(s => (Color)ColorConverter.ConvertFromString(s.For(dark)))
            .Select(c => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B)
            .ToList();

        // Tema claro: de claro a oscuro. Tema oscuro: al revés — y no por inversión automática,
        // sino porque cada rampa se eligió para su fondo.
        var ordered = dark ? luminance.OrderBy(v => v) : luminance.OrderByDescending(v => v);
        luminance.Should().Equal(ordered, "la escala tiene que ordenarse sola");

        // Y el tono es el mismo: los cinco pasos tienen el azul por encima del verde (familia
        // violeta). Un semáforo rojo-ámbar-verde rompería esto en el primer paso.
        foreach (Color c in steps.Select(s => (Color)ColorConverter.ConvertFromString(s.For(dark))))
        {
            c.B.Should().BeGreaterThan(c.G, "los cinco pasos son del mismo tono");
        }
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
            new TestFactory.NoDirectivesDialog());

    /// <summary>El diálogo de lanzamiento que siempre dice que no: aquí no se lanza nada.</summary>
    private sealed class NeverConfirms : IAuditLaunchConfirmer
    {
        public bool Confirm(AuditLaunchConfirmation confirmation) => false;
    }
}
