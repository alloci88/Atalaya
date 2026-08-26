using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Una regla excluida, tal y como se lee en la gestión (F5.10): quién, por qué, hasta cuándo y si
/// sigue en pie. Es un modelo de fila, no la entidad: la entidad no sabe decir «caducada».
/// </summary>
public sealed partial class RuleExclusionRow : ObservableObject
{
    public RuleExclusionRow(RuleExclusion exclusion, DateTimeOffset now)
    {
        RuleId = exclusion.RuleId;
        Reason = SilenceReasonNames.Display(exclusion.Reason);
        Notes = exclusion.Notes ?? string.Empty;
        By = exclusion.By;
        Expired = exclusion.IsExpiredAt(now);
        ExpiresUtc = exclusion.ExpiresUtc;
        ExpiryDays = exclusion.ExpiresUtc is null
            ? 0
            : Math.Max(0, (int)Math.Ceiling((exclusion.ExpiresUtc.Value - now).TotalDays));
    }

    public string RuleId { get; }

    public string Reason { get; }

    public string Notes { get; }

    public string By { get; }

    public DateTimeOffset? ExpiresUtc { get; }

    /// <summary>Caducada = no suprime nada. Sigue listada para que alguien decida qué hacer con ella.</summary>
    public bool Expired { get; }

    /// <summary>La caducidad, escrita como se lee: una fecha o «permanente».</summary>
    public string Expiry => ExpiresUtc is null
        ? "permanente"
        : $"{ExpiresUtc.Value.ToLocalTime():dd/MM/yyyy}";

    /// <summary>
    /// El estado, con el mismo vocabulario que los silencios: viva suprime, caducada no. Se dice
    /// «revisar» porque una exclusión caducada no es un error — es una decisión que venció.
    /// </summary>
    public string State => Expired ? "caducada — revisar" : "activa";

    /// <summary>Lo que hoy protege esta exclusión, en una frase.</summary>
    public string Effect => Expired
        ? "No suprime nada: la regla vuelve a reportarse."
        : "Ninguna auditoría de esta aplicación reporta esta regla.";

    /// <summary>Días que se le van a poner al editar la caducidad. 0 = permanente.</summary>
    [ObservableProperty] private int _expiryDays;
}

/// <summary>
/// La gestión de reglas excluidas de UNA aplicación (F5.10): listar, des-excluir y cambiar
/// caducidad. Visible para todos; editable con la misma política que el resto de la gobernanza.
/// <para>
/// Vive separada del diálogo para que el flujo se pueda ejercitar sin abrir una ventana, que es
/// como se comprueban las dos cosas que importan: que des-excluir devuelve la regla al juego y que
/// una caducidad editada se escribe donde la ingestión la lee.
/// </para>
/// </summary>
public sealed partial class RuleExclusionsViewModel : ObservableObject
{
    private readonly HubContext _hub;
    private readonly GovernanceService _governance;
    private readonly ToastCenter _toasts;
    private readonly Func<DateTimeOffset> _now;

    public RuleExclusionsViewModel(
        HubContext hub, GovernanceService governance, ToastCenter toasts,
        Func<DateTimeOffset>? now = null)
    {
        _hub = hub;
        _governance = governance;
        _toasts = toasts;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public ObservableCollection<RuleExclusionRow> Rows { get; } = new();

    [ObservableProperty] private string _slug = string.Empty;

    [ObservableProperty] private string _appName = string.Empty;

    /// <summary>El vacío se dice con palabras: una lista en blanco se lee como un fallo de carga.</summary>
    public bool IsEmpty => Rows.Count == 0;

    public string Title => $"Reglas excluidas · {AppName}";

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
        foreach (RuleExclusion e in _hub.Store.ListRuleExclusions(Slug)
                     .OrderBy(e => e.RuleId, StringComparer.Ordinal))
        {
            Rows.Add(new RuleExclusionRow(e, now));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Retira la exclusión: la regla vuelve al brief y puede volver a reportarse.</summary>
    [RelayCommand]
    private void Unexclude(RuleExclusionRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_governance.UnexcludeRule(Slug, row.RuleId))
        {
            _toasts.Show($"Regla {row.RuleId} des-excluida: vuelve a poder reportarse en {AppName}.");
        }

        Reload();
    }

    /// <summary>
    /// Cambia la caducidad, en días desde hoy. 0 = permanente, que es también la forma de revivir
    /// una exclusión caducada sin volver a escribir su motivo.
    /// </summary>
    [RelayCommand]
    private void ApplyExpiry(RuleExclusionRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.ExpiryDays < 0)
        {
            _toasts.Show("La caducidad no puede ser negativa: 0 días es una exclusión permanente.");
            return;
        }

        DateTimeOffset? expiry = row.ExpiryDays > 0 ? _now().AddDays(row.ExpiryDays) : null;
        if (_governance.SetRuleExclusionExpiry(Slug, row.RuleId, expiry))
        {
            _toasts.Show(expiry is null
                ? $"Regla {row.RuleId}: exclusión permanente."
                : $"Regla {row.RuleId}: excluida hasta el {expiry.Value.ToLocalTime():dd/MM/yyyy}.");
        }

        Reload();
    }
}
