using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Un patrón silenciado, tal y como se lee en la gestión (F5.12): qué se calla, de dónde salió,
/// quién lo decidió, hasta cuándo, y <b>cuánto trabaja</b>. Es un modelo de fila, no la entidad: la
/// entidad no sabe decir «caducado».
/// </summary>
public sealed partial class PatternSilenceRow : ObservableObject
{
    public PatternSilenceRow(PatternSilence pattern, DateTimeOffset now)
    {
        Id = pattern.Id;
        ShortId = pattern.ShortId;
        _exemplar = pattern.Exemplar;
        Origin = pattern.SourceFindingUlid is { } src
            ? $"desde el hallazgo {src}"
            : "sin hallazgo origen (migrado de una exclusión de regla)";
        Reason = SilenceReasonNames.Display(pattern.Reason);
        Notes = pattern.Notes ?? string.Empty;
        By = pattern.By;
        Suppressions = pattern.Suppressions;
        LastSuppressionUtc = pattern.LastSuppressionUtc;
        Expired = pattern.IsExpiredAt(now);
        ExpiresUtc = pattern.ExpiresUtc;
        ExpiryDays = pattern.ExpiresUtc is null
            ? 0
            : Math.Max(0, (int)Math.Ceiling((pattern.ExpiresUtc.Value - now).TotalDays));
    }

    public Ulid Id { get; }

    /// <summary>El id que el auditor ve y cita. No cambia al reescribir el ejemplar.</summary>
    public string ShortId { get; }

    /// <summary>
    /// La frase, editable en el sitio. Afinar el alcance es reescribirla: no hay catálogo que
    /// mantener, ni granularidad que negociar.
    /// </summary>
    [ObservableProperty] private string _exemplar;

    public string Origin { get; }

    public string Reason { get; }

    public string Notes { get; }

    public string By { get; }

    /// <summary>Detecciones que los auditores han declarado callarse por este patrón.</summary>
    public int Suppressions { get; }

    public DateTimeOffset? LastSuppressionUtc { get; }

    public DateTimeOffset? ExpiresUtc { get; }

    /// <summary>Caducado = no suprime nada. Sigue listado para que alguien decida qué hacer con él.</summary>
    public bool Expired { get; }

    /// <summary>La caducidad, escrita como se lee: una fecha o «permanente».</summary>
    public string Expiry => ExpiresUtc is null
        ? "permanente"
        : $"{ExpiresUtc.Value.ToLocalTime():dd/MM/yyyy}";

    /// <summary>
    /// El estado, con el mismo vocabulario que los silencios: vivo suprime, caducado no. Se dice
    /// «revisar» porque un patrón caducado no es un error — es una decisión que venció.
    /// </summary>
    public string State => Expired ? "caducado — revisar" : "activo";

    /// <summary>
    /// Cuánto trabaja, en una frase. Un patrón que nunca ha suprimido nada no es necesariamente un
    /// error, pero es lo primero que hay que mirar al decidir si sobra.
    /// </summary>
    public string Work => Suppressions == 0
        ? "Todavía no ha suprimido ninguna detección."
        : $"Ha suprimido {Suppressions} detección(es)"
          + (LastSuppressionUtc is null ? "." : $", la última el {LastSuppressionUtc.Value.ToLocalTime():dd/MM/yyyy}.");

    /// <summary>Lo que hoy hace este patrón, en una frase.</summary>
    public string Effect => Expired
        ? "No suprime nada: las auditorías vuelven a reportar problemas de este tipo."
        : "Las auditorías de esta aplicación no reportan problemas de este tipo.";

    /// <summary>Días que se le van a poner al editar la caducidad. 0 = permanente.</summary>
    [ObservableProperty] private int _expiryDays;
}

/// <summary>
/// La gestión de patrones silenciados de UNA aplicación (F5.12): listar, editar el ejemplar,
/// des-silenciar y cambiar caducidad. Visible para todos; editable con la misma política que el
/// resto de la gobernanza.
/// <para>
/// Vive separada del diálogo para que el flujo se pueda ejercitar sin abrir una ventana, que es
/// como se comprueban las tres cosas que importan: que des-silenciar devuelve el tipo al juego, que
/// una caducidad editada se escribe donde el prompt la lee, y que reescribir el ejemplar cambia lo
/// que el auditor va a leer sin cambiar el id que ya está en los informes.
/// </para>
/// </summary>
public sealed partial class PatternSilencesViewModel : ObservableObject
{
    private readonly HubContext _hub;
    private readonly GovernanceService _governance;
    private readonly ToastCenter _toasts;
    private readonly Func<DateTimeOffset> _now;

