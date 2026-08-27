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

    /// <summary>F6.9: el arreglo asistido tiene su propio item pulsante, igual que la sesión.</summary>
    private readonly LiveFixService _fix;

    private readonly InterruptedSessionRecovery _recovery;
    private readonly DisplayIdService _aliases;
    private readonly ToastCenter _toasts;

    public MainViewModel(
        NavigationService navigation,
        HubContext hub,
        SettingsService settings,
        GitHubAccountService account,
        LiveSessionService live,
        LiveFixService fix,
        InterruptedSessionRecovery recovery,
        DisplayIdService aliases,
        ToastCenter toasts)
    {
        Navigation = navigation;
        _toasts = toasts;
        _hub = hub;
        _settings = settings;
        _account = account;
        _live = live;
        _fix = fix;
        _recovery = recovery;
        _aliases = aliases;
        _live.Changed += SyncSession;
        _live.Completed += OnSessionCompleted;
        // F5.15: un fallo de arranque no puede quedarse dentro de una vista que quiza nadie esta
        // mirando. Sale por toast, como el resumen de cierre.
        _live.Failed += OnSessionFailed;
        _live.Notice += OnSessionNotice;
        _fix.Changed += SyncFix;
        _fix.Notice += OnSessionNotice;
        _fix.Completed += OnFixCompleted;
        _account.Changed += SyncAccount;
        // Connecting clones and pulls the hub off the UI thread; without this the indicator would
        // stay amber until the next polling tick even though the sync already succeeded.
        _hub.SyncStateChanged += OnSyncStateChanged;
        SyncAccount();
        // Y el de la sesión, igual que el de la cuenta (F5.13). El acceso a V5 colgaba SOLO de
        // cazar un evento: si la carcasa nacía con una sesión ya viva —o se perdía un aviso— no
        // había item en el rail y no quedaba forma de volver a una auditoría en curso. Un freno de
        // emergencia no puede depender de haber estado escuchando en el momento justo; se deriva
        // del estado, que es lo que siempre se puede volver a preguntar.
        SyncSession();
        SyncFix();
    }

    public NavigationService Navigation { get; }

    /// <summary>
    /// Avisos EFÍMEROS (F5.3 §3). No son elementos de la barra de estado: caducan solos y se
    /// pueden descartar de un clic. La barra inferior se queda con lo estable — sync, cuenta y
    /// «Auditando…» — y nada más.
    /// </summary>
    public ObservableCollection<Toast> Toasts => _toasts.Items;

    /// <summary>Retira lo caducado. Lo llama el temporizador de la ventana (cada segundo).</summary>
    public void SweepToasts() => _toasts.Sweep();

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

    // ---- Arreglo asistido (F6.9) ----

    /// <summary>Hay un arreglo corriendo: el item late.</summary>
    [ObservableProperty]
    private bool _isFixRunning;

    /// <summary>Hay algo que enseñar en «Arreglo asistido»: en curso, terminado o con cambios sin cerrar.</summary>
    [ObservableProperty]
    private bool _hasFix;

    [ObservableProperty]
    private string _fixNavLabel = "Arreglo asistido";

    [ObservableProperty]
    private string _fixProgress = string.Empty;

    /// <summary>
    /// Un arreglo en curso: cerrar la aplicación pregunta antes, igual que con una auditoría. Y por
    /// la misma razón, con una diferencia importante que el mensaje dice: los cambios YA ESTÁN en
    /// el clon y se quedan ahí.
    /// </summary>
    public bool FixIsRunning => _fix.IsRunning;

    /// <summary>Parada ordenada del arreglo. Lo aplicado se conserva y queda registrado.</summary>
    public void StopFix() => _fix.Stop();

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
                _toasts.Show("No se pudo sincronizar el hub. Revisa «Cuenta».");
            }
        }
        catch (Exception ex)
        {
            _account.NoteFailure(ex);
            SyncHealth = SyncHealth.Red;
            _toasts.Show("No se pudo sincronizar el hub. Revisa «Cuenta».");
        }
        finally
        {
            IsBusy = false;
        }

        BackfillDisplayIds();

        await ShowPortfolio();
    }

    /// <summary>
    /// Reparte alias legibles a los hallazgos que se quedaron sin uno (F5.6 §3, D-228). El paso
    /// post-push del §2 nunca se llegó a cablear, así que todo lo detectado hasta ahora mostraba
    /// «(sin alias todavía)». Es idempotente: en cuanto todos lo tienen, no hace nada y no escribe.
    /// Nunca lanza — quedarse sin alias no puede impedir arrancar.
    /// </summary>
    private void BackfillDisplayIds()
    {
        try
        {
            int assigned = _aliases.BackfillAll();
            if (assigned > 0)
            {
                _toasts.Show($"{assigned} hallazgo(s) han recibido su identificador legible.");
            }
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudieron asignar identificadores legibles: {ex.Message}");
        }
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
                _toasts.Show(recovered.Message);
            }
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudo recuperar una sesión interrumpida: {ex.Message}");
        }
    }

    private void SyncSession() => OnUiThread(() =>
    {
        IsSessionRunning = _live.IsRunning;
        HasSession = _live.HasSession;
        SessionNavLabel = _live.IsRunning
            ? "Sesión en vivo"
            : _live.HasFailed ? "Sesión fallida" : "Última sesión";
        SessionProgress = _live.ProgressLine;
    });

    /// <summary>
    /// Terminó una sesión que quizá nadie estaba mirando: el resumen no puede perderse en una
    /// línea fugaz de una vista cerrada (Hito 1).
    /// </summary>
    private void OnSessionCompleted(SessionResult result) => OnUiThread(() =>
    {
        SessionCounters c = result.Counters;
        _toasts.Show(
            (result.Interrupted ? "Sesión detenida" : "Sesión completada")
            + $": {c.New} nuevos, {c.Confirmed} confirmados, {c.Resolved} resueltos"
            + (c.Disputed > 0 ? $", ⚖ {c.Disputed} disputados" : "")
            + ". Abre «Última sesión» para el desglose.",
            ToastKind.SessionCompleted);
        SyncSession();
    });

    /// <summary>
    /// La sesión no arrancó o reventó (F5.15). El toast dura y se puede descartar; el detalle vive
    /// en V5, adonde el item del rail —que ahora SIGUE ahí tras un fallo— lleva de vuelta.
    /// </summary>
    private void OnSessionFailed(string message) => OnUiThread(() =>
    {
        _toasts.Show(message, ToastKind.SessionCompleted);
        SyncSession();
    });

    /// <summary>Un aviso sin fallo: típicamente «se ha cambiado el modelo a X».</summary>
    private void OnSessionNotice(string message) => OnUiThread(() => _toasts.Show(message));

    private void SyncFix() => OnUiThread(() =>
    {
        IsFixRunning = _fix.IsRunning;
        // El item se queda mientras QUEDEN CAMBIOS sin cerrar, aunque la sesión ya terminara: es
        // el único camino de vuelta al botón de descartar, y perderlo al navegar dejaría al
        // usuario con cambios del agente en el clon y sin forma de deshacerlos de un clic.
        HasFix = _fix.HasSession || _fix.HasPendingChanges;
        FixNavLabel = _fix.IsRunning
            ? "Arreglo asistido"
            : _fix.HasFailed ? "Arreglo fallido" : "Último arreglo";
        FixProgress = _fix.ProgressLine;
    });

    /// <summary>
    /// Terminó un arreglo que quizá nadie estaba mirando. El toast dice lo único que no se puede
    /// perder: que los cambios están en el clon sin commitear.
    /// </summary>
    private void OnFixCompleted(string message) => OnUiThread(() =>
    {
        _toasts.Show(message, ToastKind.SessionCompleted);
        SyncFix();
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

    /// <summary>Un clic en el aviso lo retira sin esperar a que caduque.</summary>
    [RelayCommand]
    private void DismissToast(Toast? toast) => _toasts.Dismiss(toast);

    [RelayCommand]
    private Task ShowPortfolio() => Navigation.NavigateToAsync<PortfolioViewModel>();

    [RelayCommand]
    private Task ShowFindings() => Navigation.NavigateToAsync<FindingsViewModel>(vm => vm.SetApp(null));

    [RelayCommand]
    private Task ShowMetrics() => Navigation.NavigateToAsync<MetricsViewModel>();

    /// <summary>V7 Informes (F6.3): la lista de todo lo que las auditorías han dejado escrito.</summary>
    [RelayCommand]
    private Task ShowReports() => Navigation.NavigateToAsync<ReportsViewModel>();

    [RelayCommand]
    private Task ShowSettings() => Navigation.NavigateToAsync<SettingsViewModel>();

    /// <summary>Abre V5 con el estado al día — la vista se reconstruye desde el servicio.</summary>
    [RelayCommand]
    private Task ShowSession() => Navigation.NavigateToAsync<SessionViewModel>();

    /// <summary>Abre V8 con el estado al día — la vista se reconstruye desde el servicio (F6.9).</summary>
    [RelayCommand]
    private Task ShowFix() => Navigation.NavigateToAsync<AssistedFixViewModel>();

    [RelayCommand]
    private Task ShowAccount() => Navigation.NavigateToAsync<AccountViewModel>();

    [RelayCommand]
    private Task NewApp() => Navigation.NavigateToAsync<OnboardingViewModel>();

    /// <summary>
    /// La ventana ha vuelto al primer plano (F5.8 §1). Mientras Atalaya estaba detrás, el usuario
    /// ha podido mover, borrar o volver a clonar la carpeta de un repo — es justo cuando pasa—, y
    /// el piloto de vinculación se quedaría enseñando lo que era verdad hace media hora.
    /// <para>
    /// Solo se recarga la página VIVA, y solo si es una de las dos que leen ese estado. Recargar
    /// cualquier página al enfocar sería una sorpresa: en la ficha de un hallazgo tiraría lo que
    /// se estuviera escribiendo en un comentario.
    /// </para>
    /// </summary>
    public Task OnWindowActivatedAsync()
        => ShellRefresh.ShouldReloadOnActivate(Navigation.Current, IsBusy)
            ? Navigation.Current!.LoadAsync()
            : Task.CompletedTask;

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
                _toasts.Show("GitHub rechazó tus credenciales. Vuelve a conectar en «Cuenta».");
            }

            SyncHealth = SyncHealth.Red;
            return;
        }

        SyncHealth = _hub.Health;

        foreach (string note in result.Notifications)
        {
            _toasts.Show(note);
        }

        if (result.HasChanges && Navigation.Current is { } page)
        {
            // Nada de un aviso por fichero: eran rutas y ULIDs que no le dicen nada a nadie, se
            // apilaban unos encima de otros y tapaban la barra de estado y el indicador de
            // conexión. La página se recarga sola, que es la señal útil. Los avisos de verdad
            // (result.Notifications) sí se muestran, arriba.
            await page.LoadAsync();
        }

    }
}

/// <summary>
/// Qué se recarga al volver la ventana al primer plano (F5.8 §1). Separado de la carcasa para
/// poder probarlo: la regla es corta pero su parte importante es lo que NO hace.
/// </summary>
public static class ShellRefresh
{
    /// <summary>
    /// Solo las dos páginas que leen el estado de vinculación local, y solo cuando no hay una
    /// operación en curso.
    /// <para>
    /// Recargar cualquier página al enfocar sería una sorpresa: en la ficha de un hallazgo tiraría
    /// el comentario a medio escribir, y en Ajustes, lo que se estuviera cambiando sin guardar.
    /// </para>
    /// </summary>
    public static bool ShouldReloadOnActivate(ViewModelBase? current, bool busy)
        => !busy && current is PortfolioViewModel or InventoryViewModel;
}
