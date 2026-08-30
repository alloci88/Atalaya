using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Qué recorte del ciclo de vida enseña la lista. «Todos» siempre existe (F5.4).</summary>
public enum FindingsScope
{
    Activos,
    Resueltos,
    Silenciados,
    Todos,
}

/// <summary>
/// Una opción de un combo de filtro. Todos los combos de V3 siguen el mismo patrón: la primera
/// opción es «Todas/Todos», vale <c>null</c> (o el valor neutro) y es el arranque. Sin ella,
/// filtrar era un viaje sin billete de vuelta.
/// </summary>
public sealed record AppFilterOption(string? Slug, string Label)
{
    public override string ToString() => Label;
}

/// <inheritdoc cref="AppFilterOption"/>
public sealed record SeverityFilterOption(Severity? Value, string Label)
{
    public override string ToString() => Label;
}

/// <inheritdoc cref="AppFilterOption"/>
public sealed record ScopeFilterOption(FindingsScope Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Un conteo por severidad, para el resumen de la cabecera de grupo.</summary>
public sealed record SeverityChip(Severity Severity, int Count)
{
    public string Label => $"{Count} {SeverityNames.Display(Severity)}";
}

/// <summary>
/// Elemento de la lista plana de V3: o una cabecera de unidad o un hallazgo. La lista se aplana
/// para que la virtualización siga siendo por FILA — agrupar con contenedores anidados la habría
/// convertido en virtualización por grupo, que con una app real no virtualiza nada.
/// </summary>
public abstract class FindingsListItem : ObservableObject
{
}

/// <summary>Un hallazgo en la lista V3. Solo lectura: la fila entera es un enlace a V4 (F5.4).</summary>
public sealed class FindingRow : FindingsListItem
{
    public required string Slug { get; init; }
    public required string AppName { get; init; }
    public required Ulid Id { get; init; }
    public string? DisplayId { get; init; }
    public required string Title { get; init; }
    public Severity Severity { get; init; }
    public Confidence Confidence { get; init; }
    public FindingStatus Status { get; init; }
    public Pillar Pillar { get; init; }
    public string? Assignee { get; init; }

    /// <summary>La unidad (fichero) que agrupa el hallazgo: la ruta de su primera localización.</summary>
    public required string UnitPath { get; init; }

    public int Line { get; init; }

    /// <summary>Cuántas localizaciones tiene además de la principal.</summary>
    public int ExtraLocations { get; init; }

    public int DaysSinceConfirmed { get; init; }
    public bool NeedsReview { get; init; }
    public bool IsStale { get; init; }

    /// <summary>Cuántos auditores sostienen que esto nunca fue un defecto (F5.1b).</summary>
    public int DisputeCount { get; init; }

    /// <summary>Cuántos MODELOS distintos discrepan. Tres es una señal muy fuerte.</summary>
    public int DisputingModels { get; init; }

    public bool IsDisputed => DisputeCount > 0;

    /// <summary>Etiqueta de la marca de disputa, para el tooltip.</summary>
    public string DisputeLabel => DisputeCount == 0
        ? string.Empty
        : DisputingModels > 1
            ? $"Disputado por {DisputingModels} modelos distintos: ninguno cree que sea un defecto."
            : "Un auditor sostiene que esto nunca fue un defecto.";

    /// <summary>
    /// Lo que se LEE en la marca. La balanza lleva el selector de presentación de texto (U+FE0E)
    /// porque en presentación emoji sale como un borrón dorado que ignora el color del texto: sobre
    /// el ámbar del badge no se distinguía nada. La palabra va al lado porque un icono solo, de
    /// 11 px y en una esquina, no dice qué pasa.
    /// </summary>
    public string DisputeBadge => DisputeCount == 0
        ? string.Empty
        : DisputingModels > 1
            ? $"⚖︎ Disputado ×{DisputingModels}"
            : "⚖︎ Disputado";

    public string LocationLabel => ExtraLocations > 0
        ? $"L{Line} +{ExtraLocations} más"
        : $"L{Line}";

    /// <summary>
    /// Segunda línea, atenuada. El estado solo aparece cuando NO es «Activo»: con el filtro en
    /// «Todos», una lista sin estado mezcla resueltos y silenciados sin decirlo.
    /// </summary>
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (Status != FindingStatus.Activo)
            {
                parts.Add(Status.ToString());
            }

            if (!string.IsNullOrWhiteSpace(DisplayId))
            {
                parts.Add(DisplayId!);
            }

            parts.Add($"confianza {Confidence}");
            parts.Add(LocationLabel);
            return string.Join(" · ", parts);
        }
    }

    public bool HasAssignee => !string.IsNullOrWhiteSpace(Assignee);

    /// <summary>Iniciales para el avatar del asignado.</summary>
    public string AssigneeInitials
    {
        get
        {
            string who = Assignee?.Trim() ?? string.Empty;
            if (who.Length == 0)
            {
                return string.Empty;
            }

            string[] words = who.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 1
                ? string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]))
                : char.ToUpperInvariant(words[0][0]).ToString();
        }
    }

    public string FreshnessLabel => DaysSinceConfirmed switch
    {
        <= 0 => "hoy",
        1 => "ayer",
        _ => $"hace {DaysSinceConfirmed} días",
    };
}

