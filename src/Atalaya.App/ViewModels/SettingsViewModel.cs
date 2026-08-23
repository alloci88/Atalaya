using Atalaya.App.Services;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Ajustes (§8) — preferences ONLY since F2 (D4): theme, editor, thresholds, polling and feature
/// flags. Everything about the connection (hub URL, PAT, git identity, "Comprobar Copilot") moved
/// to the Cuenta page. What remains here of the old world lives collapsed under "Opciones
/// avanzadas": the PAT fallback for organizations that block OAuth Apps, and a development-only
/// hub URL override.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly GitHubAccountService _account;

    public SettingsViewModel(SettingsService settings, HubContext hub, GitHubAccountService account)
    {
        _settings = settings;
        _hub = hub;
        _account = account;
        AppSettings s = settings.Current;
        _editor = s.Editor;
        _isLightTheme = string.Equals(s.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _pollingSeconds = s.PollingSeconds;
        _largeUnitLoc = s.DefaultThresholds.LargeUnitLoc;
        _freshnessDays = s.DefaultThresholds.FreshnessDays;
        _enableAssistedFix = s.EnableAssistedFix;
        _copilotTimeoutMinutes = s.CopilotTimeoutMinutes;
        _hubUrlOverride = s.HubUrlOverride ?? string.Empty;
        _hasStoredPat = settings.GetPat() is not null;
        _requireTlsRevocationCheck = s.RequireTlsRevocationCheck;
    }

    public override string Title => "Ajustes";

    [ObservableProperty] private string _editor;
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private int _pollingSeconds;
    [ObservableProperty] private int _largeUnitLoc;
    [ObservableProperty] private int _freshnessDays;
    [ObservableProperty] private bool _enableAssistedFix;
    [ObservableProperty] private int _copilotTimeoutMinutes;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // --- Opciones avanzadas (colapsadas) ---
    [ObservableProperty] private string _pat = string.Empty;
    [ObservableProperty] private string _hubUrlOverride;
    [ObservableProperty] private bool _hasStoredPat;
    [ObservableProperty] private bool _requireTlsRevocationCheck;

    /// <summary>The hub actually in use — shown read-only under advanced options, for support.</summary>
    public string EffectiveHubUrl => _hub.HubUrl ?? "(sin configurar)";

    /// <summary>The PAT is ignored while an account is connected (D3).</summary>
    public bool AccountOverridesPat => _account.IsConnected;

    private AppSettings BuildSettings()
    {
        AppSettings s = _settings.Current;
        s.Editor = Editor;
        s.Theme = IsLightTheme ? "light" : "dark";
        s.PollingSeconds = Math.Max(15, PollingSeconds);
        s.DefaultThresholds = new Thresholds
        {
            LargeUnitLoc = LargeUnitLoc,
            FreshnessDays = FreshnessDays,
        };
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
