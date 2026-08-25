using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Atalaya.App.Services;
using Atalaya.Domain.Model;
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
    private readonly LiveSessionService _live;
    private readonly InterruptedSessionRecovery _recovery;

    public MainViewModel(
        NavigationService navigation,
        HubContext hub,
        SettingsService settings,
        GitHubAccountService account,
        LiveSessionService live,
        InterruptedSessionRecovery recovery)
    {
        Navigation = navigation;
        _hub = hub;
        _settings = settings;
        _account = account;
        _live = live;
        _recovery = recovery;
        _live.Changed += SyncSession;
        _live.Completed += OnSessionCompleted;
        _account.Changed += SyncAccount;
        // Connecting clones and pulls the hub off the UI thread; without this the indicator would
        // stay amber until the next polling tick even though the sync already succeeded.
        _hub.SyncStateChanged += OnSyncStateChanged;
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

    // ---- Sesión (F5.2, Hito 1) ----

    /// <summary>Hay una auditoría corriendo: el item de navegación late.</summary>
    [ObservableProperty]
    private bool _isSessionRunning;

    /// <summary>Hay algo que enseñar en V5: la sesión en curso o el cierre de la última.</summary>
    [ObservableProperty]
    private bool _hasSession;

    /// <summary>«Sesión en vivo» mientras corre; «Última sesión» cuando termina.</summary>
    [ObservableProperty]
    private string _sessionNavLabel = "Sesión en vivo";

    /// <summary>Línea de la barra inferior: «Auditando app · unidad 3/10 · pasada 2».</summary>
    [ObservableProperty]
    private string _sessionProgress = string.Empty;

    /// <summary>La sesión sigue viva: cerrar la app debe preguntar antes (Hito 1).</summary>
    public bool SessionIsRunning => _live.IsRunning;

    /// <summary>Detiene la sesión por el camino ORDENADO de F5.1b. No hay un segundo camino.</summary>
    public void StopSession() => _live.Stop();

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

        // D-110: una sesión que murió con el proceso deja su marca; se cierra ANTES de nada más,
        // para que sus claims no bloqueen la sesión que el usuario vaya a lanzar ahora.
        RecoverInterruptedSession();

        IsBusy = true;
        try
        {
            await Task.Run(_hub.EnsureHub);
            SyncHealth = _hub.Health;

            // EnsureHub does not throw for a merely failed pull (offline is normal), so say so
            // here instead of leaving an unexplained amber light.
            if (SyncHealth != SyncHealth.Green)
            {
                Toasts.Add("No se pudo sincronizar el hub. Revisa «Cuenta».");
            }
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

    /// <summary>
    /// Cierra la sesión que se quedó abierta por un cierre forzado y lo dice (D-110). Nunca lanza:
    /// un fallo recuperando no puede impedir arrancar la aplicación.
    /// </summary>
    private void RecoverInterruptedSession()
    {
        try
        {
            if (_recovery.RecoverIfNeeded() is { } recovered)
            {
                Toasts.Add(recovered.Message);
            }
        }
        catch (Exception ex)
        {
            Toasts.Add($"No se pudo recuperar una sesión interrumpida: {ex.Message}");
        }
    }

    private void SyncSession() => OnUiThread(() =>
    {
        IsSessionRunning = _live.IsRunning;
        HasSession = _live.HasSession;
        SessionNavLabel = _live.IsRunning ? "Sesión en vivo" : "Última sesión";
        SessionProgress = _live.ProgressLine;
    });

    /// <summary>
    /// Terminó una sesión que quizá nadie estaba mirando: el resumen no puede perderse en una
    /// línea fugaz de una vista cerrada (Hito 1).
    /// </summary>
    private void OnSessionCompleted(SessionResult result) => OnUiThread(() =>
    {
        SessionCounters c = result.Counters;
        Toasts.Add((result.Interrupted ? "Sesión detenida" : "Sesión completada")
            + $": {c.New} nuevos, {c.Confirmed} confirmados, {c.Resolved} resueltos"
            + (c.Disputed > 0 ? $", ⚖ {c.Disputed} disputados" : "")
            + ". Abre «Última sesión» para el desglose.");
        SyncSession();
    });

    private void SyncAccount() => OnUiThread(() =>
    {
        GitHubAccount? account = _account.Current;
        AccountAvatarUrl = account?.AvatarUrl;
        AccountLabel = account?.Login ?? "Sin cuenta";
        AccountNeedsAttention = _account.NeedsReconnect;
        SyncHealth = _hub.Health;
    });

    private void OnSyncStateChanged() => OnUiThread(() =>
    {
        SyncHealth = _hub.Health;
        AccountNeedsAttention = _account.NeedsReconnect;
    });

    /// <summary>The hub events fire from background pulls; marshal before touching bound state.</summary>
    private static void OnUiThread(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
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

    /// <summary>Abre V5 con el estado al día — la vista se reconstruye desde el servicio.</summary>
    [RelayCommand]
    private Task ShowSession() => Navigation.NavigateToAsync<SessionViewModel>();

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
            // Nada de un aviso por fichero: eran rutas y ULIDs que no le dicen nada a nadie, se
            // apilaban unos encima de otros y tapaban la barra de estado y el indicador de
            // conexión. La página se recarga sola, que es la señal útil. Los avisos de verdad
            // (result.Notifications) sí se muestran, arriba.
            await page.LoadAsync();
        }

        while (Toasts.Count > 6)
        {
            Toasts.RemoveAt(0);
        }
    }
}
