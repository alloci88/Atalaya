using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Una opción del selector de rango temporal.</summary>
public sealed record RangeOption(MetricsRange Range, string Label);

/// <summary>Una entrada de leyenda: el color y a que app pertenece. Nunca un color solo.</summary>
public sealed record LegendItem(string Name, Brush Brush, bool Dashed);

/// <summary>
/// Un chip de severidad del tile de activos (F5.9). Trae ya su fondo calculado: el mismo color de la
/// severidad, atenuado, como en las tarjetas del portafolio.
/// </summary>
public sealed record MetricsSeverityChip(string Label, int Count, Brush Foreground, Brush Background);

/// <summary>Un rosco de cobertura con todo lo que la vista escribe alrededor.</summary>
public sealed record CoverageCard(
    string Slug,
    string Name,
    string CycleLabel,
    string PctText,
    string Detail,
    IReadOnlyList<DonutSegment> Segments,
    bool HasData);

/// <summary>Una línea del registro de operaciones, ya escrita.</summary>
public sealed record SessionLine(
    string SessionId,
    string Slug,
    string AppName,
    Brush AppBrush,
    string When,
    string By,
    string Units,
    string Findings,
    string Cost,
    bool HasReport);

/// <summary>
/// El panel de mando de F5.9: filtros, cuatro cifras grandes y cuatro gráficas.
/// <para>
/// <b>Qué responde.</b> Las tres preguntas del equipo y del jefe: cómo estamos (activos y
/// cobertura), avanzamos (flujo de hallazgos) y cuánto cuesta (coste en el tiempo y registro de
/// sesiones). Lo que no responde a ninguna de las tres no esta.
/// </para>
/// <para>
/// <b>Qué se retiró.</b> El «% criterio» —que mide la calidad del AUDITOR, no el estado del
/// código— vive donde se puede interpretar, que es el informe de cada sesión. Y el «tiempo medio
/// a resolucion» no vuelve hasta que haya resoluciones reales que promediar: un tile con «0,0
/// días» sobre cero resoluciones es un número enganoso, no un dato.
/// </para>
/// <para>
/// <b>El tema.</b> Los colores de serie tienen un paso para claro y otro para oscuro y se eligen
/// al cargar la página, leyendo el ajuste que la propia aplicación usa para aplicar el tema.
/// Cambiar el tema exige ir a Ajustes, y volver a Métricas vuelve a cargar la página: no hace
/// falta escuchar ningún evento para que los colores acompanen al fondo.
/// </para>
/// </summary>
public sealed partial class MetricsViewModel : ViewModelBase
{
    private readonly MetricsQuery _metrics;
    private readonly NavigationService _navigation;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly IFileOpener _opener;
    private readonly ToastCenter _toasts;

    /// <summary>Lo último agregado. El toggle de acumulado se sirve de aquí sin releer el hub.</summary>
    private MetricsDashboard? _dashboard;

    /// <summary>Mientras se rellenan los selectores, un cambio de seleccion no re-agrega nada.</summary>
    private bool _binding;

    private bool _dark = true;

    public MetricsViewModel(
        MetricsQuery metrics,
        NavigationService navigation,
        SettingsService settings,
        HubContext hub,
        IFileOpener opener,
        ToastCenter toasts)
    {
        _metrics = metrics;
        _navigation = navigation;
        _settings = settings;
        _hub = hub;
        _opener = opener;
        _toasts = toasts;

        RangeOptions = new ObservableCollection<RangeOption>
        {
            new(MetricsRange.Weeks4, "4 semanas"),
            new(MetricsRange.Weeks8, "8 semanas"),
            new(MetricsRange.Weeks26, "26 semanas"),
            new(MetricsRange.All, "Todo"),
        };
        _selectedRange = RangeOptions[1];
    }

    public override string Title => "Métricas";

    // ---------- Fila de filtros ----------

    public ObservableCollection<AppOption> AppOptions { get; } = new();

    public ObservableCollection<RangeOption> RangeOptions { get; }

    [ObservableProperty] private AppOption? _selectedApp;

    [ObservableProperty] private RangeOption _selectedRange;

    /// <summary>
    /// La curva de mercado: el coste del periodo sumado cubo a cubo. Es una forma de LEER los
    /// mismos datos, no otros datos, así que el toggle no vuelve a tocar el hub.
    /// </summary>
    [ObservableProperty] private bool _cumulative;

    partial void OnSelectedAppChanged(AppOption? value) => Reload();

    partial void OnSelectedRangeChanged(RangeOption value) => Reload();

    partial void OnCumulativeChanged(bool value) => RebuildCostChart();

    private void Reload()
    {
        if (!_binding)
        {
            _ = LoadAsync();
        }
    }

    // ---------- Estado de la página ----------

