using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Una fila de «hallazgos de unidades que ya no existen» (F9 §4): el hallazgo, dónde vivía, y el
/// commit que borró ese fichero cuando se ha podido localizar.
/// </summary>
public sealed partial class OrphanRow : ObservableObject
{
    public OrphanRow(OrphanFinding orphan)
    {
        Id = orphan.FindingId;
        UnitPath = orphan.UnitPath;
        Alias = orphan.Alias ?? orphan.FindingId;
        Title = orphan.Title;
        Severity = orphan.Severity;
    }

    public string Id { get; }

    public string UnitPath { get; }

    /// <summary>El identificador legible, o el ULID si todavía no tiene alias.</summary>
    public string Alias { get; }

    public string Title { get; }

    public Severity Severity { get; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>El commit del borrado. Se busca al abrir, y es la evidencia de la resolución.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Evidence))]
    private string? _deletedCommit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Evidence))]
    private DateTimeOffset? _deletedUtc;

    /// <summary>
    /// Lo que sostiene la resolución, escrito. Cuando no se localiza el commit lo DICE: una
    /// evidencia que no se ha encontrado no se sustituye por una frase que suene a que sí.
    /// </summary>
    public string Evidence => DeletedCommit is { Length: > 0 }
        ? $"Borrado en {DeletedCommit}"
          + (DeletedUtc is { } utc
              ? $" · {utc.ToLocalTime().ToString("d MMM yyyy", AppCulture.Display)}"
              : string.Empty)
        : "No se ha localizado el commit del borrado";
}

/// <summary>
/// Los hallazgos que se quedaron sin código (F9 §4). Existen porque el fichero que los contenía
/// desapareció del repositorio: no se pueden verificar —no hay nada que mirar— ni se resuelven
/// solos, así que sin una salida explícita se quedan activos para siempre.
/// <para>
/// <b>Nada se resuelve automáticamente.</b> Un fichero que no está donde estaba puede haberse
/// movido, y la detección de renombrados caza una parte pero no todas: un fichero troceado sale
/// como borrado más unidades nuevas, y coserlo a ojo sería inventar. Se presenta tal cual y decide
/// una persona, que es la regla de la casa.
/// </para>
/// </summary>
public sealed partial class DeletedUnitsViewModel : ObservableObject
{
    private readonly GovernanceService _governance;
    private readonly DriftQuery _drift;
    private readonly ToastCenter _toasts;

    public DeletedUnitsViewModel(GovernanceService governance, DriftQuery drift, ToastCenter toasts)
    {
        _governance = governance;
        _drift = drift;
        _toasts = toasts;
    }

    public ObservableCollection<OrphanRow> Rows { get; } = new();

    [ObservableProperty] private string _slug = string.Empty;

    [ObservableProperty] private string _title = "Hallazgos de unidades que ya no existen";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    public bool IsEmpty => Count == 0;

    private string? _clonePath;

    public void Load(string slug, string appName, string? clonePath, IReadOnlyList<OrphanFinding> orphans)
    {
        Slug = slug;
        _clonePath = clonePath;
        Title = $"{appName} · hallazgos de unidades que ya no existen";

        Rows.Clear();
        foreach (OrphanFinding orphan in orphans)
        {
            var row = new OrphanRow(orphan);

            // La evidencia se busca AQUÍ y no durante el cálculo de la deriva: localizar el commit
            // de un borrado cuesta un recorrido del historial por ruta, y solo hace falta cuando
            // alguien va a decidir sobre esto.
            (string? sha, DateTimeOffset? utc) = _drift.FindDeletion(_clonePath, orphan.UnitPath);
            row.DeletedCommit = sha;
            row.DeletedUtc = utc;
            Rows.Add(row);
        }

        Count = Rows.Count;
    }

    public IReadOnlyList<OrphanRow> Selected => Rows.Where(r => r.IsSelected).ToList();

    [RelayCommand]
    private void SelectAll()
    {
        bool select = !Rows.All(r => r.IsSelected);
        foreach (OrphanRow row in Rows)
        {
            row.IsSelected = select;
        }
    }

    /// <summary>
    /// «Resolver por código eliminado» sobre lo seleccionado (F9 §4). Es la única salida digna para
    /// estos hallazgos, y la ejecuta una persona: la aplicación aporta la evidencia y la atribución,
    /// nunca la decisión.
    /// </summary>
    [RelayCommand]
    private void ResolveSelected()
    {
        var selected = Selected;
        if (selected.Count == 0)
        {
            _toasts.Show("Marca al menos un hallazgo. Nada se resuelve solo.");
            return;
        }

        int done = 0;
        foreach (OrphanRow row in selected)
        {
            if (!Ulid.TryParse(row.Id, out Ulid id))
            {
                continue;
            }

            try
            {
                _governance.ResolveAsDeletedCode(Slug, id, row.UnitPath, row.DeletedCommit);
                done++;
            }
            catch (Exception ex)
            {
                _toasts.Show($"No se pudo resolver {row.Alias}: {ex.Message}");
            }
        }

        foreach (OrphanRow row in selected.Take(done))
        {
            Rows.Remove(row);
        }

        Count = Rows.Count;
        _toasts.Show(done == 1
            ? "1 hallazgo resuelto por código eliminado, con tu nombre y el commit del borrado."
            : $"{done} hallazgos resueltos por código eliminado, con tu nombre y el commit del borrado.");
    }
}
