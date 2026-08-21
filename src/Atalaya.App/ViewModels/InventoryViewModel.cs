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

    public InventoryViewModel(
        HubContext hub, InventoryScanner scanner, MachineConfigStore machines,
        IUlidFactory ulids, NavigationService navigation)
    {
        _hub = hub;
        _scanner = scanner;
        _machines = machines;
        _ulids = ulids;
        _navigation = navigation;
    }

    public override string Title => AppName is { Length: > 0 } ? $"Inventario · {AppName}" : "Inventario";

    public ObservableCollection<ModuleNode> Modules { get; } = new();

    [ObservableProperty] private string _slug = string.Empty;
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private int _cycleN = 1;
    [ObservableProperty] private int _totalUnits;
    [ObservableProperty] private int _auditedUnits;
    [ObservableProperty] private int _largeUnits;
    [ObservableProperty] private int _pendingUnits;
    [ObservableProperty] private int _sessionCount;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isEmpty;

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
        if (string.IsNullOrEmpty(Slug))
        {
            IsEmpty = true;
            return;
        }

        AppConfig? app = _hub.Store.TryReadApp(Slug);
        if (app is null)
        {
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
        TotalUnits = units.Count;
        AuditedUnits = units.Count(u => u.State == UnitState.Auditada);
        LargeUnits = units.Count(u => u.State == UnitState.Grande);
        PendingUnits = units.Count(u => u.State == UnitState.Pendiente);
        SessionCount = _hub.Store.ListSessions(Slug).Count;

        string search = SearchText.Trim();
        IEnumerable<InventoryUnit> filtered = string.IsNullOrEmpty(search)
            ? units
            : units.Where(u => u.Path.Contains(search, StringComparison.OrdinalIgnoreCase));

        foreach (var group in filtered.GroupBy(u => u.Module).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var node = new ModuleNode { Name = group.Key };
            foreach (InventoryUnit u in group.OrderBy(u => u.Path, StringComparer.Ordinal))
            {
                node.Units.Add(new UnitNode
                {
                    Path = u.Path,
                    Module = u.Module,
                    Loc = u.Loc,
                    State = u.State,
                    ClaimedBy = claims.TryGetValue(u.UnitHash, out string? by) ? by : null,
                });
            }

            Modules.Add(node);
        }

        IsEmpty = Modules.Count == 0;
    }

    private IEnumerable<UnitNode> AllUnits => Modules.SelectMany(m => m.Units);

    [RelayCommand]
    private void SelectPending()
    {
        foreach (UnitNode u in AllUnits)
        {
            u.IsSelected = u.State == UnitState.Pendiente;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (UnitNode u in AllUnits)
        {
            u.IsSelected = false;
        }
    }

    [RelayCommand]
    private async Task Rescan()
    {
        string? clonePath = _machines.Load().ClonePathFor(Slug);
        if (string.IsNullOrWhiteSpace(clonePath) || !Directory.Exists(clonePath))
        {
            StatusMessage = "No hay clon local configurado para esta app en esta máquina.";
            return;
        }

        string clone = clonePath;

        AppConfig? app = _hub.Store.TryReadApp(Slug);
        if (app is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Re-escaneando…";
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

            StatusMessage = "Inventario actualizado.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
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

            StatusMessage = "Ciclo reiniciado. Nada se ha borrado.";
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
        var selected = AllUnits.Where(u => u.IsSelected).Select(u => u.Path).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Selecciona al menos una unidad (o usa «Seleccionar pendientes»).";
            return Task.CompletedTask;
        }

        return LaunchSession(AuditMode.Lotes, selected);
    }

    [RelayCommand]
    private Task AuditIntegral()
        => LaunchSession(AuditMode.Integral, AllUnits.Where(u => u.State != UnitState.Grande).Select(u => u.Path).ToList());

    [RelayCommand]
    private Task AuditSuperficial()
    {
        // Superficial prioritises: domain > size > antiquity. Here: largest pending first (§5.3).
        var budget = AllUnits
            .Where(u => u.State == UnitState.Pendiente)
            .OrderByDescending(u => u.Loc)
            .Take(Math.Max(1, AllUnits.Count() / 4))
            .Select(u => u.Path)
            .ToList();
        return LaunchSession(AuditMode.Superficial, budget);
    }

    [RelayCommand]
    private Task ShowFindings() => _navigation.NavigateToAsync<FindingsViewModel>(vm => vm.SetApp(Slug));

    private Task LaunchSession(AuditMode mode, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            StatusMessage = "No hay unidades para auditar.";
            return Task.CompletedTask;
        }

        var request = new SessionRequest(Slug, mode, paths);
        return _navigation.NavigateToAsync<SessionViewModel>(vm => vm.Configure(request, paths));
    }
}
