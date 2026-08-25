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

/// <summary>
/// V4 Detalle de hallazgo (§8): ficha completa, snippet, historial, comentarios, gobernanza y la
/// acción "Generar prompt de arreglo" (§5.7).
/// <para>
/// <b>F5.4.</b> Aquí vive AHORA el juego completo de acciones de escritura sobre un hallazgo. V3
/// se quedó sin ninguna: <b>la lista encuentra, el detalle actúa</b>. Lo que bajó de V3 en esta
/// tanda es <see cref="VerifyCommand"/>, las dos salidas de disputa
/// (<see cref="AcceptDisputeCommand"/> / <see cref="DismissDisputeCommand"/>) y
/// <see cref="OpenInEditorCommand"/>. Silenciar y asignar ya estaban.
/// </para>
/// </summary>
public sealed partial class FindingDetailViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly GovernanceService _governance;
    private readonly MachineConfigStore _machines;
    private readonly VerifyCoordinator _verify;
    private readonly EditorLauncher _editor;

    public FindingDetailViewModel(
        HubContext hub,
        GovernanceService governance,
        MachineConfigStore machines,
        VerifyCoordinator verify,
        EditorLauncher editor)
    {
        _hub = hub;
        _governance = governance;
        _machines = machines;
        _verify = verify;
        _editor = editor;
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

    /// <summary>Hay una discrepancia abierta: la ficha ofrece las dos salidas (F5.1b).</summary>
    public bool IsDisputed => Finding is { Disputes.Count: > 0 };

    /// <summary>Quién discrepa y por qué, para decidir con la razón delante y no a ciegas.</summary>
    public string DisputeSummary => Finding is null || Finding.Disputes.Count == 0
        ? string.Empty
        : string.Join(" · ", Finding.Disputes.Select(d => $"{d.Model ?? "auditor"}: {d.Justification}"));

    public ObservableCollection<HistoryEntry> History { get; } = new();
    public ObservableCollection<Comment> Comments { get; } = new();
    public IReadOnlyList<SilenceReason> Reasons { get; } = Enum.GetValues<SilenceReason>();
    public IReadOnlyList<Severity> Severities { get; } = Enum.GetValues<Severity>();

    partial void OnFindingChanged(Finding? value)
    {
        OnPropertyChanged(nameof(IsDisputed));
        OnPropertyChanged(nameof(DisputeSummary));
    }

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

    /// <summary>
    /// Cierra la disputa dando la razón al auditor que discrepó (F5.1b): se silencia como falso
    /// positivo, con tu nombre. No es una resolución — nunca hubo nada que arreglar.
    /// </summary>
    [RelayCommand]
    private void AcceptDispute()
    {
        if (Finding is null || Finding.Disputes.Count == 0)
        {
            StatusMessage = "Este hallazgo no tiene ninguna disputa abierta.";
            return;
        }

        _governance.ResolveDisputeAsFalsePositive(
            Slug, Id, string.IsNullOrWhiteSpace(SilenceNotes) ? null : SilenceNotes.Trim());
        StatusMessage = "Disputa aceptada: silenciado como falso positivo.";
        Reload(Id);
    }

    /// <summary>Cierra la disputa dando la razón a quien lo reportó: sigue siendo un defecto.</summary>
    [RelayCommand]
    private void DismissDispute()
    {
        if (Finding is null || Finding.Disputes.Count == 0)
        {
            StatusMessage = "Este hallazgo no tiene ninguna disputa abierta.";
            return;
        }

        _governance.DismissDispute(
            Slug, Id, string.IsNullOrWhiteSpace(SilenceNotes) ? null : SilenceNotes.Trim());
        StatusMessage = "Disputa descartada: sigue siendo un defecto.";
        Reload(Id);
    }

    /// <summary>
    /// Re-verifica ESTE hallazgo (§5.4). Bajó de V3 en F5.4: un verify masivo sobre una selección
    /// no dejaba ver qué se le estaba preguntando al agente sobre cada uno.
    /// </summary>
    [RelayCommand]
    private async Task Verify()
    {
        if (Finding is null)
        {
            return;
        }

        Ulid id = Id;
        IsBusy = true;
        StatusMessage = "Verificando…";
        try
        {
            int applied = await Task.Run(() => _verify.RunAsync(Slug, new[] { id }, CancellationToken.None));
            StatusMessage = applied > 0
                ? "Verificado: el veredicto está aplicado y en el historial."
                : "El verify no pudo emitir veredicto. Mira el historial.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Reload(id);
        }
    }

    /// <summary>Abre la localización principal en el editor configurado (§8).</summary>
    [RelayCommand]
    private void OpenInEditor()
    {
        if (Finding is null || Finding.Locations.Count == 0)
        {
            StatusMessage = "Este hallazgo no tiene una ubicación que abrir.";
            return;
        }

        Location loc = Finding.Locations[0];
        StatusMessage = _editor.Open(Slug, loc.Path, loc.Line)
            ? "Abriendo en el editor…"
            : "No se pudo abrir el editor.";
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
