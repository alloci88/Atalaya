using System.Collections.ObjectModel;
using System.Windows.Media;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Domain.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Una opción del selector de métrica del color.</summary>
public sealed record HeatMetricOption(HeatMetric Metric, string Label, string Hint);

/// <summary>
/// Una fila de la tabla equivalente (F10 §2). <b>Las mismas columnas que el mapa</b>, en texto y
/// ordenables: el color no puede ser nunca el único canal.
/// </summary>
/// <param name="Stripe">
/// La franja de densidad de la izquierda: el MISMO color de la celda del mapa, o el gris tramado
/// cuando no se ha auditado. Sin ella, distinguir una fila auditada de una que no lo está dependía
/// de leerse la columna «Estado» entera, palabra por palabra, novecientas veces (F10.1 §3).
/// </param>
public sealed record HeatRow(
    string Module,
    string Unit,
    string Path,
    int Loc,
    int Critica,
    int Alta,
    int Media,
    int Baja,
    int Debt,
    double? Density,
    string State,
    string StateDetail,
    Brush Stripe,
    HeatUnit Source)
{
    /// <summary>«—» y no «0,0»: una densidad desconocida no es una densidad medida.</summary>
    public string DensityText => Density is { } d ? d.ToString("0.#", AppCulture.Display) : Unknown;

    public string LocText => Loc.ToString("N0", AppCulture.Display);

    /// <inheritdoc cref="MetricsViewModel.Unknown"/>
    public const string Unknown = "—";
}

/// <summary>
/// El puñado de unidades que el mapa funde en una sola celda por no llegar a verse (F10.1 §2).
/// Existe como tipo propio —y no como una lista suelta— porque es lo que viaja al pulsar: el
/// gesto tiene que saber que lo pulsado NO es una unidad.
/// </summary>
public sealed record HeatCluster(string Module, IReadOnlyList<HeatUnit> Units);

/// <summary>Cómo se ordenan las tarjetas de módulo (F10.2 §1).</summary>
public enum ModuleSort
{
    /// <summary>¿Por dónde miro ahora? Lo medido y lo ignorado, juntos. Ver <see cref="AttentionScore"/>.</summary>
    Atencion,

    Deuda,
    Densidad,
    Tamano,
    Cobertura,
    Nombre,
}

/// <summary>Una opción del selector de orden, con lo que responde escrito.</summary>
public sealed record ModuleSortOption(ModuleSort Sort, string Label, string Hint);

/// <summary>Por qué columna se ordena la tabla.</summary>
public enum HeatSort
{
    Module,
    Unit,
    Loc,
    Critica,
    Alta,
    Media,
    Baja,
    Debt,
    Density,
}

/// <summary>
/// V9 «Mapa de calor» (F10): la estructura de una aplicación —módulos y unidades— con el
/// <b>área</b> por tamaño y el <b>color</b> por densidad de deuda.
/// <para>
/// <b>Qué responde.</b> Dónde atacar. Una lista ordenada por número de hallazgos responde «quién
/// tiene más», que casi siempre es «quién es más grande»; normalizando por KLOC y dando el tamaño
/// al área, la vista separa las dos preguntas y las enseña a la vez.
/// </para>
/// <para>
/// <b>Lo que NO hace, y es lo importante.</b> No pinta de «limpio» lo que nadie ha mirado. Una
/// unidad sin auditar va en gris tramado, con su propia entrada en la leyenda, porque tratarla
/// como densidad 0 convertiría un inventario sin auditar —que es el estado normal de una
/// aplicación recién dada de alta— en un mapa tranquilizador y falso.
/// </para>
/// </summary>
public sealed partial class HeatmapViewModel : ViewModelBase
{
    private readonly HeatmapQuery _heat;
    private readonly NavigationService _navigation;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly ToastCenter _toasts;
    private readonly IFileSaver _saver;
    private readonly TimeProvider _time;

    private HeatmapView _view = HeatmapView.Empty;
    private bool _binding;
    private bool _dark = true;
    private string? _pendingSlug;

    public HeatmapViewModel(
        HeatmapQuery heat,
        NavigationService navigation,
        SettingsService settings,
        HubContext hub,
        ToastCenter toasts,
        IFileSaver saver,
        TimeProvider? time = null)
    {
        _heat = heat;
        _navigation = navigation;
        _settings = settings;
        _hub = hub;
        _toasts = toasts;
        _saver = saver;
        _time = time ?? TimeProvider.System;

        MetricOptions = new ObservableCollection<HeatMetricOption>
        {
            new(HeatMetric.Densidad, "Densidad", "Deuda por cada mil líneas: dónde está CONCENTRADO el problema."),
            new(HeatMetric.Deuda, "Deuda absoluta", "Deuda total sin normalizar: dónde HAY más problemas."),
        };
        _selectedMetric = MetricOptions[0];

        SortOptions = new ObservableCollection<ModuleSortOption>
        {
            new(ModuleSort.Atencion, "Atención",
                "¿Por dónde miro ahora? Junta lo que se ha medido que arde con lo que nadie ha "
                + "mirado: un módulo grande sin auditar sube, porque la ignorancia es riesgo."),
            new(ModuleSort.Deuda, "Deuda", "Puntos de deuda conocida, sin normalizar por tamaño."),
            new(ModuleSort.Densidad, "Densidad",
                "Deuda por KLOC de lo auditado. Los módulos sin auditar van al final: no tienen densidad."),
            new(ModuleSort.Tamano, "Tamaño", "Líneas de código, lo mire quien lo mire."),
            new(ModuleSort.Cobertura, "Cobertura", "De menos auditado a más: qué queda por mirar."),
            new(ModuleSort.Nombre, "Nombre", "Alfabético, para buscar uno concreto."),
        };
        _selectedSort = SortOptions[0];
    }

    public override string Title => "Mapa de calor";

    /// <summary>
    /// Si la única animación de la vista —la entrada al ampliar un módulo— debe correr. Sale del
    /// ajuste del SISTEMA: quien ha apagado las animaciones de Windows ya ha dicho que no quiere
    /// movimiento, y no tiene por qué volver a decirlo aquí.
    /// </summary>
    public static bool AnimationsEnabled => System.Windows.SystemParameters.ClientAreaAnimation;

