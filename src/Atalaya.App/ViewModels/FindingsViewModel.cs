using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>A finding row in the V3 master table.</summary>
public sealed partial class FindingRow : ObservableObject
{
    public required string Slug { get; init; }
    public required Ulid Id { get; init; }
    public string? DisplayId { get; init; }
    public required string Title { get; init; }
    public Severity Severity { get; init; }
    public Confidence Confidence { get; init; }
    public FindingStatus Status { get; init; }
    public Pillar Pillar { get; init; }
    public string? Assignee { get; init; }
    public required string Location { get; init; }
    public int DaysSinceConfirmed { get; init; }
    public bool NeedsReview { get; init; }
    public bool IsStale { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>V3 Hallazgos (§8): master table across apps, sorted severity×confidence, with filters
/// and inline/bulk governance (silence with reason+expiry, assign, verify, open in editor).</summary>
public sealed partial class FindingsViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly GovernanceService _governance;
    private readonly VerifyCoordinator _verify;
    private readonly EditorLauncher _editor;
    private readonly NavigationService _navigation;
    private readonly SettingsService _settings;

    public FindingsViewModel(
        HubContext hub, GovernanceService governance, VerifyCoordinator verify,
        EditorLauncher editor, NavigationService navigation, SettingsService settings)
    {
        _hub = hub;
        _governance = governance;
        _verify = verify;
        _editor = editor;
        _navigation = navigation;
        _settings = settings;
    }

    public override string Title => "Hallazgos";

    public ObservableCollection<FindingRow> Rows { get; } = new();
    public IReadOnlyList<Severity> Severities { get; } = Enum.GetValues<Severity>();
    public IReadOnlyList<SilenceReason> Reasons { get; } = Enum.GetValues<SilenceReason>();

    [ObservableProperty] private string? _appFilter;
    [ObservableProperty] private Severity? _severityFilter;
    [ObservableProperty] private bool _showSilenced;
    [ObservableProperty] private bool _onlyNeedsReview;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private SilenceReason _silenceReason = SilenceReason.FalsoPositivo;
    [ObservableProperty] private string _silenceNotes = string.Empty;
    [ObservableProperty] private int _silenceExpiryDays;
    [ObservableProperty] private string _assignee = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isEmpty;

    public void SetApp(string? slug) => AppFilter = slug;

    partial void OnSeverityFilterChanged(Severity? value) => Reload();
    partial void OnShowSilencedChanged(bool value) => Reload();
    partial void OnOnlyNeedsReviewChanged(bool value) => Reload();
    partial void OnSearchTextChanged(string value) => Reload();
    partial void OnAppFilterChanged(string? value) => Reload();

    public override Task LoadAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        Rows.Clear();
        var slugs = AppFilter is { Length: > 0 } ? new[] { AppFilter } : _hub.Store.ListAppSlugs().ToArray();
        int freshness = _settings.Current.DefaultThresholds.FreshnessDays;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var all = new List<FindingRow>();
        foreach (string slug in slugs)
        {
            foreach (Finding f in _hub.Store.ListFindings(slug))
            {
                if (!ShowSilenced && f.Status == FindingStatus.Silenciado)
                {
                    continue;
                }

                if (OnlyNeedsReview && !f.NeedsReview)
                {
                    continue;
                }

                if (SeverityFilter is { } sev && f.Severity != sev)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(SearchText)
                    && !f.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                    && !f.RuleId.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int days = (int)(now - f.LastConfirmed.Utc).TotalDays;
                Location loc = f.Locations.Count > 0 ? f.Locations[0] : new Location("", 0);
                all.Add(new FindingRow
                {
                    Slug = slug,
                    Id = f.Id,
                    DisplayId = f.DisplayId,
                    Title = f.Title,
                    Severity = f.Severity,
                    Confidence = f.Confidence,
                    Status = f.Status,
                    Pillar = f.Pillar,
                    Assignee = f.Assignee,
                    Location = $"{loc.Path}:{loc.Line}",
                    DaysSinceConfirmed = days,
                    NeedsReview = f.NeedsReview,
                    IsStale = days > freshness,
                });
            }
        }

        foreach (FindingRow row in all.OrderBy(r => r.Severity).ThenBy(r => r.Confidence).ThenByDescending(r => r.DaysSinceConfirmed))
        {
            Rows.Add(row);
        }

        IsEmpty = Rows.Count == 0;
    }

    private IEnumerable<FindingRow> Selected => Rows.Where(r => r.IsSelected);

    [RelayCommand]
    private Task OpenDetail(FindingRow? row)
        => row is null ? Task.CompletedTask
            : _navigation.NavigateToAsync<FindingDetailViewModel>(vm => vm.Load(row.Slug, row.Id));

    [RelayCommand]
    private void OpenInEditor(FindingRow? row)
    {
        if (row is null)
        {
            return;
        }

        string[] parts = row.Location.Split(':');
        int line = parts.Length > 1 && int.TryParse(parts[^1], out int l) ? l : 1;
        StatusMessage = _editor.Open(row.Slug, parts[0], line) ? "Abriendo en el editor…" : "No se pudo abrir el editor.";
    }

    [RelayCommand]
    private void SilenceSelected()
    {
        var selected = Selected.ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Selecciona hallazgos para silenciar.";
            return;
        }

        DateTimeOffset? expiry = SilenceExpiryDays > 0 ? DateTimeOffset.UtcNow.AddDays(SilenceExpiryDays) : null;
        foreach (FindingRow row in selected)
        {
            _governance.Silence(row.Slug, row.Id, SilenceReason, string.IsNullOrWhiteSpace(SilenceNotes) ? null : SilenceNotes, expiry);
        }

        StatusMessage = $"{selected.Count} hallazgo(s) silenciado(s).";
        Reload();
    }

    [RelayCommand]
    private void AssignSelected()
    {
        var selected = Selected.ToList();
        string? who = string.IsNullOrWhiteSpace(Assignee) ? null : Assignee.Trim();
        foreach (FindingRow row in selected)
        {
            _governance.Assign(row.Slug, row.Id, who);
        }

        StatusMessage = $"{selected.Count} hallazgo(s) asignado(s).";
        Reload();
    }

    [RelayCommand]
    private async Task VerifySelected()
    {
        var selected = Selected.ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Selecciona hallazgos para verificar.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Verificando…";
        try
        {
            int applied = 0;
            foreach (var group in selected.GroupBy(r => r.Slug))
            {
                applied += await Task.Run(() =>
                    _verify.RunAsync(group.Key, group.Select(r => r.Id).ToList(), CancellationToken.None));
            }

            StatusMessage = $"Verificados {applied} hallazgo(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }
}
