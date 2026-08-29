using System.Collections.ObjectModel;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.App.Views;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>V2 Inventory (§8): module→unit tree with state, claims, filters, and cycle actions.</summary>
public sealed partial class InventoryViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;
    private readonly NavigationService _navigation;
    private readonly LiveSessionService _live;
    private readonly SettingsService _settings;
    private readonly CostEstimator _costs;
    private readonly IAuditLaunchConfirmer _confirmer;

    /// <summary>F5.8 §1: si esta máquina tiene el clon. Decide el modo solo-lectura.</summary>
    private readonly CloneLinkService _links;

    /// <summary>F5.8 §2: el mismo diálogo de vincular que abre el portafolio.</summary>
    private readonly LinkCloneFlow _linkFlow;

    /// <summary>F5.8 §2: el re-escaneo, ahora compartido con el flujo de vincular.</summary>
    private readonly InventoryRescanService _rescan;

    /// <summary>F5.10: la gestión de reglas excluidas de esta app.</summary>
    private readonly GovernanceService _governance;

    /// <summary>F5.10: quién abre esa gestión. Inyectada para poder probar el gesto sin ventana.</summary>
    private readonly IPatternSilencesDialog _patternsDialog;

    /// <summary>F7: el registro de directivas del proyecto de esta app.</summary>
    private readonly DirectiveService _directives;

    /// <summary>F7: quién abre su gestión. Inyectada por lo mismo que la de patrones.</summary>
    private readonly IDirectivesDialog _directivesDialog;

    /// <summary>
    /// F5.7 §4: el resultado de una acción se cuenta por el toast global. El texto que vivía al
    /// fondo del panel del ciclo se quedaba pegado hasta la acción siguiente y, con la ventana
    /// corta, ni siquiera se veía.
    /// </summary>
    private readonly ToastCenter _toasts;

    /// <summary>Plegar y desplegar módulos: la MISMA lógica que V3 (F5.6 §1).</summary>
    private readonly GroupCollapse _collapse;

    /// <summary>
    /// Lo seleccionado, por ruta de unidad y para TODO el ciclo — no solo para lo que está a la
    /// vista. Es la pieza que hace que filtrar no deseleccione: los nodos del árbol se tiran y se
    /// reconstruyen en cada búsqueda, así que la selección no puede vivir en ellos.
    /// </summary>
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    /// <summary>El ciclo entero, sin filtrar: contra esto se cuentan pendientes y seleccionadas.</summary>
    private IReadOnlyList<InventoryUnit> _allUnits = Array.Empty<InventoryUnit>();

    private readonly HeatmapQuery _heat;

    public InventoryViewModel(
        HubContext hub, IUlidFactory ulids, NavigationService navigation, LiveSessionService live,
        SettingsService settings, CostEstimator costs, IAuditLaunchConfirmer confirmer,
        GroupExpansionMemory expansion, ToastCenter toasts,
        CloneLinkService links, LinkCloneFlow linkFlow, InventoryRescanService rescan,
        GovernanceService governance, IPatternSilencesDialog patternsDialog,
        DirectiveService directives, IDirectivesDialog directivesDialog,
        HeatmapQuery heat)
    {
        _heat = heat;
        _governance = governance;
        _patternsDialog = patternsDialog;
        _directives = directives;
        _directivesDialog = directivesDialog;
        _hub = hub;
        _ulids = ulids;
        _navigation = navigation;
        _live = live;
        _settings = settings;
        _costs = costs;
        _confirmer = confirmer;
        _toasts = toasts;
        _links = links;
        _linkFlow = linkFlow;
        _rescan = rescan;
        _collapse = new GroupCollapse(expansion);
        _collapse.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
    }

    public override string Title => AppName is { Length: > 0 } ? $"Inventario · {AppName}" : "Inventario";

    public ObservableCollection<ModuleNode> Modules { get; } = new();

    [ObservableProperty] private string _slug = string.Empty;
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private int _cycleN = 1;
    [ObservableProperty] private int _totalUnits;
    [ObservableProperty] private int _auditedUnits;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitsTooltip))]
    private int _largeUnits;
    [ObservableProperty] private int _pendingUnits;
    /// <summary>
    /// Cuántas AUDITORÍAS se han lanzado en este ciclo. Antes contaba TODAS las sesiones de la
    /// aplicación, de todos los ciclos y de todos los tipos: un número que no se podía explicar
    /// en una frase, y por eso desconcertaba.
    /// </summary>
    [ObservableProperty] private int _sessionCount;

    /// <summary>
    /// Patrones silenciados VIVOS en esta app (F5.12). Es un dato del panel del ciclo porque decide
    /// qué se va a reportar y qué no en cada auditoría de esta aplicación: una cobertura del 100 %
    /// con tres tipos de problema silenciados no significa lo mismo que una del 100 % sin ninguno.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternsTooltip))]
    private int _silencedPatterns;

    /// <summary>Los caducados, que ya no suprimen y esperan que alguien decida (F5.12).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternsTooltip))]
    private int _expiredPatterns;

    /// <summary>Lo que explica el número, incluido el caso «hay caducados que revisar».</summary>
    public string PatternsTooltip => SilencedPatterns == 0 && ExpiredPatterns == 0
        ? "Tipos de problema que las auditorías de esta aplicación no reportan. Ninguno por ahora."
        : $"{SilencedPatterns} tipo(s) de problema que las auditorías de esta aplicación no reportan"
          + (ExpiredPatterns > 0
              ? $" · {ExpiredPatterns} caducado(s) que ya no suprimen: revísalos."
              : ". Es por-aplicación: el mismo tipo puede ser crítico en otra.");

    /// <summary>
    /// Directivas ACTIVAS del proyecto (F7). Va en el panel del ciclo, junto a los patrones
    /// silenciados, por la misma razón que ellos: condiciona la lectura de todo lo demás. Una
    /// auditoría que conoce las convenciones deliberadas del proyecto no reporta lo mismo que una
    /// que las ignora, y quien lea la cobertura tiene que saber cuál de las dos está viendo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectivesTooltip))]
    private int _activeDirectives;

    /// <summary>Candidatos detectados en el clon que nadie ha curado todavía (F7 §1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectivesTooltip))]
    private int _newDirectiveCandidates;

    public string DirectivesTooltip => ActiveDirectives == 0 && NewDirectiveCandidates == 0
        ? "Los ficheros de convenciones del proyecto —AGENTS.md, ADRs, specs, skills— que informan al "
          + "auditor y al arreglo. Ninguno activo por ahora."
        : $"{ActiveDirectives} directiva(s) activa(s) informando al auditor y al arreglo"
          + (NewDirectiveCandidates > 0
              ? $" · {NewDirectiveCandidates} candidato(s) detectado(s) sin activar: decídelos tú."
              : ". Su contenido se lee del clon en cada uso: siempre viaja la versión vigente.");

    /// <summary>El ciclo con su fecha: «Ciclo 5 · iniciado 12 ago 2026» (F5.6 §5).</summary>
    [ObservableProperty] private string _cycleLabel = "Ciclo 1";

    /// <inheritdoc cref="CycleSummary.Tooltip"/>
    [ObservableProperty] private string _cycleTooltip = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isEmpty;

    /// <summary>
    /// Si esta máquina tiene el clon de la app (F5.8 §3). El inventario se ABRE siempre —ver
    /// estados, quién audita y el resumen del ciclo no necesita el código—, pero lo que LANZA
    /// una auditoría o lee ficheros del clon queda deshabilitado y dice por qué.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAudit))]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    [NotifyPropertyChangedFor(nameof(ReadOnlyNotice))]
    [NotifyPropertyChangedFor(nameof(LinkActionLabel))]
    [NotifyPropertyChangedFor(nameof(AuditDisabledTooltip))]
    private CloneLink _link = CloneLink.Unknown(string.Empty);

    /// <inheritdoc cref="CloneLink.CanAudit"/>
    public bool CanAudit => Link.CanAudit;

    /// <summary>La barra de solo-lectura solo existe cuando de verdad lo es.</summary>
    public bool IsReadOnly => !Link.CanAudit;

    /// <summary>Lo que se lee en esa barra: el estado y lo que sigue siendo posible sin clon.</summary>
    /// <remarks>
    /// El salto de línea no es cosmético: el diagnóstico del caso ámbar termina en las DOS URLs,
    /// cada una en su renglón, y pegarle la frase siguiente a continuación la hacía leerse como
    /// parte de la última URL.
    /// </remarks>
    public string ReadOnlyNotice => Link.State == CloneLinkState.Problema
        ? $"Solo lectura: {Link.Problem}\nHallazgos, métricas e informes siguen accesibles."
        : "Solo lectura: no tienes un clon local de esta aplicación en esta máquina. "
          + "Puedes ver estados, quién audita, hallazgos, métricas e informes; para auditar hace falta el código.";

    /// <inheritdoc cref="CloneLink.ActionLabel"/>
    public string LinkActionLabel => Link.ActionLabel;

    /// <inheritdoc cref="CloneLink.DisabledActionTooltip"/>
    public string AuditDisabledTooltip => Link.DisabledActionTooltip;

    /// <summary>
    /// Cuántas unidades hay marcadas EN TODO EL CICLO. Es el número que no puede mentir: lanzar
    /// una auditoría cuesta dinero, y con 900 unidades nadie las cuenta a ojo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionLabel))]
    private int _selectedCount;

    /// <summary>La barra de selección solo existe cuando hay algo seleccionado.</summary>
    [ObservableProperty] private bool _hasSelection;

    /// <summary>Simétrico: si ya están todas marcadas, lo útil es lo contrario (F5.6 §3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingToggleTooltip))]
    private string _pendingToggleLabel = "Seleccionar pendientes";

    public string SelectionLabel => SelectedCount == 1
        ? "1 unidad seleccionada"
        : $"{SelectedCount} unidades seleccionadas";

    /// <summary>
    /// Qué es una unidad y, si las hay, dónde se han metido las «grandes». El recuento de grandes
    /// salió del panel por decisión del usuario (F5.6 §5) —no aporta a ese nivel—, pero sin
    /// decirlo en algún sitio el panel deja de cuadrar: auditadas + pendientes no suman el total.
    /// </summary>
    public string UnitsTooltip
    {
        get
        {
            const string What = "Cada fichero que se audita por separado; «Re-escanear» las pone al día.";
            return LargeUnits switch
            {
                0 => What,
                1 => What + " Una es demasiado grande para auditarla de una vez: sale marcada "
                          + "«Grande» en la lista, no cuenta como pendiente y no impide cerrar el ciclo.",
                _ => What + $" {LargeUnits} son demasiado grandes para auditarlas de una vez: salen "
                          + "marcadas «Grande» en la lista, no cuentan como pendientes y no impiden "
                          + "cerrar el ciclo.",
            };
        }
    }

    public string PendingToggleTooltip => PendingUnits == 1
        ? "Actúa sobre la única unidad pendiente del ciclo, esté o no a la vista."
        : $"Actúa sobre las {PendingUnits} unidades pendientes del ciclo, estén o no a la vista.";

    /// <inheritdoc cref="GroupCollapse.AllCollapsed"/>
    public bool AllCollapsed => _collapse.AllCollapsed;

    /// <inheritdoc cref="GroupCollapse.ToggleAllLabel"/>
    public string ToggleAllLabel => _collapse.ToggleAllLabel;

    /// <inheritdoc cref="GroupCollapse.HasGroups"/>
    public bool HasGroups => _collapse.HasGroups;

    public void SetApp(string slug) => Slug = slug;

    /// <summary>
    /// Deja UNA unidad marcada y buscada al entrar (desde el mapa de calor, F10 §2). No lanza
    /// nada: el gasto se confirma donde siempre, en «Auditar selección».
    /// <para>
    /// Marca <b>solo esa</b> —limpia lo que hubiera— porque venir del mapa es venir a por una
    /// unidad concreta, y heredar una selección anterior invisible es exactamente cómo se lanza y
    /// se paga una auditoría que nadie pidió (F5.13).
    /// </para>
    /// </summary>
    public void Preselect(string path)
    {
        _selected.Clear();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _selected.Add(path);
            SearchText = path;
        }
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    public override Task LoadAsync()
    {
        Rebuild();
        return Task.CompletedTask;
    }

    private void Rebuild()
    {
        Modules.Clear();
        AppConfig? app = string.IsNullOrEmpty(Slug) ? null : _hub.Store.TryReadApp(Slug);
        if (app is null)
        {
            _collapse.Adopt(Array.Empty<ModuleNode>());
            Link = CloneLink.Unknown(Slug);
            IsEmpty = true;
            return;
        }

        AppName = app.Name;
        CycleN = app.CurrentCycle;

        // Se recalcula en cada reconstrucción, que es lo que corre al entrar, al sincronizar y al
        // volver la ventana al primer plano (F5.8 §1). Un vínculo roto entre dos vistas de la
        // misma página no puede quedar diciendo que se puede auditar.
        Link = _links.For(app);
        InventoryCycle? inv = _hub.Store.TryReadInventory(Slug, CycleN);
        var claims = _hub.Store.ListClaims(Slug)
            .Where(c => !c.IsExpiredAt(DateTimeOffset.UtcNow))
            .ToDictionary(c => c.UnitHash, c => c.By);

        var units = inv?.Units ?? new List<InventoryUnit>();
        _allUnits = units;
        TotalUnits = units.Count;
        AuditedUnits = units.Count(u => u.State == UnitState.Auditada);
        LargeUnits = units.Count(u => u.State == UnitState.Grande);
        PendingUnits = units.Count(u => u.State == UnitState.Pendiente);
        OnPropertyChanged(nameof(PendingToggleTooltip));

        // F5.12: vivos y caducados se cuentan por separado porque significan cosas distintas —
        // uno vivo suprime, uno caducado solo pide una decisión.
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var patterns = _hub.Store.ListPatternSilences(Slug);
        SilencedPatterns = patterns.Count(p => p.IsLiveAt(nowUtc));
        ExpiredPatterns = patterns.Count(p => p.IsExpiredAt(nowUtc));

        // F7: activas y candidatos-sin-curar se cuentan por separado. Un candidato NO cuenta como
        // directiva: detectar no es activar, y el panel tiene que decir cuántas decisiones esperan
        // a alguien sin sugerir que ya se han tomado.
        var directives = _hub.Store.ListDirectives(Slug);
        ActiveDirectives = directives.Count(d => d.IsActive);
        NewDirectiveCandidates = _directives.NewCandidates(Slug, Link.Path).Count;

        var sessions = _hub.Store.ListSessions(Slug);
        CycleStart start = CycleSummary.StartOf(sessions, CycleN);
        CycleLabel = CycleSummary.Label(CycleN, start);
        CycleTooltip = CycleSummary.Tooltip(start);
        SessionCount = CycleSummary.LaunchesIn(sessions, CycleN);

        // Un re-escaneo o un reset pueden hacer desaparecer unidades: lo seleccionado se poda
        // contra el inventario vigente para que el contador nunca cuente fantasmas.
        _selected.IntersectWith(units.Select(u => u.Path));

        // La misma consulta que alimenta el mapa de calor: el color de una unidad no puede
        // depender de en qué vista se mire (F10.1 §1).
        bool dark = !string.Equals(_settings.Current.Theme, "light", StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, HeatUnit> heat = _heat.ByUnit(Slug);

        string search = SearchText.Trim();
        IEnumerable<InventoryUnit> filtered = string.IsNullOrEmpty(search)
            ? units
            : units.Where(u => u.Path.Contains(search, StringComparison.OrdinalIgnoreCase));

        var built = new List<ModuleNode>();
        foreach (var group in filtered.GroupBy(u => u.Module).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var node = new ModuleNode { Name = group.Key, Slug = Slug };
            foreach (InventoryUnit u in group.OrderBy(u => u.Path, StringComparer.Ordinal))
            {
                var unit = new UnitNode
                {
                    Path = u.Path,
                    Module = u.Module,
                    Loc = u.Loc,
                    State = u.State,
                    ClaimedBy = claims.TryGetValue(u.UnitHash, out string? by) ? by : null,
                };

                if (heat.TryGetValue(u.Path, out HeatUnit? measured))
                {
                    unit.DensityBrush = measured.Density is { } d
                        ? HeatBrushes.Solid(DensityScale.StepOf(d, HeatMetric.Densidad)!.For(dark))
                        : HeatBrushes.Solid(DensityScale.Unknown.For(dark));
                    unit.DensityTooltip = measured.Density is { } density
                        ? $"Densidad de deuda: {density.ToString("0.#", AppCulture.Display)} por KLOC "
                          + $"({measured.KnownDebt} de deuda en {measured.Loc} líneas)"
                        : "Densidad DESCONOCIDA: nadie ha auditado esta unidad todavía.";
                }

                // Se restaura antes de enganchar el aviso: reconstruir la vista no es seleccionar.
                unit.SetSelectedQuietly(_selected.Contains(u.Path));
                unit.SelectionChanged = OnUnitSelectionChanged;
                node.Units.Add(unit);
            }

            node.RefreshCheckState();
            node.SelectionRequested = OnModuleSelectionRequested;
            built.Add(node);
        }

        _collapse.Adopt(built);
        foreach (ModuleNode node in built)
        {
            Modules.Add(node);
        }

        RefreshSelectionState();
        IsEmpty = Modules.Count == 0;
    }

    /// <summary>Los nodos que están a la vista. Lo que se ve, no lo que hay.</summary>
    private IEnumerable<UnitNode> VisibleUnits => Modules.SelectMany(m => m.Units);

    private void OnUnitSelectionChanged(UnitNode unit)
    {
        if (unit.IsSelected)
        {
            _selected.Add(unit.Path);
        }
        else
        {
            _selected.Remove(unit.Path);
        }

        foreach (ModuleNode module in Modules)
        {
            if (module.Name == unit.Module)
            {
                module.RefreshCheckState();
            }
        }

        RefreshSelectionState();
    }

    /// <summary>
    /// El gesto de la casilla del módulo (F5.13). La decisión de marcar o limpiar la toma el nodo;
    /// aquí se aplica sobre el conjunto de seleccionadas, que es el único dueño de la selección.
    /// <para>
    /// Un módulo NO es una unidad: lo que entra en el conjunto son SIEMPRE las rutas de sus hijas.
    /// El grupo no tiene ruta y no puede llegar nunca a la lista de lanzamiento.
    /// </para>
    /// </summary>
    private void OnModuleSelectionRequested(ModuleNode module, bool select)
    {
        foreach (UnitNode unit in module.Units)
        {
            if (select)
            {
                _selected.Add(unit.Path);
            }
            else
            {
                _selected.Remove(unit.Path);
            }
        }

        SyncVisibleFromSelection();
    }

    /// <summary>
    /// <b>LA lista.</b> Las unidades-hoja marcadas, y nada más: ni módulos, ni nodos de grupo, ni
    /// rutas que ya no estén en el inventario vigente.
    /// <para>
    /// Existe porque el incidente del 2026-08-26 enseñó lo que cuesta tener dos expresiones de «lo
    /// seleccionado»: el contador decía una cosa y lo que se lanzaba podía ser otra, y la diferencia
    /// se paga en tokens. La consumen los TRES sitios que necesitan saberlo —el contador de la
    /// barra, el diálogo de confirmación y la petición que va al coordinador—, así que no pueden
    /// discrepar ni aunque alguien lo intente.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SelectedUnits()
        => _allUnits
            .Where(u => _selected.Contains(u.Path))
            .Select(u => u.Path)
            .ToList();

    /// <summary>
    /// Pone al día lo que lee la barra: el contador, su visibilidad y el sentido del botón de
    /// pendientes. El contador sale de <see cref="SelectedUnits"/> — de la MISMA lista que se va a
    /// auditar— y no de un recuento paralelo.
    /// </summary>
    private void RefreshSelectionState()
    {
        SelectedCount = SelectedUnits().Count;
        HasSelection = SelectedCount > 0;

        var pending = PendingPaths();
        bool allPendingSelected = pending.Count > 0 && pending.All(_selected.Contains);
        PendingToggleLabel = allPendingSelected ? "Deseleccionar pendientes" : "Seleccionar pendientes";
    }

    private List<string> PendingPaths()
        => _allUnits.Where(u => u.State == UnitState.Pendiente).Select(u => u.Path).ToList();

    /// <summary>
    /// Marca o desmarca TODAS las pendientes del ciclo, a la vista o no. Es la acción global que
    /// hace pareja con «Deseleccionar todo»; para recortar a un trozo está la casilla del módulo.
    /// </summary>
    [RelayCommand]
    private void SelectPending()
    {
        var pending = PendingPaths();
        if (pending.Count == 0)
        {
            _toasts.Show("No queda ninguna unidad pendiente en este ciclo.");
            return;
        }

        bool select = !pending.All(_selected.Contains);
        foreach (string path in pending)
        {
            if (select)
            {
                _selected.Add(path);
            }
            else
            {
                _selected.Remove(path);
            }
        }

        SyncVisibleFromSelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _selected.Clear();
        SyncVisibleFromSelection();
    }

    /// <summary>
    /// Baja el conjunto de seleccionadas al árbol visible. Silencioso por nodo —el conjunto ya es
    /// la verdad— y luego se recalcula todo de una vez, en vez de una por casilla.
    /// </summary>
    private void SyncVisibleFromSelection()
    {
        foreach (UnitNode unit in VisibleUnits)
        {
            unit.SetSelectedQuietly(_selected.Contains(unit.Path));
        }

        foreach (ModuleNode module in Modules)
        {
            module.RefreshCheckState();
        }

        RefreshSelectionState();
    }

    /// <summary>
    /// El clic sobre la casilla del módulo. Existe como comando —y no solo como efecto de escribir
    /// <c>IsChecked</c>— porque la casilla ya no recibe el tri-estado: pinta
    /// <see cref="ModuleNode.IsAllSelected"/>, que es de dos estados, así que el gesto tiene que
    /// llegar por su propia vía en vez de deducirse de un valor que la vista ya no escribe.
    /// </summary>
    [RelayCommand]
    private void ToggleModule(ModuleNode? group) => group?.RequestToggle();

    /// <inheritdoc cref="GroupCollapse.Toggle"/>
    [RelayCommand]
    private void ToggleGroup(ModuleNode? group) => _collapse.Toggle(group);

    /// <inheritdoc cref="GroupCollapse.ToggleAll"/>
    [RelayCommand]
    private void ToggleAllGroups() => _collapse.ToggleAll();

    /// <summary>
    /// Abre la gestión de patrones silenciados de esta app (F5.12). No pide clon ni permiso: es
    /// gobernanza, y la gobernanza se lee y se edita sin tener el código delante (F5.8 §3).
    /// Al cerrarla se reconstruye la página, así que el contador refleja lo que se acaba de hacer.
    /// </summary>
    [RelayCommand]
    private void ManagePatternSilences()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        var vm = new PatternSilencesViewModel(_hub, _governance, _toasts);
        vm.Load(Slug);
        _patternsDialog.Show(vm);
        Rebuild();
    }

    /// <summary>
    /// Abre la gestión de directivas del proyecto de esta app (F7 §1). A diferencia de la de
    /// patrones, esta SÍ quiere el clon —el catálogo busca en el repositorio y la vista previa lee
    /// el fichero—, pero se abre igualmente sin él: lo ya registrado se puede leer y desactivar sin
    /// tener el código delante, y el panel dice con todas las letras que no se ha podido mirar.
    /// </summary>
    [RelayCommand]
    private void ManageDirectives()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        var vm = new DirectivesViewModel(_directives, _hub, _toasts);
        vm.Load(Slug, Link.Path);
        _directivesDialog.Show(vm);
        Rebuild();
    }

    /// <summary>
    /// Abre «Vincular clon local…» / «Reparar vínculo…» sin salir del inventario (F5.8 §3): el
    /// acceso directo que acompaña a cada acción deshabilitada. Al volver, la página se
    /// reconstruye, y con ella el modo solo-lectura.
    /// </summary>
    [RelayCommand]
    private void LinkClone()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        _linkFlow.Run(Slug);
        Rebuild();
    }

    [RelayCommand]
    private async Task Rescan()
    {
        // Re-escanear LEE el clon: sin él no hay nada que escanear. Se comprueba contra el mismo
        // estado que pinta el piloto, no con una comprobación propia (F5.8 §1).
        if (!CanAudit)
        {
            _toasts.Show(AuditDisabledTooltip);
            return;
        }

        string clone = Link.Path!;

        IsBusy = true;
        _toasts.Show("Re-escaneando…");
        try
        {
            RescanOutcome outcome = await Task.Run(() => _rescan.Rescan(Slug, clone));
            // F5.16: lo que le pasó a los hallazgos medidos se DICE. Un hallazgo que se resuelve en
            // silencio se lee como un hallazgo que ha desaparecido, y eso costó una investigación.
            // F7 §1: los candidatos nuevos se ANUNCIAN, no se activan. El aviso es informativo a
            // propósito — «se han detectado» y no «se han añadido»— porque quien lo lee tiene que
            // entender que todavía no ha pasado nada y que la decisión sigue siendo suya.
            string directives = outcome.Candidates.Count == 0
                ? string.Empty
                : $" · {outcome.Candidates.Count} fichero(s) de directivas detectado(s), sin activar: "
                  + "revísalos en «Directivas · Gestionar»";

            _toasts.Show((outcome.Measured.Total > 0
                ? $"Inventario actualizado · {outcome.Measured.Summary}"
                : "Inventario actualizado") + directives + ".");
        }
        catch (Exception ex)
        {
            _toasts.Show($"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    [RelayCommand]
    private async Task ResetCycle()
    {
        AppConfig? app = _hub.Store.TryReadApp(Slug);
        InventoryCycle? current = app is null ? null : _hub.Store.TryReadInventory(Slug, app.CurrentCycle);
        if (app is null || current is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                int next = app.CurrentCycle + 1;
                var fresh = new InventoryCycle { CycleN = next };
                foreach (InventoryUnit u in current.Units)
                {
                    // Nothing is deleted; large units are re-evaluated against the threshold (§5.5).
                    bool large = u.Loc > app.Thresholds.LargeUnitLoc;
                    fresh.Units.Add(new InventoryUnit
                    {
                        Path = u.Path,
                        Module = u.Module,
                        Loc = u.Loc,
                        ContentHash = u.ContentHash,
                        State = large ? UnitState.Grande : UnitState.Pendiente,
                    });
                }

                app.CurrentCycle = next;
                _hub.Store.WriteApp(app);
                _hub.Store.WriteInventory(Slug, fresh);
                _hub.Store.WriteSession(new AuditSession
                {
                    Id = _ulids.NewUlid(),
                    AppSlug = Slug,
                    Mode = AuditMode.Reset,
                    By = _hub.ResolveIdentity().Name,
                    Machine = Environment.MachineName,
                    StartedUtc = DateTimeOffset.UtcNow,
                    EndedUtc = DateTimeOffset.UtcNow,
                    CycleN = next,
                });
                _hub.Sync?.CommitAndPush($"reset: {Slug} nuevo ciclo {next}");
            });

            _toasts.Show("Ciclo reiniciado. Nada se ha borrado.");
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    [RelayCommand]
    private Task AuditSelection()
    {
        // La MISMA lista que cuenta la barra (F5.13). No se vuelve a derivar aquí: derivarla dos
        // veces es exactamente cómo el contador y el lanzamiento acabaron diciendo cosas distintas.
        IReadOnlyList<string> selected = SelectedUnits();

        if (selected.Count == 0)
        {
            _toasts.Show("Selecciona al menos una unidad (o usa «Seleccionar pendientes»).");
            return Task.CompletedTask;
        }

        return LaunchSession(AuditMode.Lotes, selected);
    }

    [RelayCommand]
    private Task ShowFindings() => _navigation.NavigateToAsync<FindingsViewModel>(vm => vm.SetApp(Slug));

    /// <summary>
    /// El tope de pasadas vigente, que es lo que multiplica el gasto. Vive en los ajustes de la
    /// máquina (D-095), no en la app auditada.
    /// </summary>
    private int MaxPasses => Math.Max(1, _settings.Current.MaxPassesPerUnit);

    /// <summary>
    /// Estima el coste de auditar esas unidades. Público para que la verificación humana y los
    /// tests puedan leer el mismo número que verá el diálogo.
    /// </summary>
    public CostEstimate EstimateFor(int units) => _costs.Estimate(Slug, units, MaxPasses);

    /// <summary>
    /// Desde cuántas unidades se pregunta. Configurable por app; por defecto 3 — el clic de más
    /// solo se justifica cuando el gasto es relevante (F5.6 §4).
    /// </summary>
    private int ConfirmThreshold
        => Math.Max(0, _hub.Store.TryReadApp(Slug)?.Thresholds.ConfirmLaunchUnits ?? 3);

    /// <summary>
    /// Lanzar es un acto EXPLÍCITO (F5.2): se arranca la sesión en el servicio y luego se navega a
    /// V5 para verla. Antes se navegaba y la vista arrancaba la sesión en su <c>LoadAsync</c>, de
    /// modo que cualquier recarga de página ejecutaba una auditoría entera — el bucle de D-085.
    /// <para>
    /// Desde F5.6 §4, por encima del umbral se pregunta antes, enseñando el gasto estimado. La
    /// estimación informa; el botón de confirmar nunca se bloquea por ella.
    /// </para>
    /// </summary>
    private async Task LaunchSession(AuditMode mode, IReadOnlyList<string> paths)
    {
        // La puerta de F5.8 §3, en el MODELO y no solo en el XAML: un botón gris es una cortesía
        // de la vista; auditar sin clon escribiría hallazgos sobre un código que no está.
        if (!CanAudit)
        {
            _toasts.Show(AuditDisabledTooltip);
            return;
        }

        if (paths.Count == 0)
        {
            _toasts.Show("No hay unidades para auditar.");
            return;
        }

        if (_live.IsRunning)
        {
            _toasts.Show("Ya hay una sesión en curso. Ábrela desde «Sesión en vivo».");
            await _navigation.NavigateToAsync<SessionViewModel>();
            return;
        }

        if (paths.Count > ConfirmThreshold)
        {
            var confirmation = new AuditLaunchConfirmation(AppName, EstimateFor(paths.Count));
            if (!_confirmer.Confirm(confirmation))
            {
                _toasts.Show("Lanzamiento cancelado. La selección sigue como estaba.");
                return;
            }
        }

        // El N que el usuario ha visto y aceptado viaja con la petición (F5.13). El coordinador lo
        // vuelve a comprobar contra lo que de verdad va a auditar: si alguna vez vuelven a
        // discrepar, la sesión muere antes de la primera llamada al modelo y no antes de la
        // factura. Es una salvaguarda de última línea, no la corrección — la corrección es que
        // contador y lista sean el mismo método.
        _ = _live.StartAsync(new SessionRequest(Slug, mode, paths, paths.Count), paths);
        await _navigation.NavigateToAsync<SessionViewModel>();
    }
}
