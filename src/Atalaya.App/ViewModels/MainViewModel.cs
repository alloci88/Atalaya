using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Storage.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// The shell view-model: navigation rail commands, the permanent sync indicator (§3), the
/// connected-account indicator (D4), live toasts for other users' pushes (§8), and the polling
/// refresh loop.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly HubContext _hub;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;

    public MainViewModel(
        NavigationService navigation,
        HubContext hub,
        SettingsService settings,
        GitHubAccountService account)
    {
        Navigation = navigation;
        _hub = hub;
        _settings = settings;
        _account = account;
        _account.Changed += SyncAccount;
        SyncAccount();
    }

    public NavigationService Navigation { get; }

    public ObservableCollection<string> Toasts { get; } = new();

    [ObservableProperty]
    private SyncHealth _syncHealth = SyncHealth.Amber;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _accountAvatarUrl;

    [ObservableProperty]
    private string _accountLabel = "Sin cuenta";

    /// <summary>Amber in the status bar: connected, but GitHub rejected the token (D3).</summary>
    [ObservableProperty]
    private bool _accountNeedsAttention;

    public int PollingSeconds => Math.Max(15, _settings.Current.PollingSeconds);

    /// <summary>
    /// First run (D4): with no account we land straight on the welcome = the Cuenta page in its
    /// disconnected state. No hub URL, no PAT, no git identity, no console — just one button.
    /// Users configured before F2 (PAT + hub) keep going straight to the portfolio.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!_account.IsConnected && !HasLegacyCredentials())
        {
            await Navigation.NavigateToAsync<AccountViewModel>();
            return;
        }

        if (!_hub.IsConfigured)
        {
            await Navigation.NavigateToAsync<AccountViewModel>();
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(_hub.EnsureHub);
            SyncHealth = _hub.Health;
        }
        catch (Exception ex)
        {
            _account.NoteFailure(ex);
            SyncHealth = SyncHealth.Red;
            Toasts.Add("No se pudo sincronizar el hub. Revisa «Cuenta».");
        }
        finally
        {
            IsBusy = false;
        }

        await ShowPortfolio();
    }

    /// <summary>A pre-F2 setup: a PAT stored on this machine is enough to keep working (D4).</summary>
    private bool HasLegacyCredentials() => _settings.GetPat() is not null;

    private void SyncAccount()
    {
        GitHubAccount? account = _account.Current;
        AccountAvatarUrl = account?.AvatarUrl;
        AccountLabel = account?.Login ?? "Sin cuenta";
        AccountNeedsAttention = _account.NeedsReconnect;
        // Connecting builds the sync service and clones the hub: reflect the new health at once
        // instead of waiting for the next polling tick.
        SyncHealth = _hub.Health;
    }

    [RelayCommand]
    private Task ShowPortfolio() => Navigation.NavigateToAsync<PortfolioViewModel>();

    [RelayCommand]
    private Task ShowFindings() => Navigation.NavigateToAsync<FindingsViewModel>(vm => vm.SetApp(null));

    [RelayCommand]
    private Task ShowMetrics() => Navigation.NavigateToAsync<MetricsViewModel>();

    [RelayCommand]
    private Task ShowImport() => Navigation.NavigateToAsync<ImportViewModel>();

    [RelayCommand]
    private Task ShowSettings() => Navigation.NavigateToAsync<SettingsViewModel>();

    [RelayCommand]
    private Task ShowAccount() => Navigation.NavigateToAsync<AccountViewModel>();

    [RelayCommand]
    private Task NewApp() => Navigation.NavigateToAsync<OnboardingViewModel>();

    /// <summary>Polling tick (§3): pull, update the indicator, surface toasts, reload the page.</summary>
    public async Task RefreshAsync()
    {
        if (!_hub.IsConfigured || IsBusy || _hub.Sync is null)
        {
            return;
        }

        PullResult result;
        try
        {
            result = await _hub.PullAsync();
        }
        catch (Exception ex)
        {
            if (_account.NoteFailure(ex))
            {
                Toasts.Add("GitHub rechazó tus credenciales. Vuelve a conectar en «Cuenta».");
            }

            SyncHealth = SyncHealth.Red;
            return;
        }

        SyncHealth = _hub.Health;

        foreach (string note in result.Notifications)
        {
            Toasts.Add(note);
        }

        if (result.HasChanges && Navigation.Current is { } page)
        {
            foreach (HubChange change in result.Changes.Take(3))
            {
                Toasts.Add($"Cambio: {change.Kind} {change.RelativePath}");
            }

            await page.LoadAsync();
        }

        while (Toasts.Count > 6)
        {
            Toasts.RemoveAt(0);
        }
    }
}
