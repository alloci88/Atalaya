using Atalaya.App.Services;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Ajustes (§8): identity, hub URL/PAT, editor, thresholds, theme, polling.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly HubContext _hub;

    public SettingsViewModel(SettingsService settings, HubContext hub)
    {
        _settings = settings;
        _hub = hub;
        AppSettings s = settings.Current;
        _gitUserName = s.GitUserName ?? string.Empty;
        _gitUserEmail = s.GitUserEmail ?? string.Empty;
        _hubRepoUrl = s.HubRepoUrl ?? string.Empty;
        _editor = s.Editor;
        _isLightTheme = string.Equals(s.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _pollingSeconds = s.PollingSeconds;
        _largeUnitLoc = s.DefaultThresholds.LargeUnitLoc;
        _freshnessDays = s.DefaultThresholds.FreshnessDays;
        _enableAssistedFix = s.EnableAssistedFix;
    }

    public override string Title => "Ajustes";

    [ObservableProperty] private string _gitUserName;
    [ObservableProperty] private string _gitUserEmail;
    [ObservableProperty] private string _hubRepoUrl;
    [ObservableProperty] private string _organizationName = "Mi organización";
    [ObservableProperty] private string _pat = string.Empty;
    [ObservableProperty] private string _editor;
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private int _pollingSeconds;
    [ObservableProperty] private int _largeUnitLoc;
    [ObservableProperty] private int _freshnessDays;
    [ObservableProperty] private bool _enableAssistedFix;
    [ObservableProperty] private string _statusMessage = string.Empty;

    private AppSettings BuildSettings()
    {
        AppSettings s = _settings.Current;
        s.GitUserName = NullIfBlank(GitUserName);
        s.GitUserEmail = NullIfBlank(GitUserEmail);
        s.HubRepoUrl = NullIfBlank(HubRepoUrl);
        s.Editor = Editor;
        s.Theme = IsLightTheme ? "light" : "dark";
        s.PollingSeconds = Math.Max(15, PollingSeconds);
        s.DefaultThresholds = new Thresholds
        {
            LargeUnitLoc = LargeUnitLoc,
            FreshnessDays = FreshnessDays,
        };
        s.EnableAssistedFix = EnableAssistedFix;
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
        }

        ThemeService.Apply(IsLightTheme ? "light" : "dark");
        StatusMessage = "Ajustes guardados.";
    }

    [RelayCommand]
    private async Task ConnectHub()
    {
        Save();
        if (!_hub.IsConfigured)
        {
            StatusMessage = "Indica la URL del repo del hub.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Conectando con el hub…";
        try
        {
            await Task.Run(() =>
            {
                _hub.EnsureHub();
                if (_hub.Store.TryReadHub() is null)
                {
                    // Empty remote — initialize hub.json and push (§11 "montar el hub").
                    _hub.Store.WriteHub(new HubInfo { OrganizationName = OrganizationName });
                    _hub.Sync?.CommitAndPush("hub: init");
                }
            });

            StatusMessage = $"Hub conectado. Estado de sync: {_hub.Health}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error conectando: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