    // ---------- Filtros ----------

    public ObservableCollection<AppOption> AppOptions { get; } = new();

    public ObservableCollection<HeatMetricOption> MetricOptions { get; }

    [ObservableProperty] private AppOption? _selectedApp;

    /// <summary>
    /// Densidad por defecto. «Dónde hay más problemas» y «dónde están más concentrados» son dos
    /// preguntas distintas, y la que sirve para decidir por dónde empezar es la segunda: sin
    /// normalizar, el mapa ordena por tamaño de fichero y eso ya lo dice el área.
    /// </summary>
    [ObservableProperty] private HeatMetricOption _selectedMetric;

    /// <summary>La alternativa accesible: las mismas columnas, en texto y ordenables.</summary>
    [ObservableProperty] private bool _tableMode;

    public ObservableCollection<ModuleSortOption> SortOptions { get; }

    /// <summary>
    /// Por defecto, <see cref="ModuleSort.Atencion"/>. Es la única que contesta algo útil cuando
    /// la cobertura es baja: ordenar por deuda con el 0,2 % auditado ordena por los dos ficheros
    /// que alguien miró.
    /// </summary>
    [ObservableProperty] private ModuleSortOption _selectedSort;

    /// <summary>
    /// Esconde lo que nadie ha auditado (F10.2 §4). Con cobertura baja es la única forma de que el
    /// mapa de densidad enseñe algo: deja a la vista SOLO lo que se sabe.
    /// <para>
    /// No es una forma de esconder el problema — el termómetro de arriba sigue contando la
    /// aplicación entera, con filtro y sin él.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _onlyAudited;

    partial void OnSelectedSortChanged(ModuleSortOption value) => Render();

    partial void OnOnlyAuditedChanged(bool value) => Render();

    partial void OnSelectedAppChanged(AppOption? value)
    {
        if (!_binding)
        {
            _zoom = null;
            _ = LoadAsync();
        }
    }

    partial void OnSelectedMetricChanged(HeatMetricOption value)
    {
        _groups.Clear();
        Render();
    }

    /// <summary>Preselecciona la aplicación al navegar (desde el portafolio o el inventario).</summary>
    public void SetApp(string? slug) => _pendingSlug = slug;

    // ---------- Zoom y migas ----------

    private HeatModule? _zoom;

    /// <summary>El módulo ampliado, o null en la vista completa. Las migas dicen dónde se está.</summary>
    [ObservableProperty] private string? _zoomedModule;

    public bool IsZoomed => ZoomedModule is not null;

    partial void OnZoomedModuleChanged(string? value) => OnPropertyChanged(nameof(IsZoomed));

    // ---------- Lo que la vista pinta ----------

    /// <summary>
    /// Las celdas del nivel 2. <b>Vacío en el nivel 1</b>: ahí no hay treemap, hay tarjetas — el
    /// treemap de 925 hojas de una vez no vuelve.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<HeatGroup> _mapGroups = Array.Empty<HeatGroup>();

    /// <summary>Las tarjetas del nivel 1, ya ordenadas.</summary>
    public ObservableCollection<ModuleCard> Cards { get; } = new();

    /// <summary>
    /// El termómetro de la cabecera (F10.2 §3): la aplicación entera repartida por paso de la
    /// rampa, más lo que nadie ha mirado. Es la leyenda aplicada al total — y lo que mejor
    /// funciona en una diapositiva, porque se entiende sin explicarlo.
    /// <para>
    /// Cuenta SIEMPRE la aplicación entera, con «solo auditadas» puesto o no: es el ancla de
    /// honestidad de la vista, y un ancla que se mueve con el filtro no ancla nada.
    /// </para>
    /// </summary>
    public ObservableCollection<HeatSegment> Thermometer { get; } = new();

    /// <summary>«925 unidades · 2 auditadas · 107 de deuda conocida».</summary>
    [ObservableProperty] private string _thermometerCaption = string.Empty;

    /// <summary>Qué mide el orden elegido, para el tooltip de «por qué este módulo está arriba».</summary>
    [ObservableProperty] private string _sortHint = string.Empty;

    /// <summary>
    /// El layout compuesto de cada módulo, por (módulo, métrica, filtro). Componer las celdas de un
    /// módulo es barato, pero se hace en cada ida y vuelta del zoom y no cambia entre ellas.
    /// </summary>
    private readonly Dictionary<string, HeatGroup> _groups = new(StringComparer.Ordinal);

    /// <inheritdoc cref="Treemap.ClusterFactory"/>
    [ObservableProperty] private Func<IReadOnlyList<HeatCell>, HeatCell>? _clusterFactory;

    public ObservableCollection<HeatLegendItem> Legend { get; } = new();

    public ObservableCollection<HeatRow> Rows { get; } = new();

    /// <summary>Qué mide el color, escrito. Un mapa sin esta frase no se puede leer.</summary>
    [ObservableProperty] private string _scaleCaption = string.Empty;

    /// <summary>«925 unidades · 314 382 líneas · 2 auditadas (0 %)».</summary>
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>El aviso de cobertura: cuánto del mapa es, literalmente, desconocido.</summary>
    [ObservableProperty] private string _coverageWarning = string.Empty;

    /// <summary>
    /// Qué se está omitiendo en las bandas del mapa (F10.1c), con un ejemplo real de esta
    /// aplicación. Vacío cuando no se omite nada. <b>Se declara una vez y en la cabecera</b>, no en
    /// cada banda: omitir sin decirlo obligaría a adivinar qué falta, y decirlo veintidós veces
    /// sería el mismo ruido que se está quitando.
    /// </summary>
    [ObservableProperty] private string _prefixNotice = string.Empty;

    /// <summary>
    /// Cómo se rotula cada módulo en la banda: su nombre corto, o ausente si va entero. Vacío en la
    /// vista ampliada, donde el sitio no escasea.
    /// </summary>
    private IReadOnlyDictionary<string, string?> _shortNames =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    [ObservableProperty] private bool _isEmpty = true;

