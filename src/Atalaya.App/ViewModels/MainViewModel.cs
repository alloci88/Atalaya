using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Storage.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// The shell view-model: navigation rail commands, the permanent sync indicator (§3), live
/// toasts for other users' pushes (§8), and the polling refresh loop.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly HubContext _hub;
    private readonly SettingsService _settings;

    public MainViewModel(NavigationService navigation, HubContext hub, SettingsService settings)
    {
        Navigation = navigation;
        _hub = hub;
        _settings = settings;
    }

    public NavigationService Navigation { get; }

    public ObservableCollection<string> Toasts { get; } = new();

    [ObservableProperty]
    private SyncHealth _syncHealth = SyncHealth.Amber;

    [ObservableProperty]
    private bool _isBusy;

    public int PollingSeconds => Math.Max(15, _settings.Current.PollingSeconds);

    /// <summary>First-run: open the hub, then land on the portfolio (or settings if unconfigured).</summary>
    public async Task InitializeAsync()
    {
        if (!_hub.IsConfigured)
        {
            await Navigation.NavigateToAsync<SettingsViewModel>();
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(_hub.EnsureHub);
            SyncHealth = _hub.Health;
        }
        finally
        {
            IsBusy = false;
        }

        await ShowPortfolio();
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
    private Task NewApp() => Navigation.NavigateToAsync<OnboardingViewModel>();

    /// <summary>Polling tick (§3): pull, update the indicator, surface toasts, reload the page.</summary>
    public async Task RefreshAsync()
    {
        if (!_hub.IsConfigured || IsBusy)
        {
            return;
        }

        PullResult result = await _hub.PullAsync();
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