/// <summary>
/// Cabecera de una unidad. La petición central de F5.4: que se lea claramente de qué clase habla
/// cada hallazgo, en vez de adivinarlo en una columna «Ubicación» recortada a 15 caracteres.
/// </summary>
public sealed partial class FindingGroupHeader : FindingsListItem, ICollapsibleGroup
{
    public required string Slug { get; init; }
    public required string AppName { get; init; }
    public required string UnitPath { get; init; }
    public required IReadOnlyList<FindingRow> Rows { get; init; }

    /// <summary>Con el filtro de aplicación en «Todas», la app va aquí — no en cada fila.</summary>
    public bool ShowApp { get; init; }

    [ObservableProperty]
    private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    /// <summary>Identidad estable del grupo, para recordar los plegados entre recargas.</summary>
    public string Key => $"{Slug} {UnitPath}";

    public string FileName
    {
        get
        {
            string name = Path.GetFileName(UnitPath.Replace('\\', '/'));
            return name.Length > 0 ? name : UnitPath;
        }
    }

    /// <summary>Ruta completa (y la app si procede), en pequeño y atenuado junto al nombre.</summary>
    public string Subtitle => ShowApp ? $"{AppName} · {UnitPath}" : UnitPath;

    public IReadOnlyList<SeverityChip> Chips { get; init; } = Array.Empty<SeverityChip>();

    /// <summary>La severidad más grave del grupo. Ordena los grupos (Critica = 0).</summary>
    public Severity WorstSeverity => Rows.Count == 0 ? Severity.Baja : Rows.Min(r => r.Severity);

    public int WorstCount => Rows.Count(r => r.Severity == WorstSeverity);

    public int DisputedCount => Rows.Count(r => r.IsDisputed);

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public string CountLabel => Rows.Count == 1 ? "1 hallazgo" : $"{Rows.Count} hallazgos";
}

/// <summary>
/// V3 Hallazgos (§8), rediseñada en F5.4. <b>La lista encuentra; el detalle actúa</b>: aquí no hay
/// NINGUNA acción de escritura — ni silenciar, ni asignar, ni verify, ni resolver disputas. Todo
/// eso vive en V4 (<see cref="FindingDetailViewModel"/>), donde la acción tiene contexto y autor.
/// V3 es buscar, filtrar, ordenar y abrir.
/// </summary>
public sealed partial class FindingsViewModel : ViewModelBase
{
    /// <summary>La opción neutra del combo de aplicación. Es el valor inicial.</summary>
    public static readonly AppFilterOption AllApps = new(null, "Todas");

    /// <summary>La opción neutra del combo de severidad. Es el valor inicial.</summary>
    public static readonly SeverityFilterOption AllSeverities = new(null, "Todas");

    private readonly HubContext _hub;
    private readonly NavigationService _navigation;
    private readonly SettingsService _settings;

    /// <summary>
    /// Plegar y desplegar: la MISMA lógica que usa V2 (F5.6 §1). Lo que el usuario decide a mano
    /// vive fuera del view-model, en <see cref="GroupExpansionMemory"/>, porque V3 se reconstruye
    /// en cada navegación.
    /// </summary>
    private readonly GroupCollapse _collapse;