    [ObservableProperty] private string _emptyReason = string.Empty;

    [ObservableProperty] private HeatSort _sort = HeatSort.Debt;

    [ObservableProperty] private bool _sortDescending = true;

    // ---------- Pinceles del tema ----------

    [ObservableProperty] private Brush _unknownFill = Brushes.Gainsboro;

    /// <inheritdoc cref="ApplyTheme"/>
    [ObservableProperty] private Brush _legendHatch = Brushes.Gainsboro;

    [ObservableProperty] private Brush _surfaceBrush = Brushes.Transparent;

    [ObservableProperty] private Brush _strokeBrush = Brushes.Gray;

    [ObservableProperty] private Brush _labelBrush = Brushes.Black;

    [ObservableProperty] private Brush _mutedBrush = Brushes.Gray;

    // ---------- Carga ----------

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            _dark = !string.Equals(_settings.Current.Theme, "light", StringComparison.OrdinalIgnoreCase);
            ApplyTheme();

            string? slug = _pendingSlug ?? SelectedApp?.Slug;
            _pendingSlug = null;

            // Un clon real trae ~900 unidades y sus hallazgos: leer y agregar fuera del hilo de
            // UI, como el panel de métricas.
            HeatmapView view = await Task.Run(() => _heat.Build(slug));

            // Sin app elegida se abre la primera del portafolio: un mapa vacío por no haber
            // tocado un combo no informa de nada.
            if (view.IsEmpty && string.IsNullOrEmpty(slug) && view.AppOptions.Count > 0)
            {
                view = await Task.Run(() => _heat.Build(view.AppOptions[0].Slug));
            }

            _view = view;
            _groups.Clear();
            BindOptions(view);
            _zoom = _zoom is null ? null : view.Modules.FirstOrDefault(m => m.Name == _zoom.Name);
            ZoomedModule = _zoom?.Name;
            Render();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BindOptions(HeatmapView view)
    {
        _binding = true;
        try
        {
            if (!AppOptions.Select(o => o.Slug).SequenceEqual(view.AppOptions.Select(o => o.Slug)))
            {
                AppOptions.Clear();
                foreach (AppOption option in view.AppOptions)
                {
                    AppOptions.Add(option);
                }
            }

            SelectedApp = AppOptions.FirstOrDefault(o => o.Slug == view.Slug) ?? AppOptions.FirstOrDefault();
        }
        finally
        {
            _binding = false;
        }
    }

    private void ApplyTheme()
    {
        // Gris PLANO para las celdas (F10.2 §5). Novecientos rectángulos rayados son textura, no
        // información: el rayado se leía como ruido de fondo y tapaba las dos celdas con dato. La
        // trama se queda donde sí distingue algo — la muestra de la leyenda, que está una vez.
        UnknownFill = HeatBrushes.Solid(DensityScale.Unknown.For(_dark));
        LegendHatch = HeatBrushes.Hatch(
            (Color)ColorConverter.ConvertFromString(DensityScale.Unknown.For(_dark)),
            (Color)ColorConverter.ConvertFromString(DensityScale.UnknownHatch.For(_dark)));
        SurfaceBrush = HeatBrushes.Solid(DensityScale.Surface.For(_dark));
        StrokeBrush = HeatBrushes.Solid(_dark ? "#33383F" : "#DDDFE3");
        LabelBrush = HeatBrushes.Solid(_dark ? "#F2F2F4" : "#1A1A1D");
        MutedBrush = HeatBrushes.Solid(_dark ? "#9DA2AA" : "#65696F");
    }

    // ---------- Composición del mapa ----------

    private void Render()
    {
        HeatMetric metric = SelectedMetric.Metric;
        IsEmpty = _view.IsEmpty;
        EmptyReason = _view.Slug.Length == 0
            ? "Elige una aplicación para ver su mapa."
            : $"«{_view.AppName}» no tiene inventario en el ciclo {_view.CycleN}. Escanea el clon desde Inventario.";

        // El rótulo se calcula sobre TODOS los módulos de la aplicación —no sobre los que se estén
        // viendo, porque la colisión que hay que evitar es entre cualesquiera dos—. Es cálculo de
        // vista: no se guarda ni toca el hub.
        _shortNames = ModulePrefix.DisplayNames(
            _view.AppName, _view.Slug, _view.Modules.Select(m => m.Name));
        PrefixNotice = _zoom is null ? Notice() : string.Empty;

        BuildThermometer();
        BuildLegend(metric);

        if (_zoom is null)
        {
            BuildCards(metric);
            MapGroups = Array.Empty<HeatGroup>();
        }
        else
        {
            Cards.Clear();
            MapGroups = new[] { GroupOf(_zoom, metric) };
        }

        ClusterFactory = tiny => Cluster(tiny, metric);
        BuildRows(Scope());

        ScaleCaption = metric == HeatMetric.Deuda
            ? "El área es el tamaño (LOC); el color, la deuda total de la unidad."
            : "El área es el tamaño (LOC); el color, la deuda por cada mil líneas.";

        BuildSummary();
    }

    /// <summary>Los módulos que la vista está mirando: la app entera, o el módulo ampliado.</summary>
    private IReadOnlyList<HeatModule> Scope()
        => _zoom is null ? _view.Modules : new List<HeatModule> { _zoom };

    // ---------- Nivel 1: las tarjetas ----------

    /// <summary>
    /// Una tarjeta por módulo, ordenadas. Son ~22 elementos en vez de las 925 celdas del treemap
    /// de hojas: es de donde sale la diferencia de tiempo de carga (F10.2 §5).
    /// </summary>
    private void BuildCards(HeatMetric metric)
    {
        var cards = _view.Modules
            .Where(m => !OnlyAudited || m.AuditedUnits > 0)
            .Select(m => Card(m, metric))
            .ToList();

        Cards.Clear();
        foreach (ModuleCard card in Sorted(cards))
        {
            Cards.Add(card);
        }

        SortHint = SelectedSort.Hint;
    }