    public PatternSilencesViewModel(
        HubContext hub, GovernanceService governance, ToastCenter toasts,
        Func<DateTimeOffset>? now = null)
    {
        _hub = hub;
        _governance = governance;
        _toasts = toasts;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public ObservableCollection<PatternSilenceRow> Rows { get; } = new();

    [ObservableProperty] private string _slug = string.Empty;

    [ObservableProperty] private string _appName = string.Empty;

    /// <summary>El vacío se dice con palabras: una lista en blanco se lee como un fallo de carga.</summary>
    public bool IsEmpty => Rows.Count == 0;

    public string Title => $"Patrones silenciados · {AppName}";

    /// <summary>
    /// El aviso de tope (F5.12 §4). La lista viaja en cada prompt de unidad y con decenas de
    /// patrones sigue siendo despreciable frente al contenido; pasado
    /// <see cref="PatternSilenceSet.SoftCap"/> lo que preocupa no es el coste sino el síntoma: el
    /// silenciado se está usando como taxonomía. No bloquea nada — se dice y se sigue.
    /// </summary>
    public string CapNotice { get; private set; } = string.Empty;

    public bool HasCapNotice => CapNotice.Length > 0;

    public void Load(string slug)
    {
        Slug = slug;
        AppName = _hub.Store.TryReadApp(slug)?.Name ?? slug;
        Reload();
    }

    private void Reload()
    {
        DateTimeOffset now = _now();
        Rows.Clear();
        foreach (PatternSilence p in _hub.Store.ListPatternSilences(Slug)
                     .OrderBy(p => p.Utc)
                     .ThenBy(p => p.ShortId, StringComparer.Ordinal))
        {
            Rows.Add(new PatternSilenceRow(p, now));
        }

        int live = Rows.Count(r => !r.Expired);
        int expired = Rows.Count - live;
        CapNotice = live > PatternSilenceSet.SoftCap
            ? $"{live} patrones activos en esta aplicación (por encima de {PatternSilenceSet.SoftCap}): "
              + "considera consolidar los que digan lo mismo"
              + (expired > 0 ? $" o revisar los {expired} caducado(s)." : ".")
            : string.Empty;

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CapNotice));
        OnPropertyChanged(nameof(HasCapNotice));
    }

    /// <summary>
    /// Reescribe la frase del patrón. Es el afinado: el alcance se ajusta cambiando lo que el
    /// auditor lee, no partiendo un catálogo en trozos más finos.
    /// </summary>
    [RelayCommand]
    private void ApplyExemplar(PatternSilenceRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Exemplar))
        {
            _toasts.Show("El ejemplar no puede quedarse vacío: es la frase que define el alcance.");
            Reload();
            return;
        }

        if (_governance.EditPatternExemplar(Slug, row.Id, row.Exemplar))
        {
            _toasts.Show($"Patrón {row.ShortId} actualizado: «{row.Exemplar.Trim()}».");
        }

        Reload();
    }

    /// <summary>Retira el patrón: las auditorías vuelven a reportar problemas de ese tipo.</summary>
    [RelayCommand]
    private void Unsilence(PatternSilenceRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_governance.UnsilencePattern(Slug, row.Id))
        {
            _toasts.Show($"Patrón {row.ShortId} des-silenciado: {AppName} vuelve a reportar problemas de ese tipo.");
        }

        Reload();
    }

    /// <summary>
    /// Cambia la caducidad, en días desde hoy. 0 = permanente, que es también la forma de revivir
    /// un patrón caducado sin volver a escribir su motivo.
    /// </summary>
    [RelayCommand]
    private void ApplyExpiry(PatternSilenceRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.ExpiryDays < 0)
        {
            _toasts.Show("La caducidad no puede ser negativa: 0 días es un silencio permanente.");
            return;
        }

        DateTimeOffset? expiry = row.ExpiryDays > 0 ? _now().AddDays(row.ExpiryDays) : null;
        if (_governance.SetPatternExpiry(Slug, row.Id, expiry))
        {
            _toasts.Show(expiry is null
                ? $"Patrón {row.ShortId}: silencio permanente."
                : $"Patrón {row.ShortId}: silenciado hasta el {expiry.Value.ToLocalTime():dd/MM/yyyy}.");
        }

        Reload();
    }
}
