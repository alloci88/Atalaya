using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// «Umbrales · Gestionar» (F13): la política de tamaño de UNA aplicación, editada donde se ve su
/// consecuencia — el panel del ciclo del Inventario, junto a los patrones silenciados y las
/// directivas, que son las otras dos cosas que gobiernan qué se reporta aquí.
/// <para>
/// No está en Ajustes a propósito, y no puede estar en los dos sitios: Ajustes guarda lo de ESTA
/// máquina, y este umbral clasifica un inventario que todo el equipo comparte. Dos sitios
/// editables para el mismo valor son dos verdades esperando a discrepar.
/// </para>
/// </summary>
public sealed partial class ThresholdsViewModel : ObservableObject
{
    private readonly ThresholdPolicyService _policy;
    private readonly ToastCenter _toasts;

    public ThresholdsViewModel(ThresholdPolicyService policy, ToastCenter toasts)
    {
        _policy = policy;
        _toasts = toasts;
    }

    public string Slug { get; private set; } = string.Empty;

    [ObservableProperty] private string _appName = string.Empty;

    [ObservableProperty] private int _largeUnitLoc;

    [ObservableProperty] private int _largeUnitChars;

    /// <summary>Cuántas unidades del ciclo vigente serían grandes con lo que hay escrito AHORA.</summary>
    [ObservableProperty] private int _wouldBeLarge;

    /// <summary>Cuántas lo son con la política guardada, para poder comparar sin re-escanear.</summary>
    [ObservableProperty] private int _currentlyLarge;

    /// <summary>Las unidades del ciclo vigente, para poder contar sin volver a leer el hub.</summary>
    private IReadOnlyList<InventoryUnit> _units = Array.Empty<InventoryUnit>();

    public bool Changed => LargeUnitLoc != _savedLoc || LargeUnitChars != _savedChars;

    private int _savedLoc;
    private int _savedChars;

    /// <summary>
    /// Lo que cambiaría al aplicar, contado sobre el inventario que ya hay. No re-escanea nada —el
    /// umbral aplica al re-escanear, y prometer aquí un efecto inmediato sería mentir— pero sí
    /// permite ver la magnitud antes de decidir.
    /// </summary>
    public string Preview
    {
        get
        {
            if (_units.Count == 0)
            {
                return "Todavía no hay inventario que contar en este ciclo.";
            }

            int delta = WouldBeLarge - CurrentlyLarge;
            string ahora = $"Ahora mismo hay {CurrentlyLarge} unidad(es) grande(s) de {_units.Count}.";
            return delta switch
            {
                0 => $"{ahora} Con este umbral seguirían siendo las mismas.",
                > 0 => $"{ahora} Con este umbral pasarían a serlo {delta} más.",
                _ => $"{ahora} Con este umbral dejarían de serlo {-delta}.",
            };
        }
    }

    public void Load(string slug, string appName, IReadOnlyList<InventoryUnit> units)
    {
        Slug = slug;
        AppName = appName;
        _units = units;

        Thresholds saved = _policy.Read(slug);
        _savedLoc = saved.LargeUnitLoc;
        _savedChars = saved.LargeUnitChars;
        LargeUnitLoc = saved.LargeUnitLoc;
        LargeUnitChars = saved.LargeUnitChars;
        CurrentlyLarge = units.Count(u => u.State == UnitState.Grande);
        Recount();
    }

    partial void OnLargeUnitLocChanged(int value) => Recount();

    /// <summary>
    /// Solo cuenta por LOC: el peso de cada unidad no está en el inventario —no se guarda—, así que
    /// contar por caracteres exigiría abrir el clon. Se dice en la pantalla en vez de dar por
    /// bueno un número que no incluye la mitad del criterio.
    /// </summary>
    private void Recount()
    {
        WouldBeLarge = _units.Count(u => u.Loc > LargeUnitLoc);
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Changed));
    }

    partial void OnLargeUnitCharsChanged(int value) => OnPropertyChanged(nameof(Changed));

    /// <summary>
    /// Guarda la política y la publica. El resultado —incluidas las correcciones de mínimo— se
    /// cuenta por toast, y las cajas se quedan con lo que de verdad se guardó.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        ThresholdPolicyResult result = _policy.Set(Slug, LargeUnitLoc, LargeUnitChars);
        if (result.Saved)
        {
            _savedLoc = result.LargeUnitLoc;
            _savedChars = result.LargeUnitChars;
            LargeUnitLoc = result.LargeUnitLoc;
            LargeUnitChars = result.LargeUnitChars;
        }

        _toasts.Show(result.Message);
        Recount();
    }
}