    private ModuleCard Card(HeatModule module, HeatMetric metric)
    {
        Attention attention = AttentionScore.Of(module, _view.TotalLoc);
        double? value = module.Value(metric);
        SeverityChips chips = module.Severities;

        return new ModuleCard(
            _shortNames.TryGetValue(module.Name, out string? brief) && brief is not null
                ? brief
                : module.Name,
            module.Name,
            $"{Num(module.UnitCount)} u · {Kloc(module.Loc)} KLOC",
            module.Coverage,
            CoverageBar(module),
            $"{module.Coverage:P0} auditado · {Num(module.AuditedUnits)} de {Num(module.UnitCount)}",
            Fill(value, metric),
            Fill(value, metric) ?? UnknownFill,
            value is { } v ? v.ToString("0.#", AppCulture.Display) : HeatRow.Unknown,
            module.IsMeasured
                ? metric == HeatMetric.Deuda ? "deuda auditada" : "por KLOC auditada"
                : "densidad desconocida",
            SeverityBars(chips),
            chips.Total == 0 ? "Sin hallazgos activos" : $"{Num(chips.Total)} activos",
            attention.Score,
            attention.Score.ToString("0.00", AppCulture.Display),
            ModuleTip(module, attention),
            module);
    }

    /// <summary>
    /// Los dos tramos de la cobertura. El de «sin auditar» lleva el mismo gris que el mapa, así
    /// que la tarjeta y las celdas dicen lo mismo con el mismo color.
    /// </summary>
    private IReadOnlyList<HeatSegment> CoverageBar(HeatModule module)
    {
        int pending = module.UnitCount - module.AuditedUnits;
        return new[]
        {
            new HeatSegment(
                "auditadas",
                module.AuditedUnits,
                module.Coverage,
                MutedBrush,
                $"{Num(module.AuditedUnits)} unidades auditadas"),
            new HeatSegment(
                "sin auditar",
                pending,
                1 - module.Coverage,
                UnknownFill,
                $"{Num(pending)} unidades sin auditar — densidad DESCONOCIDA, que no es cero."),
        };
    }

    /// <summary>La mini barra apilada C/A/M/B, con los colores de estado de siempre.</summary>
    private static IReadOnlyList<HeatSegment> SeverityBars(SeverityChips chips)
    {
        int total = Math.Max(1, chips.Total);
        return Enum.GetValues<Severity>()
            .Select(sev => new HeatSegment(
                SeverityNames.Display(sev),
                chips.Of(sev),
                (double)chips.Of(sev) / total,
                HeatBrushes.Solid(SeverityPalette.Hex(sev)),
                $"{SeverityNames.Display(sev)}: {chips.Of(sev)}"))
            .Where(seg => seg.Count > 0)
            .ToList();
    }

    /// <summary>
    /// El orden de las tarjetas. «Atención» primero por defecto; las demás responden preguntas
    /// concretas y se eligen cuando se tiene una.
    /// </summary>
    private IEnumerable<ModuleCard> Sorted(IReadOnlyList<ModuleCard> cards)
    {
        var byName = cards.ToDictionary(c => c.FullName, c => c, StringComparer.Ordinal);
        HeatModule Module(ModuleCard c) => (HeatModule)c.Payload!;
        _ = byName;

        IOrderedEnumerable<ModuleCard> ordered = SelectedSort.Sort switch
        {
            ModuleSort.Deuda => cards.OrderByDescending(c => Module(c).KnownDebt),

            // Sin densidad medida no se puede ordenar por densidad: van al final, no arriba con un
            // cero que fingiría que están limpios.
            ModuleSort.Densidad => cards
                .OrderBy(c => Module(c).Density is null)
                .ThenByDescending(c => Module(c).Density ?? 0),

            ModuleSort.Tamano => cards.OrderByDescending(c => Module(c).Loc),
            ModuleSort.Cobertura => cards.OrderBy(c => Module(c).Coverage),
            ModuleSort.Nombre => cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
            _ => cards.OrderByDescending(c => c.Attention),
        };

        return ordered.ThenBy(c => c.FullName, StringComparer.OrdinalIgnoreCase);
    }

    // ---------- El termómetro ----------

    /// <summary>
    /// La aplicación entera en una barra: cada paso de la rampa con sus unidades auditadas, y al
    /// final lo que nadie ha mirado. Es la leyenda aplicada al total, así que no hay vocabulario
    /// nuevo que aprender.
    /// </summary>
    private void BuildThermometer()
    {
        Thermometer.Clear();
        var units = _view.Units.ToList();
        if (units.Count == 0)
        {
            ThermometerCaption = string.Empty;
            return;
        }

        HeatMetric metric = SelectedMetric.Metric;
        foreach (HeatStep step in DensityScale.For(metric))
        {
            int count = units.Count(u => u.IsMeasured && DensityScale.StepOf(u.Value(metric), metric) == step);
            if (count == 0)
            {
                continue;
            }

            string label = step.Index == 0 ? "auditadas sin deuda" : $"auditadas · {DensityScale.RangeLabel(step)}";
            Thermometer.Add(new HeatSegment(
                label,
                count,
                (double)count / units.Count,
                HeatBrushes.Solid(step.For(_dark)),
                $"{Num(count)} unidades {label} ({(double)count / units.Count:P1})"));
        }

        int unknown = units.Count(u => !u.IsMeasured);
        if (unknown > 0)
        {
            Thermometer.Add(new HeatSegment(
                "sin auditar",
                unknown,
                (double)unknown / units.Count,
                UnknownFill,
                $"{Num(unknown)} unidades sin auditar ({(double)unknown / units.Count:P1}) — "
                + "densidad DESCONOCIDA, que no es cero."));
        }

        ThermometerCaption =
            $"{Num(_view.TotalUnits)} unidades · {Num(_view.AuditedUnits)} auditadas ({_view.Coverage:P0}) · "
            + $"{Num(_view.KnownDebt)} de deuda conocida";
    }

    // ---------- Nivel 2: el treemap del módulo ----------