    private string? _pendingAppSlug;
    private bool _hasPendingAppSlug;
    private Severity? _pendingSeverity;
    private bool _hasPendingSeverity;
    private string? _pendingSearch;
    private bool _suspendReload;

    public FindingsViewModel(
        HubContext hub, NavigationService navigation, SettingsService settings, GroupExpansionMemory expansion)
    {
        _hub = hub;
        _navigation = navigation;
        _settings = settings;
        _collapse = new GroupCollapse(expansion);

        // La cabecera sigue leyendo AllCollapsed/ToggleAllLabel/HasGroups en el view-model: los
        // nombres coinciden, así que reenviar el aviso basta para que el enlace siga vivo.
        _collapse.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);

        SeverityOptions = new List<SeverityFilterOption> { AllSeverities }
            .Concat(Enum.GetValues<Severity>().Select(s => new SeverityFilterOption(s, SeverityNames.Display(s))))
            .ToList();

        ScopeOptions = new List<ScopeFilterOption>
        {
            new(FindingsScope.Activos, "Activos"),
            new(FindingsScope.Resueltos, "Resueltos"),
            new(FindingsScope.Silenciados, "Silenciados"),
            new(FindingsScope.Todos, "Todos"),
        };

        _suspendReload = true;
        AppOptions.Add(AllApps);
        SelectedApp = AllApps;
        SelectedSeverity = AllSeverities;
        SelectedScope = ScopeOptions[0];
        _suspendReload = false;
    }

    public override string Title => "Hallazgos";

    /// <summary>Lo que pinta la lista: cabeceras y filas intercaladas, ya plegadas.</summary>
    public ObservableCollection<FindingsListItem> Items { get; } = new();

    /// <summary>Los grupos, en orden. La lista plana sale de aquí.</summary>
    public ObservableCollection<FindingGroupHeader> Groups { get; } = new();

    public ObservableCollection<AppFilterOption> AppOptions { get; } = new();

    public IReadOnlyList<SeverityFilterOption> SeverityOptions { get; }

    public IReadOnlyList<ScopeFilterOption> ScopeOptions { get; }

    [ObservableProperty] private AppFilterOption? _selectedApp;
    [ObservableProperty] private SeverityFilterOption? _selectedSeverity;
    [ObservableProperty] private ScopeFilterOption? _selectedScope;
    [ObservableProperty] private bool _onlyNeedsReview;
    [ObservableProperty] private bool _onlyDisputed;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private int _disputedCount;
    [ObservableProperty] private string _resultsSummary = "0 hallazgos";
    [ObservableProperty] private bool _hasActiveFilters;

    /// <inheritdoc cref="GroupCollapse.AllCollapsed"/>
    public bool AllCollapsed => _collapse.AllCollapsed;

    /// <inheritdoc cref="GroupCollapse.ToggleAllLabel"/>
    public string ToggleAllLabel => _collapse.ToggleAllLabel;

    /// <inheritdoc cref="GroupCollapse.HasGroups"/>
    public bool HasGroups => _collapse.HasGroups;

    partial void OnSelectedAppChanged(AppFilterOption? value) => Reload();
    partial void OnSelectedSeverityChanged(SeverityFilterOption? value) => Reload();
    partial void OnSelectedScopeChanged(ScopeFilterOption? value) => Reload();
    partial void OnOnlyNeedsReviewChanged(bool value) => Reload();
    partial void OnOnlyDisputedChanged(bool value) => Reload();
    partial void OnSearchTextChanged(string value) => Reload();

    /// <summary>
    /// Pre-selecciona una aplicación al navegar (desde V2). El combo aún no existe cuando esto se
    /// llama —<c>NavigateToAsync</c> inicializa antes de cargar—, así que se guarda y se aplica en
    /// <see cref="LoadAsync"/>.
    /// </summary>
    public void SetApp(string? slug)
    {
        _pendingAppSlug = slug;
        _hasPendingAppSlug = true;
    }

    /// <summary>
    /// Pre-selecciona una severidad al navegar (desde el rosco de severidad de Métricas, F6.5).
    /// Mismo mecanismo diferido que <see cref="SetApp"/>: los combos todavía no existen cuando
    /// esto se llama. <c>null</c> es «todas», que es lo que pide un clic en el centro del rosco.
    /// </summary>
    public void SetSeverity(Severity? severity)
    {
        _pendingSeverity = severity;
        _hasPendingSeverity = true;
    }

    public override Task LoadAsync()
    {
        RefreshAppOptions();
        ApplyPendingFilters();
        Reload();
        return Task.CompletedTask;
    }

    /// <summary>
    /// El combo se rellena con las apps del portafolio, precedidas de «Todas». Solo se reconstruye
    /// si el conjunto cambió: rehacerlo en cada tick del polling tiraría la selección del usuario.
    /// </summary>
    private void RefreshAppOptions()
    {
        var wanted = new List<AppFilterOption> { AllApps };
        foreach (string slug in _hub.Store.ListAppSlugs().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            wanted.Add(new AppFilterOption(slug, _hub.Store.TryReadApp(slug)?.Name ?? slug));
        }

        if (AppOptions.SequenceEqual(wanted))
        {
            return;
        }

        string? current = SelectedApp?.Slug;
        bool previous = _suspendReload;
        _suspendReload = true;
        AppOptions.Clear();
        foreach (AppFilterOption option in wanted)
        {
            AppOptions.Add(option);
        }

        SelectedApp = AppOptions.FirstOrDefault(o => o.Slug == current) ?? AllApps;
        _suspendReload = previous;
    }

    private void ApplyPendingFilters()
    {
        if (!_hasPendingAppSlug && !_hasPendingSeverity && _pendingSearch is null)
        {
            return;
        }

        bool previous = _suspendReload;
        _suspendReload = true;

        if (_pendingSearch is { } search)
        {
            _pendingSearch = null;
            SearchText = search;
        }

        if (_hasPendingAppSlug)
        {
            _hasPendingAppSlug = false;
            SelectedApp = AppOptions.FirstOrDefault(o => o.Slug == _pendingAppSlug) ?? AllApps;
        }

        if (_hasPendingSeverity)
        {
            _hasPendingSeverity = false;
            SelectedSeverity = SeverityOptions.FirstOrDefault(o => o.Value == _pendingSeverity) ?? AllSeverities;
        }

        _suspendReload = previous;
    }

    private void Reload()
    {
        if (_suspendReload)
        {
            return;
        }

        string? appFilter = SelectedApp?.Slug;
        Severity? severityFilter = SelectedSeverity?.Value;
        FindingsScope scope = SelectedScope?.Value ?? FindingsScope.Activos;
        string search = SearchText?.Trim() ?? string.Empty;
        bool showApp = appFilter is null;

        int freshness = _settings.Current.DefaultThresholds.FreshnessDays;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string[] slugs = appFilter is { Length: > 0 }
            ? new[] { appFilter }
            : _hub.Store.ListAppSlugs().ToArray();

        var rows = new List<FindingRow>();
        foreach (string slug in slugs)
        {
            string appName = _hub.Store.TryReadApp(slug)?.Name ?? slug;
            foreach (Finding f in _hub.Store.ListFindings(slug))
            {
                if (!MatchesScope(f, scope)
                    || (OnlyNeedsReview && !f.NeedsReview)
                    || (OnlyDisputed && f.Disputes.Count == 0)
                    || (severityFilter is { } sev && f.Severity != sev))
                {
                    continue;
                }

                Location loc = f.Locations.Count > 0 ? f.Locations[0] : new Location("(sin ubicación)", 0);
                if (search.Length > 0 && !Matches(f, loc, search))
                {
                    continue;
                }

                int days = (int)(now - f.LastConfirmed.Utc).TotalDays;
                rows.Add(new FindingRow
                {
                    Slug = slug,
                    AppName = appName,
                    Id = f.Id,
                    DisplayId = f.DisplayId,
                    Title = f.Title,
                    Severity = f.Severity,
                    Confidence = f.Confidence,
                    Status = f.Status,
                    Pillar = f.Pillar,
                    Assignee = f.Assignee,
                    UnitPath = loc.Path,
                    Line = loc.Line,
                    ExtraLocations = Math.Max(0, f.Locations.Count - 1),
                    DaysSinceConfirmed = days,
                    NeedsReview = f.NeedsReview,
                    IsStale = days > freshness,
                    DisputeCount = f.Disputes.Count,
                    DisputingModels = f.Disputes
                        .Select(d => d.Model ?? "(sin modelo)")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                });
            }
        }

        var built = BuildGroups(rows, showApp).ToList();
        _collapse.Adopt(built);

        Groups.Clear();
        foreach (FindingGroupHeader group in built)
        {
            Groups.Add(group);
        }

        Flatten();

        ResultCount = rows.Count;
        DisputedCount = rows.Count(r => r.IsDisputed);
        ResultsSummary = BuildSummary(ResultCount, DisputedCount);
        IsEmpty = rows.Count == 0;
        HasActiveFilters = appFilter is not null
            || severityFilter is not null
            || scope != FindingsScope.Activos
            || OnlyNeedsReview
            || OnlyDisputed
            || search.Length > 0;
    }

    private IEnumerable<FindingGroupHeader> BuildGroups(IReadOnlyList<FindingRow> rows, bool showApp)
        => rows
            .GroupBy(r => (r.Slug, r.UnitPath))
            .Select(g =>
            {
                var ordered = g
                    .OrderBy(r => r.Severity)
                    .ThenBy(r => r.Confidence)
                    .ThenByDescending(r => r.DaysSinceConfirmed)
                    .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new FindingGroupHeader
                {
                    Slug = g.Key.Slug,
                    AppName = ordered[0].AppName,
                    UnitPath = g.Key.UnitPath,
                    Rows = ordered,
                    ShowApp = showApp,
                    Chips = Enum.GetValues<Severity>()
                        .Select(s => new SeverityChip(s, ordered.Count(r => r.Severity == s)))
                        .Where(c => c.Count > 0)
                        .ToList(),
                };
            })
            .OrderBy(g => g.WorstSeverity)
            .ThenByDescending(g => g.WorstCount)
            .ThenBy(g => g.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.UnitPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Aplana grupos y filas en la lista virtualizada; los plegados solo aportan cabecera.</summary>
    private void Flatten()
    {
        Items.Clear();
        foreach (FindingGroupHeader group in Groups)
        {
            Items.Add(group);
            if (!group.IsExpanded)
            {
                continue;
            }

            foreach (FindingRow row in group.Rows)
            {
                Items.Add(row);
            }
        }
    }

    private static bool MatchesScope(Finding f, FindingsScope scope) => scope switch
    {
        FindingsScope.Activos => f.Status == FindingStatus.Activo,
        FindingsScope.Resueltos => f.Status == FindingStatus.Resuelto,
        FindingsScope.Silenciados => f.Status == FindingStatus.Silenciado,
        _ => true,
    };

    private static bool Matches(Finding f, Location loc, string search)
        => f.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
           || f.RuleId.Contains(search, StringComparison.OrdinalIgnoreCase)
           || loc.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
           || (f.DisplayId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string BuildSummary(int total, int disputed)
    {
        string head = total == 1 ? "1 hallazgo" : $"{total} hallazgos";
        return disputed == 0
            ? head
            : $"{head} · {(disputed == 1 ? "1 disputado" : $"{disputed} disputados")}";
    }

    /// <summary>Devuelve todos los combos y los interruptores a su valor inicial.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _suspendReload = true;
        SearchText = string.Empty;
        SelectedApp = AppOptions.FirstOrDefault(o => o.Slug is null) ?? AllApps;
        SelectedSeverity = AllSeverities;
        SelectedScope = ScopeOptions[0];
        OnlyNeedsReview = false;
        OnlyDisputed = false;
        _suspendReload = false;
        Reload();
    }

    [RelayCommand]
    private void ToggleGroup(FindingGroupHeader? group)
    {
        _collapse.Toggle(group);
        Flatten();
    }

    /// <inheritdoc cref="GroupCollapse.ToggleAll"/>
    [RelayCommand]
    private void ToggleAllGroups()
    {
        _collapse.ToggleAll();
        Flatten();
    }

    /// <summary>La fila entera es el enlace: abrir el detalle es lo ÚNICO que hace esta vista.</summary>
    [RelayCommand]
    private Task OpenDetail(FindingRow? row)
        => row is null
            ? Task.CompletedTask
            : _navigation.NavigateToAsync<FindingDetailViewModel>(vm => vm.Load(row.Slug, row.Id));
}