    /// <summary>Ni un hallazgo, ni una sesión, ni un inventario: el panel entero lo dice.</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>«Del 1 de julio al 26 de agosto de 2026 · agregado por semana».</summary>
    [ObservableProperty] private string _periodLabel = string.Empty;

    // ---------- Tiles ----------

    [ObservableProperty] private int _activeTotal;

    public ObservableCollection<MetricsSeverityChip> ActiveChips { get; } = new();

    [ObservableProperty] private int _resolvedInPeriod;

    [ObservableProperty] private string _resolvedDelta = string.Empty;

    [ObservableProperty] private Brush _resolvedDeltaBrush = Brushes.Gray;

    [ObservableProperty] private string _costTotal = Unknown;

    [ObservableProperty] private string _costUnit = CostEstimator.DefaultCostUnit;

    [ObservableProperty] private string _costPerUnit = string.Empty;

    [ObservableProperty] private string _cyclePct = Unknown;

    [ObservableProperty] private string _cycleDetail = string.Empty;

    /// <summary>
    /// Lo que se escribe cuando no hay datos suficientes. NUNCA un cero con formato: «0,0 días»
    /// sobre cero resoluciones y «0 unidades SDK» sobre cero sesiones se leen como medidas, y no
    /// lo son.
    /// </summary>
    public const string Unknown = "—";

    // ---------- Gráfica 1: coste en el tiempo ----------

    [ObservableProperty] private IReadOnlyList<ChartSeries> _costSeries = Array.Empty<ChartSeries>();

    [ObservableProperty] private IReadOnlyList<string> _costLabels = Array.Empty<string>();

    public ObservableCollection<LegendItem> CostLegend { get; } = new();

    /// <summary>Leyenda siempre que haya dos series o más (regla del §2). Con una, sobra.</summary>
    [ObservableProperty] private bool _showCostLegend;

    /// <summary>Sin una sola sesión con coste declarado no hay gráfica que dibujar, y se dice.</summary>
    [ObservableProperty] private bool _hasCost;

    // ---------- Gráfica 2: cobertura por aplicación ----------

    public ObservableCollection<CoverageCard> Coverage { get; } = new();

    /// <summary>Con una sola app, un rosco grande. Con varias, la fila.</summary>
    [ObservableProperty] private double _donutSize = 108;

    [ObservableProperty] private bool _hasCoverage;

    // ---------- Gráfica 3: flujo de hallazgos ----------

    [ObservableProperty] private IReadOnlyList<ChartSeries> _flowSeries = Array.Empty<ChartSeries>();

    [ObservableProperty] private IReadOnlyList<string> _flowLabels = Array.Empty<string>();

    public ObservableCollection<LegendItem> FlowLegend { get; } = new();

    /// <inheritdoc cref="ShowCostLegend"/>
    [ObservableProperty] private bool _showFlowLegend;

    [ObservableProperty] private bool _hasFlow;

    // ---------- Gráfica 4: actividad de sesiones ----------

    public ObservableCollection<SessionLine> Sessions { get; } = new();

    [ObservableProperty] private bool _hasSessions;

    // ---------- Carga ----------

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            _dark = !string.Equals(_settings.Current.Theme, "light", StringComparison.OrdinalIgnoreCase);
            var filter = new MetricsFilter(SelectedApp?.Slug, SelectedRange.Range);

            // Agregar recorre todos los hallazgos y todas las sesiones de todas las apps. Fuera
            // del hilo de UI: el panel no puede congelar la ventana mientras cuenta.
            MetricsDashboard dashboard = await Task.Run(() => _metrics.Build(filter));
            _dashboard = dashboard;

            SyncAppOptions(dashboard);
            ApplyTiles(dashboard);
            RebuildCostChart();
            ApplyCoverage(dashboard);
            ApplyFlow(dashboard);
            ApplySessions(dashboard);

            PeriodLabel = DescribePeriod(dashboard);
            IsEmpty = dashboard.IsEmpty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// El selector se rellena con las apps del hub sin disparar una segunda agregación: cambiar
    /// la lista mueve <see cref="SelectedApp"/>, y sin esta guarda cada carga provocaria otra.
    /// </summary>
    private void SyncAppOptions(MetricsDashboard dashboard)
    {
        string? current = SelectedApp?.Slug;
        _binding = true;
        try
        {
            AppOptions.Clear();
            foreach (AppOption option in dashboard.AppOptions)
            {
                AppOptions.Add(option);
            }

            SelectedApp = AppOptions.FirstOrDefault(o => o.Slug == current) ?? AppOptions.FirstOrDefault();
        }
        finally
        {
            _binding = false;
        }
    }