    /// <summary>
    /// Las celdas de un módulo, cacheadas por (módulo, métrica, filtro): entrar y salir del zoom
    /// no vuelve a componerlas. La caché se tira entera en cada <see cref="LoadAsync"/>, que es
    /// cuando el hub puede haber cambiado.
    /// </summary>
    private HeatGroup GroupOf(HeatModule module, HeatMetric metric)
    {
        string key = $"{module.Name}|{metric}|{OnlyAudited}|{_dark}";
        if (_groups.TryGetValue(key, out HeatGroup? cached))
        {
            return cached;
        }

        HeatGroup group = Group(module, metric);
        _groups[key] = group;
        return group;
    }

    private void BuildSummary()
    {
        // El resumen describe lo que SE ESTÁ VIENDO, no siempre la aplicación entera: ampliado a
        // un módulo, las cifras de toda la app debajo de un mapa que solo enseña ese módulo son
        // una nota al pie que contradice la figura — y en la imagen exportada, un pie de foto
        // falso.
        var modules = Scope();
        int units = modules.Sum(m => m.UnitCount);
        int audited = modules.Sum(m => m.AuditedUnits);
        int loc = modules.Sum(m => m.Loc);
        int debt = modules.Sum(m => m.KnownDebt);
        double coverage = units == 0 ? 0 : (double)audited / units;

        Summary = _view.IsEmpty
            ? string.Empty
            : $"{Num(units)} unidades · {Num(loc)} líneas · "
              + $"{Num(audited)} auditadas ({coverage:P0}) · deuda conocida {Num(debt)}";

        CoverageWarning = _view.IsEmpty || audited == units || OnlyAudited
            ? string.Empty
            : $"{Num(units - audited)} de {Num(units)} unidades están en gris: "
              + "nadie las ha auditado, así que su densidad es DESCONOCIDA, no cero.";
    }

    /// <summary>
    /// La frase que explica la omisión, con un ejemplo <b>de esta aplicación</b> en vez de una
    /// regla abstracta: «XBLASTCore → Core» se entiende sin releerla. Y dice lo que pasa con los
    /// que no la llevan, que es lo que evita que alguien lea «Documents» y crea que le falta algo.
    /// </summary>
    private string Notice()
    {
        var elided = _shortNames.Where(p => p.Value is not null).ToList();
        if (elided.Count == 0)
        {
            return string.Empty;
        }

        KeyValuePair<string, string?> sample = elided[0];
        string rest = _shortNames.Count - elided.Count == 0
            ? string.Empty
            : " Los que no lo llevan salen enteros.";

        return $"Los módulos se muestran sin el nombre de la aplicación ({sample.Key} → {sample.Value})."
               + rest
               + " El nombre completo está en el tooltip y en la tabla.";
    }

    private HeatGroup Group(HeatModule module, HeatMetric metric)
        => new(
            module.Name,
            $"{Num(module.UnitCount)} u · {module.Coverage:P0} auditado",
            module.Loc,
            Fill(module.Value(metric), metric),
            InkOf(module.Value(metric), metric),
            module.IsQualified,
            ModuleTip(module),
            module,
            Units(module).Select(u => Cell(u, metric)).ToList(),
            // El nivel 2 escribe el nombre ENTERO: hay una banda a lo ancho de la ventana, así que
            // no hay sitio que ahorrar — y las migas de encima ya dicen de qué módulo se trata.
            ShortName: null);

    /// <summary>
    /// Las unidades que se pintan de un módulo. Con «solo auditadas», las que se sabe medir: es lo
    /// que convierte el mapa de densidad en algo legible mientras la cobertura sea baja.
    /// </summary>
    private IEnumerable<HeatUnit> Units(HeatModule module)
        => OnlyAudited ? module.Units.Where(u => u.IsMeasured) : module.Units;

    private HeatCell Cell(HeatUnit unit, HeatMetric metric)
        => new(
            unit.FileName,
            unit.Loc,
            Fill(unit.Value(metric), metric),
            InkOf(unit.Value(metric), metric),
            unit.IsQualified,
            UnitTip(unit),
            unit);

    /// <summary>
    /// El color de un valor. <c>null</c> —desconocido— devuelve <c>null</c>, que es lo que hace
    /// que el control pinte el gris tramado en vez del paso más frío de la escala.
    /// </summary>
    private Brush? Fill(double? value, HeatMetric metric)
        => DensityScale.StepOf(value, metric) is { } step ? HeatBrushes.Solid(step.For(_dark)) : null;

    /// <summary>
    /// Con qué se escribe encima de ese relleno. Sale del PASO, no de una fórmula de luminancia
    /// aplicada al color: la fórmula devolvía blanco sobre el coral del paso 4 de la rampa oscura,
    /// donde lo que se lee es el negro. El contraste de cada pareja está verificado por diseño;
    /// recalcularlo en cada render solo sirve para volver a equivocarse en el mismo borde.
    /// </summary>
    private Brush? InkOf(double? value, HeatMetric metric)
        => DensityScale.StepOf(value, metric) is { } step ? HeatBrushes.Solid(step.InkFor(_dark)) : null;

