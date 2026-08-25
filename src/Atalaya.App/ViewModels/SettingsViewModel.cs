using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Una opción del desplegable de modelos (F5.1). El precio solo aparece si el SDK lo publica:
/// un multiplicador inventado sería peor que ninguno.
/// </summary>
public sealed record ModelOption(string Id, string Label)
{
    public static ModelOption From(AgentModel model)
    {
        string name = string.Equals(model.Name, model.Id, StringComparison.Ordinal)
            ? model.Id
            : $"{model.Name} ({model.Id})";
        return new ModelOption(
            model.Id,
            model.Multiplier is { } m ? $"{name} · ×{m:0.##}" : name);
    }

    /// <summary>El modelo configurado, cuando no se ha podido preguntar al SDK cuáles hay.</summary>
    public static ModelOption Unverified(string id) => new(id, $"{id} (configurado)");
}

/// <summary>
/// Ajustes (§8) — preferences ONLY since F2 (D4): theme, editor, thresholds, polling and feature
/// flags. Everything about the connection (hub URL, PAT, git identity, "Comprobar Copilot") moved
/// to the Cuenta page. What remains here of the old world lives collapsed under "Opciones
/// avanzadas": the PAT fallback for organizations that block OAuth Apps, and a development-only
/// hub URL override.
/// <para>
/// F5.1 añade dos: el tope de pasadas del barrido (antes solo en <c>app.json</c>) y el selector de
/// modelo, poblado con lo que el SDK lista para esta cuenta.
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly GitHubAccountService _account;
    private readonly ICopilotAgent _agent;

    /// <summary>Plazo para que el SDK conteste con su catálogo antes de rendirse.</summary>
    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(30);

    public SettingsViewModel(
        SettingsService settings, HubContext hub, GitHubAccountService account, ICopilotAgent agent)
    {
        _settings = settings;
        _hub = hub;
        _account = account;
        _agent = agent;
        AppSettings s = settings.Current;
        _editor = s.Editor;
        _isLightTheme = string.Equals(s.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _pollingSeconds = s.PollingSeconds;
        _largeUnitLoc = s.DefaultThresholds.LargeUnitLoc;
        _freshnessDays = s.DefaultThresholds.FreshnessDays;
        _maxPassesPerUnit = s.MaxPassesPerUnit;
        _enableAssistedFix = s.EnableAssistedFix;
        _copilotTimeoutMinutes = s.CopilotTimeoutMinutes;
        _hubUrlOverride = s.HubUrlOverride ?? string.Empty;
        _hasStoredPat = settings.GetPat() is not null;
        _requireTlsRevocationCheck = s.RequireTlsRevocationCheck;
        _selectedModelId = s.CopilotModel;

        // Hasta que el SDK conteste, el desplegable enseña el modelo configurado: así nunca está
        // vacío ni «elige» en silencio uno distinto del que se está usando.
        Models.Add(ModelOption.Unverified(s.CopilotModel));
    }

    public override string Title => "Ajustes";

    [ObservableProperty] private string _editor;
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private int _pollingSeconds;
    [ObservableProperty] private int _largeUnitLoc;
    [ObservableProperty] private int _freshnessDays;
    [ObservableProperty] private int _maxPassesPerUnit;
    [ObservableProperty] private bool _enableAssistedFix;
    [ObservableProperty] private int _copilotTimeoutMinutes;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // --- Modelo (F5.1) ---

    /// <summary>Lo que el SDK lista para esta cuenta. Nunca una lista escrita a mano.</summary>
    public ObservableCollection<ModelOption> Models { get; } = new();

    [ObservableProperty] private string _selectedModelId;

    /// <summary>Por qué la lista no es la del SDK (offline, sin credencial, sin asiento).</summary>
    [ObservableProperty] private string _modelsNotice = string.Empty;

    // --- Opciones avanzadas (colapsadas) ---
    [ObservableProperty] private string _pat = string.Empty;
    [ObservableProperty] private string _hubUrlOverride;
    [ObservableProperty] private bool _hasStoredPat;
    [ObservableProperty] private bool _requireTlsRevocationCheck;

    /// <summary>The hub actually in use — shown read-only under advanced options, for support.</summary>
    public string EffectiveHubUrl => _hub.HubUrl ?? "(sin configurar)";

    /// <summary>The PAT is ignored while an account is connected (D3).</summary>
    public bool AccountOverridesPat => _account.IsConnected;

    public override Task LoadAsync() => RefreshModels();

    /// <summary>
    /// Pide al SDK los modelos disponibles para esta cuenta. Si no se puede (sin red, sin
    /// credencial, sin asiento) deja el modelo configurado con un aviso: Ajustes sigue usable y no
    /// se inventa una lista que luego el asiento rechazaría.
    /// </summary>
    [RelayCommand]
    private async Task RefreshModels()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        // Acotado: pedir la lista arranca el runtime de Copilot, y una red que traga paquetes
        // dejaría Ajustes girando para siempre. Vencido el plazo se enseña el modelo configurado.
        using var timeout = new CancellationTokenSource(ModelListTimeout);
        try
        {
            IReadOnlyList<AgentModel> models = await _agent.ListModelsAsync(timeout.Token);
            if (models.Count == 0)
            {
                ModelsNotice = "El SDK no devolvió ningún modelo; se mantiene el configurado.";
                return;
            }

            string chosen = SelectedModelId;
            Models.Clear();
            foreach (AgentModel model in models)
            {
                Models.Add(ModelOption.From(model));
            }

            // El modelo guardado puede haber desaparecido del catálogo (retirado, o cambio de
            // plan). Se conserva como opción marcada en vez de saltar en silencio a otro.
            if (!string.IsNullOrWhiteSpace(chosen) && Models.All(m => m.Id != chosen))
            {
                Models.Insert(0, ModelOption.Unverified(chosen));
                ModelsNotice = $"El modelo «{chosen}» ya no figura entre los de tu cuenta.";
            }
            else
            {
                ModelsNotice = string.Empty;
            }

            SelectedModelId = chosen;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            ModelsNotice = "No se pudo obtener la lista de modelos (Copilot no respondió a tiempo). "
                + "Se muestra el modelo configurado.";
        }
        catch (Exception ex)
        {
            ModelsNotice = "No se pudo obtener la lista de modelos ("
                + ex.Message.Trim()
                + "). Se muestra el modelo configurado.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AppSettings BuildSettings()
    {
        AppSettings s = _settings.Current;
        s.Editor = Editor;
        s.Theme = IsLightTheme ? "light" : "dark";
        s.PollingSeconds = Math.Max(15, PollingSeconds);
        // Se parte de los umbrales vigentes y solo se pisan los editables: construir un
        // Thresholds nuevo devolvía MaxTokensPerUnit y ClaimTtlMinutes a sus valores por defecto
        // cada vez que alguien pulsaba Guardar.
        s.DefaultThresholds.LargeUnitLoc = LargeUnitLoc;
        s.DefaultThresholds.FreshnessDays = FreshnessDays;
        // Tope 1 = una pasada única; por eso el barrido no necesita ningún selector de modo por
        // lanzamiento (F5.1).
        s.MaxPassesPerUnit = Math.Max(1, MaxPassesPerUnit);
        s.CopilotModel = string.IsNullOrWhiteSpace(SelectedModelId)
            ? s.CopilotModel
            : SelectedModelId.Trim();
        s.EnableAssistedFix = EnableAssistedFix;
        s.CopilotTimeoutMinutes = Math.Max(1, CopilotTimeoutMinutes);
        s.HubUrlOverride = string.IsNullOrWhiteSpace(HubUrlOverride) ? null : HubUrlOverride.Trim();
        s.RequireTlsRevocationCheck = RequireTlsRevocationCheck;
        return s;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.Save(BuildSettings());
        if (!string.IsNullOrEmpty(Pat))
        {
            _settings.SetPat(Pat);
            Pat = string.Empty;
            HasStoredPat = true;
        }

        MaxPassesPerUnit = _settings.Current.MaxPassesPerUnit;
        ThemeService.Apply(IsLightTheme ? "light" : "dark");
        OnPropertyChanged(nameof(EffectiveHubUrl));
        StatusMessage = "Ajustes guardados.";
    }

    [RelayCommand]
    private void ClearPat()
    {
        _settings.SetPat(null);
        Pat = string.Empty;
        HasStoredPat = false;
        StatusMessage = "PAT borrado.";
    }
}
