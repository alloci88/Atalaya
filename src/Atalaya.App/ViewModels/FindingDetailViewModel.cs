using System.Collections.ObjectModel;
using System.Windows;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>V4 Detalle de hallazgo (§8): full record, snippet, history, comments, governance and the
/// "Generar prompt de arreglo" action (§5.7).</summary>
public sealed partial class FindingDetailViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly GovernanceService _governance;
    private readonly MachineConfigStore _machines;

    public FindingDetailViewModel(HubContext hub, GovernanceService governance, MachineConfigStore machines)
    {
        _hub = hub;
        _governance = governance;
        _machines = machines;
    }

    public override string Title => Finding is null ? "Hallazgo" : $"{Finding.DisplayId ?? Finding.Id.ToString()}";

    [ObservableProperty] private string _slug = string.Empty;
    [ObservableProperty] private Finding? _finding;
    [ObservableProperty] private string _snippet = string.Empty;
    [ObservableProperty] private string _ruleText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Governance inputs
    [ObservableProperty] private SilenceReason _silenceReason = SilenceReason.FalsoPositivo;
    [ObservableProperty] private int _silenceExpiryDays;
    [ObservableProperty] private string _silenceNotes = string.Empty;
    [ObservableProperty] private string _assignee = string.Empty;
    [ObservableProperty] private Severity _severity = Severity.Media;
    [ObservableProperty] private string _justification = string.Empty;
    [ObservableProperty] private string _newComment = string.Empty;

    public ObservableCollection<HistoryEntry> History { get; } = new();
    public ObservableCollection<Comment> Comments { get; } = new();
    public ObservableCollection<string> Recurrences { get; } = new();
    public IReadOnlyList<SilenceReason> Reasons { get; } = Enum.GetValues<SilenceReason>();
    public IReadOnlyList<Severity> Severities { get; } = Enum.GetValues<Severity>();

    public void Load(string slug, Ulid id)
    {
        Slug = slug;
        Reload(id);
    }

    private void Reload(Ulid id)
    {
        Finding = _hub.Store.TryReadFinding(Slug, id.ToString());
        History.Clear();
        Comments.Clear();
        Recurrences.Clear();
        if (Finding is null)
        {
            return;
        }

        Severity = Finding.Severity;
        RuleText = Atalaya.Copilot.RuleCatalog.Find(Finding.RuleId)?.Look ?? "(regla de criterio o sin texto)";

        foreach (HistoryEntry h in Finding.History)
        {
            History.Add(h);
        }

        foreach (Comment c in _hub.Store.ListComments(Slug, Finding.Id.ToString()).OrderBy(c => c.Utc))
        {
            Comments.Add(c);
        }

        foreach (Finding other in _hub.Store.ListFindings(Slug).Where(x => x.RecurrenceOf == Finding.Id))
        {
            Recurrences.Add($"{other.DisplayId ?? other.Id.ToString()} · {other.Status}");
        }

        Snippet = ReadSnippet(Finding);
    }

    private string ReadSnippet(Finding f)
    {
        string? clone = _machines.Load().ClonePathFor(Slug);
        if (string.IsNullOrWhiteSpace(clone) || f.Locations.Count == 0)
        {
            return "(sin clon local para mostrar el snippet)";
        }

        Location loc = f.Locations[0];
        string abs = Path.Combine(clone, loc.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs))
        {
            return $"(no encontrado: {loc.Path})";
        }

        string[] lines = File.ReadAllLines(abs);
        int from = Math.Max(0, loc.Line - 4);
        int to = Math.Min(lines.Length, loc.Line + 3);
        return string.Join('\n', lines[from..to]);
    }

    private Ulid Id => Finding!.Id;

    [RelayCommand]
    private void Silence()
    {
        if (Finding is null)
        {
            return;
        }

        DateTimeOffset? expiry = SilenceExpiryDays > 0 ? DateTimeOffset.UtcNow.AddDays(SilenceExpiryDays) : null;
        _governance.Silence(Slug, Id, SilenceReason, string.IsNullOrWhiteSpace(SilenceNotes) ? null : SilenceNotes, expiry);
        StatusMessage = "Silenciado.";
        Reload(Id);
    }

    [RelayCommand]
    private void Unsilence()
    {
        _governance.Unsilence(Slug, Id);
        StatusMessage = "Des-silenciado.";
        Reload(Id);
    }

    [RelayCommand]
    private void ApplyAssign()
    {
        _governance.Assign(Slug, Id, string.IsNullOrWhiteSpace(Assignee) ? null : Assignee.Trim());
        StatusMessage = "Asignación actualizada.";
        Reload(Id);
    }

    [RelayCommand]
    private void ApplySeverity()
    {
        _governance.ChangeSeverity(Slug, Id, Severity);
        StatusMessage = "Severidad actualizada.";
        Reload(Id);
    }

    [RelayCommand]
    private void ResolveManually()
    {
        if (string.IsNullOrWhiteSpace(Justification))
        {
            StatusMessage = "La resolución manual exige justificación.";
            return;
        }

        _governance.ResolveManually(Slug, Id, Justification.Trim(), GitInfo.HeadSha(_machines.Load().ClonePathFor(Slug)));
        StatusMessage = "Resuelto manualmente.";
        Reload(Id);
    }

    [RelayCommand]
    private void Reopen()
    {
        _governance.Reopen(Slug, Id, "reabierto manualmente");
        StatusMessage = "Reabierto.";
        Reload(Id);
    }

    [RelayCommand]
    private void AddComment()
    {
        if (string.IsNullOrWhiteSpace(NewComment))
        {
            return;
        }

        _governance.AddComment(Slug, Id, NewComment.Trim());
        NewComment = string.Empty;
        Reload(Id);
    }

    [RelayCommand]
    private void GenerateFixPrompt()
    {
        if (Finding is null)
        {
            return;
        }

        string prompt = FixPromptBuilder.Build(Finding);
        try
        {
            Clipboard.SetText(prompt);
        }
        catch
        {
            // Clipboard may be unavailable; the record below still preserves the prompt.
        }

        _governance.AddComment(Slug, Id, prompt, kind: "fix-prompt");
        StatusMessage = "Prompt de arreglo copiado al portapapeles y guardado en el historial.";
        Reload(Id);
    }
}