    /// <summary>
    /// Cómo se resume la cola de celdas que no llegan a verse (F10.1 §2). El control decide cuáles
    /// —conoce la geometría—; aquí se decide qué dicen.
    /// <para>
    /// <b>Nunca la media, y nunca «limpio» por defecto.</b> El color del agregado sale de dos
    /// reglas, y cada una tapa una forma distinta de mentir:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>La peor manda.</b> Con la media, una clase de 40 líneas en el paso 5 desaparecería dentro
    /// de treinta y nueve tranquilas — el agregado escondería justo lo que el mapa existe para
    /// enseñar. Con la peor, agrupar solo puede exagerar, y exagerar en una celda que dice «+N
    /// unidades» invita a ampliar, que es lo que hay que hacer con ella.
    /// </item>
    /// <item>
    /// <b>Si queda algo sin auditar, el agregado no puede decir «limpio».</b> Se vio en el mapa
    /// real: sesenta unidades de XBLASTCommon, cincuenta y nueve sin auditar y una auditada y
    /// limpia — y el agregado salía del paso 1, o sea tranquilizador, por la única que alguien
    /// había mirado. Ahora eso va en gris. La excepción es la que no engaña a nadie: una medida
    /// <b>por encima del paso 1</b> sigue mandando aunque el resto esté sin auditar, porque «aquí
    /// dentro hay algo caliente» es un hecho comprobado, no una extrapolación.
    /// </item>
    /// </list>
    /// <para>
    /// Y va siempre con contorno punteado: el relleno de cuarenta unidades nunca cuenta toda la
    /// verdad.
    /// </para>
    /// </summary>
    private HeatCell Cluster(IReadOnlyList<HeatCell> tiny, HeatMetric metric)
    {
        var units = tiny.Select(c => c.Payload).OfType<HeatUnit>().ToList();
        var measured = units.Select(u => u.Value(metric)).Where(v => v is not null).ToList();

        double? worst = measured.Count == 0 ? null : measured.Max();
        bool everyoneMeasured = measured.Count == units.Count && units.Count > 0;
        bool warns = DensityScale.StepOf(worst, metric) is { Index: > 0 };

        if (!everyoneMeasured && !warns)
        {
            worst = null;
        }

        int loc = units.Sum(u => u.Loc);
        int debt = units.Sum(u => u.KnownDebt);
        int audited = units.Count(u => u.IsMeasured);

        var lines = new List<string>
        {
            $"+{Num(units.Count)} unidades pequeñas",
            $"{Num(loc)} líneas · {Num(audited)} auditadas de {Num(units.Count)}",
            $"Deuda conocida: {Num(debt)}",
        };

        HeatUnit? peak = units
            .Where(u => u.Density is not null)
            .OrderByDescending(u => u.Density)
            .FirstOrDefault();

        lines.Add(peak is null
            ? "Ninguna está auditada: el color no puede decir nada de ellas."
            : worst is null
                ? $"La peor auditada: {peak.FileName} ({peak.Density:0.#} por KLOC), pero quedan "
                  + "unidades sin auditar: el gris es lo único que se puede afirmar del grupo."
                : $"La peor: {peak.FileName} ({peak.Density:0.#} por KLOC). El color es el suyo.");

        lines.Add(IsZoomed
            ? "Demasiado pequeñas para dibujarlas. Pulsa para verlas en la tabla."
            : "Demasiado pequeñas para dibujarlas. Pulsa para ampliar el módulo.");

        return new HeatCell(
            $"+{units.Count} unidades",
            tiny.Sum(c => c.Weight),
            Fill(worst, metric),
            InkOf(worst, metric),
            Qualified: true,
            string.Join(Environment.NewLine, lines),
            units.Count > 0 ? new HeatCluster(units[0].Module, units) : null,
            ShortLabel: $"+{units.Count}");
    }

    private void BuildLegend(HeatMetric metric)
    {
        Legend.Clear();
        string unit = metric == HeatMetric.Deuda ? "puntos" : "por KLOC";
        foreach (HeatStep step in DensityScale.For(metric))
        {
            Legend.Add(new HeatLegendItem(
                $"{DensityScale.RangeLabel(step)} {unit}",
                HeatBrushes.Solid(step.For(_dark)),
                Dotted: false,
                Tooltip: $"Paso {step.Index + 1} de {DensityScale.StepCount}"));
        }

        Legend.Add(new HeatLegendItem(
            "No auditada · densidad DESCONOCIDA (no es cero)",
            LegendHatch,
            Dotted: false,
            Tooltip: "Nadie ha barrido esta unidad. Lo que no se ha mirado no se puede llamar limpio."));

        Legend.Add(new HeatLegendItem(
            "Contorno punteado · el relleno no lo dice todo",
            HeatBrushes.Solid("#00FFFFFF"),
            Dotted: true,
            Tooltip: "Gris con hallazgos ya conocidos, o medida de un código que ya ha cambiado."));

        Legend.Add(new HeatLegendItem(
            $"Pesos: Crítica {DebtWeights.Critica} · Alta {DebtWeights.Alta} · "
            + $"Media {DebtWeights.Media} · Baja {DebtWeights.Baja}",
            Fill: null,
            Dotted: false,
            Tooltip: "Los pesos con los que se suma la deuda. Solo cuentan los hallazgos activos."));
    }

    private void BuildRows(IReadOnlyList<HeatModule> modules)
    {
        var rows = modules
            .SelectMany(m => Units(m).Select(u => new HeatRow(
                m.Name,
                u.FileName,
                u.Path,
                u.Loc,
                u.Findings.Critica,
                u.Findings.Alta,
                u.Findings.Media,
                u.Findings.Baja,
                u.KnownDebt,
                u.Density,
                StateText(u),
                StateDetail(u),
                Fill(u.Value(SelectedMetric.Metric), SelectedMetric.Metric) ?? UnknownFill,
                u)))
            .ToList();

        Rows.Clear();
        foreach (HeatRow row in Order(rows))
        {
            Rows.Add(row);
        }
    }

    /// <summary>
    /// La tabla ordenada. Las unidades sin densidad van SIEMPRE al final al ordenar por densidad,
    /// en los dos sentidos: un «—» no es ni el máximo ni el mínimo, y colarlo arriba al invertir
    /// diría que son las más limpias.
    /// </summary>
    private IEnumerable<HeatRow> Order(IEnumerable<HeatRow> rows)
    {
        IOrderedEnumerable<HeatRow> ordered = Sort switch
        {
            HeatSort.Module => Direction(rows, r => r.Module),
            HeatSort.Unit => Direction(rows, r => r.Unit),
            HeatSort.Loc => Direction(rows, r => r.Loc),
            HeatSort.Critica => Direction(rows, r => r.Critica),
            HeatSort.Alta => Direction(rows, r => r.Alta),
            HeatSort.Media => Direction(rows, r => r.Media),
            HeatSort.Baja => Direction(rows, r => r.Baja),
            HeatSort.Density => Direction(
                rows.OrderBy(r => r.Density is null), r => r.Density ?? 0),
            _ => Direction(rows, r => r.Debt),
        };

        return ordered
            .ThenBy(r => r.Module, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Path, StringComparer.Ordinal);
    }