    private void ApplyTiles(MetricsDashboard d)
    {
        ActiveTotal = d.ActiveTotal;
        ActiveChips.Clear();
        ActiveChips.Add(Chip(Severity.Critica, d.Active.Critica));
        ActiveChips.Add(Chip(Severity.Alta, d.Active.Alta));
        ActiveChips.Add(Chip(Severity.Media, d.Active.Media));
        ActiveChips.Add(Chip(Severity.Baja, d.Active.Baja));

        ResolvedInPeriod = d.ResolvedInPeriod;
        int delta = d.ResolvedDelta;
        ResolvedDelta = delta == 0
            ? "igual que el periodo anterior"
            : $"{(delta > 0 ? "▲" : "▼")} {Math.Abs(delta)} vs periodo anterior";
        ResolvedDeltaBrush = delta switch
        {
            > 0 => Brush("#3FB950"),
            < 0 => Brush("#E0A030"),
            _ => Brush(_dark ? "#9A9A9A" : "#6B6B6B"),
        };

        CostUnit = d.CostUnit;
        CostTotal = d.CostInPeriod is { } c ? c.ToString("0.##", CultureInfo.CurrentCulture) : Unknown;
        CostPerUnit = d.CostInPeriod is null
            ? "Se activará cuando alguna sesión registre coste"
            : d.CostPerAuditedUnit is { } per
                ? $"~{per.ToString("0.##", CultureInfo.CurrentCulture)} por unidad auditada "
                  + $"({d.UnitsAuditedInPeriod} en el periodo)"
                : "Sin unidades auditadas en el periodo";

        CyclePct = d.HasCycleData ? d.CyclePct.ToString("0%", CultureInfo.CurrentCulture) : Unknown;
        CycleDetail = d.HasCycleData
            ? $"{d.CycleAudited} de {d.CycleAudited + d.CyclePending} unidades auditables"
              + (d.CycleLarge > 0 ? $" · {d.CycleLarge} grandes excluidas" : string.Empty)
            : "Se activará cuando haya un inventario del ciclo";
    }

    /// <summary>
    /// La gráfica de coste. Se reconstruye desde el agregado ya en memoria, así que el toggle
    /// «Acumulado» es instantaneo y no vuelve a leer un solo fichero.
    /// </summary>
    private void RebuildCostChart()
    {
        CostLegend.Clear();
        if (_dashboard is not { } d)
        {
            CostSeries = Array.Empty<ChartSeries>();
            CostLabels = Array.Empty<string>();
            HasCost = false;
            return;
        }

        HasCost = d.HasCost && d.CostSeries.Count > 0;
        CostLabels = d.Cost.Select(p => p.Label).ToList();

        var series = new List<ChartSeries>();
        foreach (string key in d.CostSeries)
        {
            bool others = key == MetricsDashboard.OthersSlug;
            Brush brush = SeriesBrush(key, d);
            var values = new List<double>(d.Cost.Count);
            double running = 0;
            foreach (CostPoint point in d.Cost)
            {
                double value = (double)point.Of(key);
                running += value;
                values.Add(Cumulative ? running : value);
            }

            string name = d.NameOf(key);
            series.Add(new ChartSeries(key, name, brush, values, ChartSeriesKind.Line, others));
            CostLegend.Add(new LegendItem(name, brush, others));
        }

        CostSeries = series;
        ShowCostLegend = CostLegend.Count >= 2;
    }

    private void ApplyCoverage(MetricsDashboard d)
    {
        Coverage.Clear();
        var drawable = d.Coverage.Where(c => c.HasData).ToList();
        foreach (CoverageDonut donut in drawable)
        {
            Brush app = SeriesBrush(donut.Slug, d);
            var segments = new List<DonutSegment>
            {
                new("Auditadas", donut.Audited, app),
                new("Pendientes", donut.Pending, Brush(SeriesPalette.Pending.For(_dark))),
                new("Grandes (excluidas)", donut.Large, Brush(SeriesPalette.Large.For(_dark))),
            };

            Coverage.Add(new CoverageCard(
                donut.Slug,
                donut.Name,
                $"Ciclo {donut.Cycle}",
                donut.Pct.ToString("0%", CultureInfo.CurrentCulture),
                $"{donut.Audited} auditadas · {donut.Pending} pendientes"
                + (donut.Large > 0 ? $" · {donut.Large} grandes" : string.Empty),
                segments,
                true));
        }

        HasCoverage = Coverage.Count > 0;
        DonutSize = Coverage.Count == 1 ? 180 : 108;
    }

