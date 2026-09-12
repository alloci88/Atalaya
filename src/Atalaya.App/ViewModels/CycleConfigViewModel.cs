using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>En qué momento se está configurando el ciclo. Cambia el título y la frase de contexto.</summary>
public enum CycleConfigReason
{
    /// <summary>Tras el escaneo del alta, antes de abrir el ciclo 1.</summary>
    Alta,

    /// <summary>Acaba de cerrarse un ciclo y el siguiente ha heredado la configuración.</summary>
    Cierre,

    /// <summary>«Reiniciar ciclo»: todo va a pendiente, y de paso se elige la lupa.</summary>
    Reinicio,

    /// <summary>«Configurar ciclo» desde el panel, a mitad de ciclo o con el ciclo recién abierto.</summary>
    Configurar,
}

/// <summary>Una temática del catálogo, lista para el combo: con su nombre y su frase.</summary>
public sealed record ThemeOption(AuditTheme Theme, string Label, string Description)
{
    public static ThemeOption Of(AuditTheme theme) => new(
        theme,
        theme == ThemeCatalog.Recommended
            ? $"{ThemeCatalog.Display(theme)} (recomendada)"
            : ThemeCatalog.Display(theme),
        ThemeCatalog.Description(theme));

    public override string ToString() => Label;
}

/// <summary>
/// El diálogo «Configurar ciclo» (F17 §4): la temática y el modelo preferido de UN ciclo.
/// <para>
/// Toda la regla vive aquí y no en la ventana: qué se preselecciona, cuándo se avisa de que el
/// cambio cuesta trabajo hecho, y qué configuración sale. Así el flujo entero —incluido cancelar—
/// se prueba sin abrir nada, como los demás diálogos de la casa.
/// </para>
/// </summary>
public sealed partial class CycleConfigViewModel : ObservableObject
{
    private CycleConfig _current = CycleConfig.Default;
    private string? _providerId;

    public CycleConfigViewModel()
    {
        foreach (AuditTheme theme in ThemeCatalog.All)
        {
            ThemeOptions.Add(ThemeOption.Of(theme));
        }

        _selectedTheme = ThemeOptions[0];
    }

    public ObservableCollection<ThemeOption> ThemeOptions { get; } = new();

    public ObservableCollection<ModelOption> Models { get; } = new();

    [ObservableProperty] private string _appName = string.Empty;

    [ObservableProperty] private int _cycleN = 1;

    [ObservableProperty] private CycleConfigReason _reason = CycleConfigReason.Configurar;

    /// <summary>Cuántas unidades están auditadas bajo la lupa actual: es lo que cuesta cambiarla.</summary>
    [ObservableProperty] private int _auditedUnits;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warning))]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    [NotifyPropertyChangedFor(nameof(ThemeDescription))]
    private ThemeOption _selectedTheme;

    [ObservableProperty] private string? _selectedModelId;

    /// <summary>El proveedor del que sale la lista: el preferente de quien configura.</summary>
    [ObservableProperty] private string _providerName = string.Empty;

    /// <summary>Por qué la lista de modelos es la que es, cuando no se pudo preguntar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelsNotice))]
    private string _modelsNotice = string.Empty;

    public bool HasModelsNotice => ModelsNotice.Length > 0;

    /// <summary>True si el usuario aceptó. Lo pone la ventana; el flujo lo lee.</summary>
    public bool Accepted { get; set; }

    public string Title => Reason switch
    {
        CycleConfigReason.Alta => "Configurar el primer ciclo",
        CycleConfigReason.Cierre => $"Ciclo {CycleN} abierto: ¿con qué lupa?",
        CycleConfigReason.Reinicio => $"Reiniciar ciclo: configurar el ciclo {CycleN}",
        _ => $"Configurar el ciclo {CycleN}",
    };

    /// <summary>La frase de contexto: qué acaba de pasar y qué significa lo que se elige.</summary>
    public string Context => Reason switch
    {
        CycleConfigReason.Alta =>
            "El escaneo ha terminado. Antes de abrir el ciclo 1, elige con qué lupa se va a auditar "
            + "esta aplicación. General es la mirada completa y la recomendada para empezar.",
        CycleConfigReason.Cierre =>
            $"El ciclo {CycleN} ha heredado la configuración del anterior. Puedes cambiarla ahora o "
            + "más tarde desde «Configurar ciclo», en el panel del inventario.",
        CycleConfigReason.Reinicio =>
            "Reiniciar pone todas las unidades a pendiente sin borrar nada. Elige de paso la lupa "
            + "con la que se va a volver a mirar todo.",
        _ =>
            "La temática decide qué busca el auditor en este ciclo y qué hallazgos reconcilia. Un "
            + "ciclo temático no sustituye a uno General: es una pasada acotada, no la mirada completa.",
    };

    public string ThemeDescription => SelectedTheme.Description;

    /// <summary>
    /// «N unidades auditadas pasarán a pendientes; los hallazgos existentes no se tocan». Solo
    /// cuando se cambia de temática con trabajo hecho: es la misma frase se cambie cuando se cambie,
    /// porque es la misma operación (F17 §4).
    /// </summary>
    public string Warning => SelectedTheme.Theme != _current.Theme
        ? CycleConfigService.ChangeWarning(AuditedUnits)
        : string.Empty;

    public bool HasWarning => Warning.Length > 0;

    /// <summary>La configuración que saldría al aceptar.</summary>
    public CycleConfig Result => new(
        SelectedTheme.Theme,
        string.IsNullOrWhiteSpace(SelectedModelId) ? null : _providerId,
        string.IsNullOrWhiteSpace(SelectedModelId) ? null : SelectedModelId);

    /// <summary>
    /// Carga el diálogo. La temática preseleccionada es la vigente (General, marcada como
    /// recomendada, en el alta); el modelo preseleccionado es el preferido del ciclo si está en la
    /// lista, y si no el que esta máquina tiene configurado para su proveedor.
    /// </summary>
    public void Load(
        CycleConfigPreview preview, CycleConfigReason reason,
        string? providerId, string providerName,
        IReadOnlyList<ModelOption> models, string? configuredModelId, string modelsNotice = "")
    {
        _current = preview.Current;
        _providerId = providerId;
        AppName = preview.AppName;
        CycleN = preview.CycleN;
        Reason = reason;
        AuditedUnits = preview.AuditedUnits;
        ProviderName = providerName;
        ModelsNotice = modelsNotice;

        SelectedTheme = ThemeOptions.First(o => o.Theme == preview.Current.Theme);

        Models.Clear();
        foreach (ModelOption m in models)
        {
            Models.Add(m);
        }

        bool sameProvider = string.IsNullOrWhiteSpace(preview.Current.PreferredProvider)
            || string.Equals(preview.Current.PreferredProvider, providerId, StringComparison.OrdinalIgnoreCase);
        string? preferred = sameProvider ? preview.Current.PreferredModel : null;
        SelectedModelId = Pick(preferred) ?? Pick(configuredModelId) ?? Models.FirstOrDefault()?.Id;

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasWarning));
    }

    private string? Pick(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Id;
}