    private IOrderedEnumerable<HeatRow> Direction<TKey>(IEnumerable<HeatRow> rows, Func<HeatRow, TKey> key)
        => SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    private IOrderedEnumerable<HeatRow> Direction<TKey>(IOrderedEnumerable<HeatRow> rows, Func<HeatRow, TKey> key)
        => SortDescending ? rows.ThenByDescending(key) : rows.ThenBy(key);

    // ---------- Los textos que explican una celda ----------

    /// <summary>
    /// El estado, corto. Las frases largas —«No auditada · excluida por tamaño»— no cabían en su
    /// columna y salían cortadas; acortar el TEXTO es mejor que ensanchar la columna a costa del
    /// nombre del fichero, que es lo que se busca leyendo. El detalle entero sigue en el tooltip
    /// de la celda del mapa.
    /// </summary>
    private static string StateText(HeatUnit unit) => unit.Knowledge switch
    {
        HeatKnowledge.Auditada => "Auditada",
        HeatKnowledge.Cambiada => "Auditada (cambió)",
        _ when unit.State == UnitState.Grande => "No auditada (grande)",
        _ => "No auditada",
    };

    /// <summary>Y la frase entera, para el tooltip de esa celda.</summary>
    private static string StateDetail(HeatUnit unit) => unit.Knowledge switch
    {
        HeatKnowledge.Auditada => "Auditada",
        HeatKnowledge.Cambiada => "Auditada · el código ha cambiado desde entonces",
        _ when unit.State == UnitState.Grande => "No auditada · excluida del ciclo por su tamaño",
        _ => "No auditada · nadie la ha barrido todavía",
    };

