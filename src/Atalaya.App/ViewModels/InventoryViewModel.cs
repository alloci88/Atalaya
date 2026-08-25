using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>V2 Inventory (§8): module→unit tree with state, claims, filters, and cycle actions.</summary>
public sealed partial class InventoryViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly InventoryScanner _scanner;
    private readonly MachineConfigStore _machines;
    private readonly IUlidFactory _ulids;
    private readonly NavigationService _navigation;
    private readonly LiveSessionService _live;
    private readonly SettingsService _settings;
    private readonly CostEstimator _costs;
    private readonly IAuditLaunchConfirmer _confirmer;

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

    public InventoryViewModel(
        HubContext hub, InventoryScanner scanner, MachineConfigStore machines,
        IUlidFactory ulids, NavigationService navigation, LiveSessionService live,
        SettingsService settings, CostEstimator costs, IAuditLaunchConfirmer confirmer,
        GroupExpansionMemory expansion, ToastCenter toasts)
    {
        _hub = hub;
        _scanner = scanner;
        _machines = machines;
        _ulids = ulids;
        _navigation = navigation;
        _live = live;
        _settings = settings;
        _costs = costs;
        _confirmer = confirmer;
        _toasts = toasts;
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

    /// <summary>El ciclo con su fecha: «Ciclo 5 · iniciado 12 ago 2026» (F5.6 §5).</summary>
    [ObservableProperty] private string _cycleLabel = "Ciclo 1";

    /// <inheritdoc cref="CycleSummary.Tooltip"/>
    [ObservableProperty] private string _cycleTooltip = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isEmpty;

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
            IsEmpty = true;
            return;
        }

        AppName = app.Name;
        CycleN = app.CurrentCycle;
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

        var sessions = _hub.Store.ListSessions(Slug);
        CycleStart start = CycleSummary.StartOf(sessions, CycleN);
        CycleLabel = CycleSummary.Label(CycleN, start);
        CycleTooltip = CycleSummary.Tooltip(start);
        SessionCount = CycleSummary.LaunchesIn(sessions, CycleN);

        // Un re-escaneo o un reset pueden hacer desaparecer unidades: lo seleccionado se poda
        // contra el inventario vigente para que el contador nunca cuente fantasmas.
        _selected.IntersectWith(units.Select(u => u.Path));

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

                // Se restaura antes de enganchar el aviso: reconstruir la vista no es seleccionar.
                unit.SetSelectedQuietly(_selected.Contains(u.Path));
                unit.SelectionChanged = OnUnitSelectionChanged;
                node.Units.Add(unit);
            }

            node.RefreshCheckState();
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
    /// Pone al día lo que lee la barra: el contador, su visibilidad y el sentido del botón de
    /// pendientes. Todo sale de <see cref="_selected"/>, que es el estado real, y no del árbol.
    /// </summary>
    private void RefreshSelectionState()
    {
        SelectedCount = _selected.Count;
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

    /// <inheritdoc cref="GroupCollapse.Toggle"/>
    [RelayCommand]
    private void ToggleGroup(ModuleNode? group) => _collapse.Toggle(group);

    /// <inheritdoc cref="GroupCollapse.ToggleAll"/>
    [RelayCommand]
    private void ToggleAllGroups() => _collapse.ToggleAll();

    [RelayCommand]
    private async Task Rescan()
    {
        string? clonePath = _machines.Load().ClonePathFor(Slug);
        if (string.IsNullOrWhiteSpace(clonePath) || !Directory.Exists(clonePath))
        {
            _toasts.Show("No hay clon local configurado para esta app en esta máquina.");
            return;
        }

        string clone = clonePath;

        AppConfig? app = _hub.Store.TryReadApp(Slug);
        if (app is null)
        {
            return;
        }

        IsBusy = true;
        _toasts.Show("Re-escaneando…");
        try
        {
            await Task.Run(() =>
            {
                ScanOutput scan = _scanner.Scan(clone, app, app.CurrentCycle);
                InventoryCycle? previous = _hub.Store.TryReadInventory(Slug, app.CurrentCycle);
                InventoryCycle merged = previous is null
                    ? scan.Inventory
                    : Rescanner.Reconcile(previous, scan.Inventory).Merged;

                _hub.Store.WriteInventory(Slug, merged);
                _hub.Sync?.CommitAndPush($"inventory: rescan {Slug} cycle {app.CurrentCycle}");
            });

            _toasts.Show("Inventario actualizado.");
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
        var selected = _allUnits
            .Where(u => _selected.Contains(u.Path))
            .Select(u => u.Path)
            .ToList();

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

        _ = _live.StartAsync(new SessionRequest(Slug, mode, paths), paths);
        await _navigation.NavigateToAsync<SessionViewModel>();
    }
}