    private void ApplyFlow(MetricsDashboard d)
    {
        FlowLegend.Clear();
        var nuevos = d.Flow.Select(b => (double)b.New).ToList();
        var resueltos = d.Flow.Select(b => (double)b.Resolved).ToList();
        var activos = d.Flow.Select(b => (double)b.ActiveAtEnd).ToList();

        HasFlow = nuevos.Concat(resueltos).Concat(activos).Any(v => v > 0);
        FlowLabels = d.Flow.Select(b => b.Label).ToList();

        // Los tres son CONTEOS de hallazgos: comparten el único eje. Poner los activos en un eje
        // propio dejaría elegir la escala con la que se cruzan las barras y la línea, que es
        // exactamente la mentira que la regla del §2 prohibe.
        Brush entran = Brush(_dark ? "#C88BE8" : "#8E44AD");
        Brush salen = Brush(_dark ? "#5CD6A0" : "#1E8E5A");
        Brush vivos = Brush(_dark ? "#93AEC0" : "#4E6472");

        FlowSeries = new List<ChartSeries>
        {
            new("nuevos", "Nuevos", entran, nuevos, ChartSeriesKind.Bar),
            new("resueltos", "Resueltos", salen, resueltos, ChartSeriesKind.Bar),
            new("activos", "Activos al cierre", vivos, activos),
        };

        foreach (ChartSeries s in FlowSeries)
        {
            FlowLegend.Add(new LegendItem(s.Name, s.Stroke, false));
        }

        ShowFlowLegend = FlowLegend.Count >= 2;
    }

    private void ApplySessions(MetricsDashboard d)
    {
        Sessions.Clear();
        foreach (SessionRow row in d.Sessions)
        {
            string findings = row.New == 0 && row.Resolved == 0
                ? "sin cambios"
                : $"+{row.New} / -{row.Resolved}";

            Sessions.Add(new SessionLine(
                row.SessionId,
                row.Slug,
                row.AppName,
                SeriesBrush(row.Slug, d),
                row.When.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture),
                row.By,
                row.Units == 1 ? "1 unidad" : $"{row.Units} unidades",
                findings,
                row.Cost is { } c ? $"{c.ToString("0.##", CultureInfo.CurrentCulture)} {row.CostUnit}" : Unknown,
                File.Exists(ReportPathFor(row.Slug, row.SessionId))));
        }

        HasSessions = Sessions.Count > 0;
    }

    /// <summary>
    /// «Del 1 jul al 26 ago 2026 · agregado por semana». El periodo y su grano se ESCRIBEN: sin
    /// ellos, dos gráficas iguales con rangos distintos se leen igual.
    /// </summary>
    private static string DescribePeriod(MetricsDashboard d)
    {
        string grain = d.Granularity switch
        {
            MetricsGranularity.Diaria => "por día",
            MetricsGranularity.Mensual => "por mes",
            _ => "por semana",
        };

        string from = d.From.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
        string to = d.To.AddDays(-1).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        return $"Del {from} al {to} · agregado {grain}";
    }

    // ---------- Gestos ----------

    /// <summary>Un clic en un rosco lleva al inventario de esa app, que es lo que el rosco cuenta.</summary>
    [RelayCommand]
    private Task OpenInventory(CoverageCard? card)
        => card is null
            ? Task.CompletedTask
            : _navigation.NavigateToAsync<InventoryViewModel>(vm => vm.SetApp(card.Slug));

    /// <summary>Un clic en una sesión abre su informe, que es el detalle de esa línea.</summary>
    [RelayCommand]
    private void OpenSession(SessionLine? line)
    {
        if (line is null)
        {
            return;
        }

        if (!_opener.Open(ReportPathFor(line.Slug, line.SessionId)))
        {
            _toasts.Show("Esta sesión no dejó informe en disco.");
        }
    }

    /// <summary>Donde vive el informe de una sesión. Publico para poder comprobarlo sin abrir nada.</summary>
    public string ReportPathFor(string slug, string sessionId) => _hub.HubPaths.ReportFile(slug, sessionId);

    // ---------- Colores ----------

    /// <summary>
    /// Un chip por severidad. «Crít» va abreviado, igual que en la tarjeta del portafolio: son
    /// cuatro chips en el ancho de un cuarto de fila, y «Crítica» los parte en dos líneas.
    /// </summary>
    private static MetricsSeverityChip Chip(Severity severity, int count)
    {
        string hex = SeverityPalette.Hex(severity);
        string label = severity == Severity.Critica ? "Crít" : SeverityNames.Display(severity);
        return new MetricsSeverityChip(label, count, Brush(hex), Brush(hex, 0x38));
    }

    /// <summary>
    /// El color de una app: el del reparto del portafolio, en su paso para el tema vigente. Una
    /// serie agrupada («Otras») va en gris y a trazos, que no es el color de ninguna app.
    /// </summary>
    private Brush SeriesBrush(string slug, MetricsDashboard d)
        => Brush(slug == MetricsDashboard.OthersSlug
            ? SeriesPalette.Others.For(_dark)
            : SeriesPalette.For(slug, d.Palette).For(_dark));

    private static Brush Brush(string hex, byte alpha = 0xFF)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