    private string UnitTip(HeatUnit unit)
    {
        var lines = new List<string>
        {
            unit.Path,
            $"{Num(unit.Loc)} líneas · {StateDetail(unit)}",
            Severities(unit.Findings),
            $"Deuda conocida: {Num(unit.KnownDebt)}",
            unit.Density is { } d
                ? $"Densidad: {d.ToString("0.#", AppCulture.Display)} por KLOC"
                : "Densidad: DESCONOCIDA — nadie ha auditado esta unidad",
        };

        if (unit.Knowledge == HeatKnowledge.NoAuditada && unit.KnownDebt > 0)
        {
            lines.Add("Los hallazgos que se le conocen son una cota inferior, no su total.");
        }
        else if (unit.Knowledge == HeatKnowledge.Cambiada)
        {
            lines.Add("La medida es del código que tenía cuando se auditó, no del de ahora.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string ModuleTip(HeatModule module, Attention? attention = null)
    {
        // Siempre el nombre COMPLETO: el tooltip es lo que deshace la omisión de la banda.
        var lines = new List<string>
        {
            module.Name,
            $"{Num(module.UnitCount)} unidades · {Num(module.Loc)} líneas",
            $"Cobertura: {Num(module.AuditedUnits)} auditadas de {Num(module.UnitCount)} ({module.Coverage:P0})",
            $"Deuda conocida: {Num(module.KnownDebt)}",
            module.Density is { } d
                ? $"Densidad (sobre lo auditado): {d.ToString("0.#", AppCulture.Display)} por KLOC"
                : "Densidad: DESCONOCIDA — ninguna unidad de este módulo se ha auditado",
            Severities(module.Severities),
        };

        if (module.UnauditedDebt > 0)
        {
            lines.Add($"De esa deuda, {Num(module.UnauditedDebt)} está en unidades sin auditar.");
        }

        if (attention is { } score)
        {
            lines.Add(string.Empty);
            lines.Add(Why(module, score));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Por qué este módulo está donde está en el orden «Atención». Con la fórmula desglosada: un
    /// ranking que no se puede explicar no se sigue, y este ordena por algo que no se ve.
    /// </summary>
    private static string Why(HeatModule module, Attention score)
    {
        string risk = module.Density is { } d
            ? $"riesgo medido {score.Risk.ToString("0.00", AppCulture.Display)} "
              + $"(densidad {d.ToString("0.#", AppCulture.Display)} por KLOC, "
              + $"con {score.Confidence:P0} de confianza sobre el {module.CoverageLoc:P0} del código mirado)"
            : "riesgo medido 0 (nadie ha auditado nada de este módulo)";

        return $"Atención {score.Score.ToString("0.00", AppCulture.Display)} = "
               + $"{AttentionScore.RiskWeight.ToString("0.#", AppCulture.Display)} × {risk} + "
               + $"{AttentionScore.IgnoranceWeight.ToString("0.#", AppCulture.Display)} × "
               + $"ignorancia {score.Ignorance.ToString("0.00", AppCulture.Display)} "
               + $"({Num(module.Loc - module.AuditedLoc)} líneas sin auditar, "
               + $"{score.Ignorance:P0} del código de la aplicación).";
    }

    /// <summary>Miles de líneas, que es como se lee el tamaño de un módulo.</summary>
    private static string Kloc(int loc) => (loc / 1000.0).ToString("0.#", AppCulture.Display);

    private static string Severities(SeverityChips chips)
        => chips.Total == 0
            ? "Sin hallazgos activos conocidos"
            : $"Activos: C{chips.Critica} · A{chips.Alta} · M{chips.Media} · B{chips.Baja}";

    private static string Num(int value) => value.ToString("N0", AppCulture.Display);

    // ---------- Gestos ----------

    /// <summary>
    /// Un clic. En la vista completa <b>amplía el módulo</b> —también si se pulsó una de sus
    /// celdas, que ahí son de dos píxeles—; ampliado, un clic en una unidad abre sus hallazgos.
    /// </summary>
    [RelayCommand]
    private Task Tile(object? payload)
    {
        switch (payload)
        {
            case HeatModule module when _zoom is null:
                _zoom = module;
                ZoomedModule = module.Name;
                Render();
                return Task.CompletedTask;

            case HeatUnit unit when _zoom is null:
                _zoom = _view.Modules.FirstOrDefault(m => m.Name == unit.Module);
                ZoomedModule = _zoom?.Name;
                Render();
                return Task.CompletedTask;

            case HeatCluster cluster when _zoom is null:
                _zoom = _view.Modules.FirstOrDefault(m => m.Name == cluster.Module);
                ZoomedModule = _zoom?.Name;
                Render();
                return Task.CompletedTask;

            // Ya ampliado no hay un nivel más al que bajar: lo honesto con cuarenta unidades que
            // no caben es enseñarlas donde sí caben, que es la tabla.
            case HeatCluster:
                TableMode = true;
                return Task.CompletedTask;

            case HeatUnit unit:
                return ShowFindingsFor(unit);

            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>Doble clic sobre una unidad: auditarla. El mismo gesto que el botón de la tabla.</summary>
    [RelayCommand]
    private Task Activate(object? payload)
        => payload is HeatUnit unit ? Audit(unit) : Task.CompletedTask;

    /// <summary>Una tarjeta del nivel 1: amplía a su treemap de unidades.</summary>
    [RelayCommand]
    private void OpenModule(ModuleCard? card)
    {
        if (card?.Payload is HeatModule module)
        {
            _zoom = module;
            ZoomedModule = module.Name;
            Render();
        }
    }

    /// <summary>Vuelve del módulo ampliado a la aplicación entera. Es la miga.</summary>
    [RelayCommand]
    private void ZoomOut()
    {
        _zoom = null;
        ZoomedModule = null;
        Render();
    }

    /// <summary>Los hallazgos de esta unidad, en V3, filtrados por su ruta.</summary>
    [RelayCommand]
    private Task ShowFindingsFor(HeatUnit? unit)
        => unit is null
            ? Task.CompletedTask
            : _navigation.NavigateToAsync<FindingsViewModel>(vm =>
            {
                vm.SetApp(_view.Slug);
                vm.SetSearch(unit.Path);
            });

    /// <summary>
    /// Auditar esta unidad: se abre el inventario con ella marcada y buscada. No se lanza la
    /// sesión desde aquí — lanzar cuesta dinero y el sitio donde se confirma el gasto es uno solo
    /// (F5.13). Este mapa dice DÓNDE; el inventario sigue siendo quien dispara.
    /// </summary>
    [RelayCommand]
    private Task Audit(HeatUnit? unit)
    {
        if (unit is null)
        {
            return Task.CompletedTask;
        }

        _toasts.Show($"«{unit.FileName}» va marcada: pulsa «Auditar selección» para lanzarla.");
        return _navigation.NavigateToAsync<InventoryViewModel>(vm =>
        {
            vm.SetApp(_view.Slug);
            vm.Preselect(unit.Path);
        });
    }

    /// <summary>Ordena la tabla por una columna; repetir la misma invierte el sentido.</summary>
    [RelayCommand]
    private void SortBy(HeatSort column)
    {
        if (Sort == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            Sort = column;

            // Los números empiezan de mayor a menor (lo que arde primero) y los textos de la A a
            // la Z, que es como se busca un nombre.
            SortDescending = column is not (HeatSort.Module or HeatSort.Unit);
        }

        RefreshHeaders();
        BuildRows(_zoom is null ? _view.Modules : new List<HeatModule> { _zoom });
    }

    /// <summary>La flecha de la cabecera de una columna. Vacía en las que no ordenan ahora.</summary>
    public string GlyphFor(HeatSort column)
        => Sort != column ? string.Empty : SortDescending ? " ▾" : " ▴";

    /// <summary>
    /// Las cabeceras, con su flecha. Van como propiedades y no como un converter porque la flecha
    /// depende de DOS cosas (qué columna y en qué sentido) y un converter con dos entradas es un
    /// multi-binding que hay que leer tres veces para entender qué escribe.
    /// </summary>
    public string HeaderModule => "Módulo" + GlyphFor(HeatSort.Module);

    public string HeaderUnit => "Unidad" + GlyphFor(HeatSort.Unit);

    public string HeaderLoc => "LOC" + GlyphFor(HeatSort.Loc);

    public string HeaderCritica => "C" + GlyphFor(HeatSort.Critica);

    public string HeaderAlta => "A" + GlyphFor(HeatSort.Alta);

    public string HeaderMedia => "M" + GlyphFor(HeatSort.Media);

    public string HeaderBaja => "B" + GlyphFor(HeatSort.Baja);

    public string HeaderDebt => "Deuda" + GlyphFor(HeatSort.Debt);

    public string HeaderDensity => "Densidad" + GlyphFor(HeatSort.Density);

    private void RefreshHeaders()
    {
        foreach (string name in new[]
                 {
                     nameof(HeaderModule), nameof(HeaderUnit), nameof(HeaderLoc), nameof(HeaderCritica),
                     nameof(HeaderAlta), nameof(HeaderMedia), nameof(HeaderBaja), nameof(HeaderDebt),
                     nameof(HeaderDensity),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ---------- Exportar la diapositiva ----------

    [RelayCommand]
    private void ExportImage()
    {
        if (_view.IsEmpty)
        {
            _toasts.Show("No hay mapa que exportar todavía.");
            return;
        }

        DateTimeOffset now = _time.GetLocalNow();
        string? target = _saver.Pick(
            "Exportar el mapa de calor",
            HeatmapImage.FileName(_view.Slug, now),
            "Imagen PNG (*.png)|*.png");

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            HeatmapImage.Save(ImageRequest(now), target);
            _toasts.Show($"Mapa guardado en {target}");
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudo exportar el mapa: {ex.Message}");
        }
    }

    /// <summary>
    /// Lo que va a la imagen. Público para que un test pueda leer el título, el pie y la leyenda
    /// sin abrir un PNG.
    /// </summary>
    public HeatmapImageRequest ImageRequest(DateTimeOffset when)
    {
        string scope = ZoomedModule is { } module ? $" · {module}" : string.Empty;
        string org = _hub.Store.TryReadHub()?.OrganizationName is { Length: > 0 } name ? name : "sin organización";

        return new HeatmapImageRequest(
            $"{_view.AppName}{scope} · mapa de calor · {when:d MMMM yyyy}",
            $"{ScaleCaption} {Summary}",
            $"Atalaya · {org}",
            MapGroups,
            Legend.ToList(),
            _dark,
            Cards.ToList(),
            Thermometer.ToList());
    }
}
