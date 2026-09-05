using System.Collections.ObjectModel;
using System.Diagnostics;
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

    /// <summary>Ofrecer configurar el ciclo recién abierto tras un cierre (F17 §4). Opcionales: los tests de la carcasa no lo montan.</summary>
    private readonly CycleConfigService? _cycleConfig;

    private readonly CycleConfigFlow? _configFlow;
    private readonly GitHubAccountService _account;
    private readonly LiveSessionService _live;

    /// <summary>F6.9: el arreglo asistido tiene su propio item pulsante, igual que la sesión.</summary>
    private readonly LiveFixService _fix;

    private readonly InterruptedSessionRecovery _recovery;
    private readonly DisplayIdService _aliases;
    private readonly ToastCenter _toasts;

    /// <summary>F8 §3: el chequeo de cortesía de versión nueva. Opcional — sin él, no hay banner.</summary>
    private readonly UpdateCheckService? _updates;

    /// <summary>F11: actualizarse de verdad. Opcional — sin él, el banner solo lleva al navegador.</summary>
    private readonly SelfUpdateService? _selfUpdate;

    public MainViewModel(
        NavigationService navigation,
        HubContext hub,
        SettingsService settings,
        GitHubAccountService account,
        LiveSessionService live,
        LiveFixService fix,
        InterruptedSessionRecovery recovery,
        DisplayIdService aliases,
        ToastCenter toasts,
        UpdateCheckService? updates = null,
        SelfUpdateService? selfUpdate = null,
        CycleConfigService? cycleConfig = null,
        CycleConfigFlow? configFlow = null,
        ActiveApp? activeApp = null)
    {
        // F26 §A: opcional como los demás de esta cola, para que los tests de la carcasa puedan
        // construirla sin montar el contenedor. Cuando falta se crea uno propio: el raíl sigue
        // funcionando, simplemente nadie más escribe en él.
        ActiveApplication = activeApp ?? new ActiveApp();
        _cycleConfig = cycleConfig;
        _configFlow = configFlow;
        _selfUpdate = selfUpdate;
        Navigation = navigation;
        _toasts = toasts;
        _hub = hub;
        _settings = settings;
        _account = account;
        _live = live;
        _fix = fix;
        _recovery = recovery;
        _aliases = aliases;
        _updates = updates;
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

        // F26 §A — el raíl y la miga se derivan de dónde estás, así que se rehacen en cada
        // navegación. Suscribirse aquí (y no que cada comando lo llame) es lo que garantiza que
        // NINGUNA forma de llegar a una página se olvide de encender su entrada: también las que
        // navegan desde dentro de otra vista, que son la mayoría.
        Navigation.Navigated += (_, _) => RefreshShell();
        RefreshShell();
    }

    public NavigationService Navigation { get; }

    /// <summary>En qué aplicación estás. La comparte todo el que navega dentro de una (F26 §A).</summary>
    public ActiveApp ActiveApplication { get; }

    /// <summary>
    /// Avisos EFÍMEROS (F5.3 §3). No son elementos de la barra de estado: caducan solos y se
    /// pueden descartar de un clic. La barra inferior se queda con lo estable — sync, cuenta y
    /// «Auditando…» — y nada más.
    /// </summary>
    public ObservableCollection<Toast> Toasts => _toasts.Items;

    /// <summary>Retira lo caducado. Lo llama el temporizador de la ventana (cada segundo).</summary>
    public void SweepToasts() => _toasts.Sweep();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncTooltip))]
    private SyncHealth _syncHealth = SyncHealth.Amber;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _accountAvatarUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountTooltip))]
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
    [NotifyPropertyChangedFor(nameof(HasFooter))]
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
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    private string _fixProgress = string.Empty;

    /// <summary>
    /// Un arreglo en curso: cerrar la aplicación pregunta antes, igual que con una auditoría. Y por
    /// la misma razón, con una diferencia importante que el mensaje dice: los cambios YA ESTÁN en
    /// el clon y se quedan ahí.
    /// </summary>
    public bool FixIsRunning => _fix.IsRunning;

    /// <summary>Parada ordenada del arreglo. Lo aplicado se conserva y queda registrado.</summary>
    public void StopFix() => _fix.Stop();

    public int PollingSeconds => Math.Max(SettingsLimits.MinPollingSeconds, _settings.Current.PollingSeconds);

    /// <summary>Dónde estaba la ventana la última vez (F26 §A, D-956).</summary>
    public WindowPlacement SavedWindowPlacement => _settings.Current.Window;

    /// <summary>
    /// Apunta dónde ha quedado la ventana. Se llama UNA vez, al cerrar; guardar en cada
    /// redimensionado sería escribir el fichero de ajustes cientos de veces por gesto.
    /// </summary>
    public void SaveWindowPlacement(WindowPlacement placement)
    {
        // UNA VENTANA QUE NUNCA SE ENSEÑÓ NO TIENE GEOMETRÍA QUE GUARDAR. `RestoreBounds` de una
        // ventana sin mostrar es `Empty`, es decir infinitos, y `System.Text.Json` no sabe escribir
        // un infinito: `--selfcheck` construye la carcasa y al cerrar el proceso el manejador de
        // `Closed` intentaba guardar eso, así que el autochequeo imprimía «Arranca.» y a
        // continuación reventaba con una excepción sin recoger — y el código de salida que mira el
        // workflow de release dejaba de significar nada. Lo que no es un número no se guarda.
        if (!IsUsable(placement))
        {
            return;
        }

        var settings = _settings.Current;

        // El raíl NO viene en lo que captura la ventana —es una preferencia, no una geometría— así
        // que se arrastra. Sin esto, cerrar la aplicación borraba el plegado que acababas de
        // elegir: se guardaba un `WindowPlacement` recién hecho encima del que lo tenía.
        placement.RailCollapsed = settings.Window.RailCollapsed;
        placement.RailPinned = settings.Window.RailPinned;

        settings.Window = placement;
        _settings.Save(settings);
    }

    /// <summary>Una geometría con números de verdad y tamaño positivo.</summary>
    private static bool IsUsable(WindowPlacement placement)
        => Finite(placement.Left) && Finite(placement.Top)
           && Finite(placement.Width) && Finite(placement.Height)
           && placement.Width > 0 && placement.Height > 0;

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

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
    /// Autocuración al arrancar (D-110 y BUGFIX-ACTIVIDAD): cierra la sesión que se quedó abierta
    /// por un cierre forzado <b>y</b> suelta los claims de esta máquina que quedaron sueltos sin
    /// marca detrás. Nunca lanza: un fallo limpiando no puede impedir arrancar la aplicación.
    /// </summary>
    private void RecoverInterruptedSession()
    {
        try
        {
            if (_recovery.CleanUpAtStartup().Message is { } message)
            {
                _toasts.Show(message);
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
        // F26 §A: el raíl enseña la sesión, así que cambia con ella. VA AL FINAL: el raíl copia el
        // rótulo y el estado en su entrada, así que rehacerlo antes de haberlos calculado lo
        // dejaría con los de la vez anterior.
        RefreshShell();
        // F11: una sesión que arranca retira el botón de actualizar, y una que termina lo
        // devuelve. Colgarlo del mismo evento que ya mueve el rail evita que el botón se quede
        // puesto para que alguien lo pulse a mitad de una auditoría pagada.
        RefreshUpdateOffer();
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
        AnnounceCycleClose(result.CycleClose);
        SyncSession();
        _ = OfferCycleConfigAsync(result.CycleClose);
    });

    /// <summary>
    /// Quien cerró el ciclo ve el diálogo para cambiar la configuración que el siguiente acaba de
    /// heredar (F17 §4). El cierre ya está hecho y publicado —no espera a nadie—; esto es la
    /// oportunidad de cambiar la lupa mientras el ciclo nuevo todavía no tiene trabajo bajo ella.
    /// Cancelar deja la herencia tal cual.
    /// </summary>
    internal async Task OfferCycleConfigAsync(CycleCloseResult close)
    {
        if (!close.Closed || _cycleConfig is null || _configFlow is null)
        {
            return;
        }

        CycleConfigPreview? preview = _cycleConfig.Preview(close.Slug);
        if (preview is null || preview.CycleN != close.NextCycle)
        {
            return;
        }

        CycleConfig? chosen = await _configFlow.AskAsync(preview, CycleConfigReason.Cierre);
        if (chosen is null)
        {
            return;
        }

        CycleConfigResult result = await Task.Run(() => _cycleConfig.Apply(close.Slug, chosen));
        if (result.Applied)
        {
            _toasts.Show(result.Message);
        }
    }

    // ---- Aviso de cierre de ciclo (F12 §G) ----

    /// <summary>
    /// Se ha cerrado un ciclo, y la carcasa lo dice.
    /// <para>
    /// F9.2 funcionaba con datos reales —el ciclo se cerró y sembró bien— pero lo hacía EN
    /// SILENCIO: el Portafolio pasaba a «Ciclo 2» y ya. La foto honesta existía, dentro del informe
    /// del cierre, y nadie tenía motivo para abrirlo. Un hito que no se anuncia no es un hito: es
    /// un cambio de número.
    /// </para>
    /// <para>
    /// Mismo patrón que el aviso de versión, y por los mismos motivos: banner y no toast —un toast
    /// caduca a los 8 s y si mirabas otra cosa te quedas sin enterar— y no modal, porque cerrar un
    /// ciclo es una buena noticia, no una interrupción.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _cycleClosed;

    /// <summary>«Ciclo 1 cerrado · 11/11 auditadas · 1 unidad sembrada como pendiente · Ciclo 2 abierto».</summary>
    [ObservableProperty]
    private string _cycleClosedLabel = string.Empty;

    /// <summary>La app y la sesión del cierre: es a lo que lleva «Ver el informe del cierre».</summary>
    private string _closedSlug = string.Empty;
    private string _closedReportId = string.Empty;

    /// <summary>Sin informe no se ofrece abrirlo: un enlace que no lleva a ningún sitio es peor que ninguno.</summary>
    public bool CanOpenCycleReport => _closedReportId.Length > 0;

    /// <summary>
    /// Enseña el aviso de un cierre. Público para que se pueda ejercitar sin montar una sesión
    /// entera: lo que hay que poder comprobar es que el aviso dice lo que dijo el cierre.
    /// </summary>
    public void AnnounceCycleClose(CycleCloseResult close)
    {
        if (!close.Closed)
        {
            return;
        }

        // El texto lo redacta el RESULTADO del cierre, no la interfaz (misma regla que D-746): así
        // el aviso no puede decir unos números distintos de los que produjeron el cierre.
        CycleClosedLabel = close.Headline;
        _closedSlug = close.Slug;
        _closedReportId = close.ReportSessionId;
        CycleClosed = CycleClosedLabel.Length > 0;
        OnPropertyChanged(nameof(CanOpenCycleReport));
    }

    /// <summary>El informe del cierre: la foto honesta de con qué se cerró y qué hereda el ciclo nuevo.</summary>
    [RelayCommand]
    private Task OpenCycleReport()
    {
        if (_closedReportId.Length == 0)
        {
            return Task.CompletedTask;
        }

        string slug = _closedSlug;
        string report = _closedReportId;
        CycleClosed = false;
        return Navigation.NavigateToAsync<ReportsViewModel>(vm => vm.ShowReport(slug, report));
    }

    /// <summary>Descartable, como el de versión: se ha leído y no vuelve a estorbar.</summary>
    [RelayCommand]
    private void DismissCycleClose() => CycleClosed = false;

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
        // F26 §A: y el arreglo, igual — también al final, y por lo mismo.
        RefreshShell();
        RefreshUpdateOffer();
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


    // ---- Aviso de versión nueva (F8 §3) ----

    /// <summary>
    /// Hay una versión más nueva publicada: la carcasa enseña un banner discreto.
    /// <para>
    /// <b>También en un build local</b>, donde es informativo y sin botón (BUGFIX-AVISO). Se
    /// eligió eso frente a esconderlo porque quien corre un `dist` de desarrollo es justo quien
    /// necesita enterarse de que salió una release —es como se descubrió este defecto—, y porque
    /// un banner que aparece o no según el origen del binario es una regla más que explicar. Lo
    /// que no puede pasar es que se ofrezca una acción que luego no está: el botón se decide en
    /// <see cref="SelfUpdateService.CanOffer"/> y su ausencia se explica en el propio banner.
    /// </para>
    /// <para>
    /// Banner y no toast ni modal, a conciencia. Un modal interrumpe para dar una noticia que no
    /// es urgente. Un toast caduca a los 8 s: si te pilla mirando otra cosa, te has quedado sin
    /// enterarte y no hay forma de recuperarlo. El banner se queda hasta que decides —lo abres o
    /// lo descartas— y ocupa una línea.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _updateAvailable;

    /// <summary>«Atalaya 1.2 disponible». La versión corta: es la que la gente dice en voz alta.</summary>
    [ObservableProperty]
    private string _updateLabel = string.Empty;

    /// <summary>La página de la Release. Vacía si GitHub no la dio: entonces no hay adónde llevar.</summary>
    [ObservableProperty]
    private string _updateUrl = string.Empty;

    /// <summary>Con url se ofrece «Ver novedades»; sin ella, el banner solo informa.</summary>
    public bool CanOpenUpdate => UpdateUrl.Length > 0;

    /// <summary>La versión de la que se está avisando, para poder descartarla por su número.</summary>
    private SemanticVersion? _offeredUpdate;

    /// <summary>El tag de esa Release: es lo que se le pide a GitHub al pulsar «Actualizar».</summary>
    private string _offeredTag = string.Empty;

    // ---- Actualizar desde la propia app (F11) ----

    /// <summary>
    /// «Actualizar a 1.0.4» se ofrece. Falso en un build local, sin cuenta, sin despliegue que
    /// declare el repositorio, y —sobre todo— mientras haya una sesión en curso.
    /// </summary>
    [ObservableProperty]
    private bool _canInstallUpdate;

    /// <summary>«Actualizar a 1.0.4».</summary>
    [ObservableProperty]
    private string _installUpdateLabel = string.Empty;

    /// <summary>
    /// Por qué NO se ofrece, o qué hay que tener en cuenta si se ofrece. Se enseña: un botón que
    /// no está y no dice por qué se lee como un fallo del programa.
    /// </summary>
    [ObservableProperty]
    private string _updateNotice = string.Empty;

    public bool HasUpdateNotice => UpdateNotice.Length > 0;

    /// <summary>La actualización está en marcha: el banner deja de ofrecer y pasa a contar.</summary>
    [ObservableProperty]
    private bool _updateInProgress;

    /// <summary>«Descargando 84 de 216 MB…»</summary>
    [ObservableProperty]
    private string _updateProgressText = string.Empty;

    /// <summary>0–100 durante la descarga; null en las fases que no tienen porcentaje.</summary>
    [ObservableProperty]
    private double? _updateProgressPercent;

    /// <summary>
    /// Descomprimir y sustituir no tienen porcentaje que dar —no se sabe cuánto queda—, y una
    /// barra parada al 0 % durante ese rato se lee como «se ha colgado». Indeterminada dice la
    /// verdad: está pasando algo y no sabemos cuánto falta.
    /// </summary>
    public bool UpdateProgressIndeterminate => UpdateProgressPercent is null;

    partial void OnUpdateNoticeChanged(string value) => OnPropertyChanged(nameof(HasUpdateNotice));

    partial void OnUpdateProgressPercentChanged(double? value)
        => OnPropertyChanged(nameof(UpdateProgressIndeterminate));

    /// <summary>
    /// Vuelve a mirar si se puede ofrecer «Actualizar». Se llama al recibir el aviso y en cada
    /// refresco: una auditoría que arranca tiene que hacer desaparecer el botón, no dejarlo
    /// puesto para que alguien lo pulse y tire una sesión pagada.
    /// </summary>
    private void RefreshUpdateOffer()
    {
        if (_selfUpdate is null || !UpdateAvailable || _offeredUpdate is null || UpdateInProgress)
        {
            CanInstallUpdate = false;
            return;
        }

        UpdateReadiness readiness = _selfUpdate.CanOffer();
        CanInstallUpdate = readiness.CanUpdate;
        InstallUpdateLabel = $"Actualizar a {_offeredUpdate}";
        UpdateNotice = readiness.CanUpdate ? readiness.Warning ?? string.Empty : readiness.Reason;
    }

    /// <summary>
    /// Descarga, verifica y sustituye. Lo dispara una persona; nunca se llama solo.
    /// <para>
    /// Si sale bien, esto NO vuelve: el relevo está esperando a que este proceso muera para poder
    /// sustituir la carpeta, así que lo último que hace la aplicación es cerrarse.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_selfUpdate is null || _offeredTag.Length == 0 || UpdateInProgress)
        {
            return;
        }

        UpdateInProgress = true;
        CanInstallUpdate = false;
        UpdateNotice = string.Empty;
        UpdateProgressText = "Preparando…";
        UpdateProgressPercent = null;

        var progress = new Progress<UpdateProgress>(p => OnUiThread(() =>
        {
            UpdateProgressText = p.Text;
            UpdateProgressPercent = p.Percent;
        }));

        UpdateStart result = await _selfUpdate.UpdateAsync(
            _offeredTag, UpdateUrl.Length > 0 ? UpdateUrl : null, progress, CancellationToken.None);

        if (result.HandedOff)
        {
            // El relevo ya está esperando. Cerrar es el último paso de la actualización, no una
            // consecuencia de ella: mientras este proceso viva, la carpeta no se puede tocar.
            UpdateProgressText = result.Message;
            OnUiThread(() => Application.Current?.Shutdown());
            return;
        }

        UpdateInProgress = false;
        UpdateProgressText = string.Empty;
        UpdateProgressPercent = null;
        UpdateNotice = result.Message;
        RefreshUpdateOffer();
        // El camino manual de siempre sigue estando: el banner conserva «Ver novedades».
        if (result.ReleaseUrl is { Length: > 0 })
        {
            UpdateUrl = result.ReleaseUrl;
            OnPropertyChanged(nameof(CanOpenUpdate));
        }
    }

    /// <summary>
    /// Cuenta cómo acabó la actualización anterior, si la hubo. Se llama una vez al arrancar: es
    /// la versión NUEVA quien confirma que arrancó bien, y por eso también es quien borra la copia
    /// de la anterior.
    /// </summary>
    public void ReportUpdateAftermath()
    {
        if (_selfUpdate?.TakeAftermath() is not { } aftermath)
        {
            return;
        }

        OnUiThread(() => _toasts.Show(aftermath.Message));
    }

    /// <summary>
    /// Cuenta una vez que un ajuste de fábrica ha cambiado por debajo (F16 §D). Se enseña como
    /// toast y no como banner: es una noticia, no algo que atender — el valor nuevo ya está puesto
    /// y cambiarlo está a dos clics.
    /// </summary>
    public void ReportSettingsPromotion(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        OnUiThread(() => _toasts.Show(notice!));
    }

    /// <summary>
    /// Pregunta si hay versión nueva, sin bloquear nada y sin poder romper el arranque.
    /// <para>
    /// Va DESPUÉS de que la aplicación esté en marcha y en su propia tarea: llega cuando llegue.
    /// Nada de lo que hace la aplicación depende de esta respuesta, así que nada puede esperarla —
    /// una comprobación de cortesía que retrasa el arranque ya ha dejado de ser cortés.
    /// </para>
    /// </summary>
    public async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (_updates is null)
        {
            return;
        }

        UpdateAvailability result = await _updates.CheckAsync(ct);
        OnUiThread(() =>
        {
            _offeredUpdate = result.Version;
            _offeredTag = result.Tag ?? string.Empty;
            UpdateAvailable = result.HasUpdate;
            UpdateUrl = result.Url ?? string.Empty;
            // El texto lo redacta el RESULTADO del chequeo (BUGFIX-AVISO). Aquí no se calcula ni
            // se formatea ninguna versión: hacerlo era lo que permitía que el aviso dijera un
            // número que la decisión nunca había usado.
            UpdateLabel = result.Headline;
            OnPropertyChanged(nameof(CanOpenUpdate));
            RefreshUpdateOffer();
        });
    }

    /// <summary>
    /// El re-chequeo de las instancias que <b>llevan abiertas sin reiniciarse</b>. Lo llama el
    /// temporizador de la ventana, que solo sabe que el tiempo pasa: si toca o no —24 h desde el
    /// último intento— lo decide el propio chequeo, donde vive el resto de la política de
    /// frecuencia. Aquí no se llama a ninguna API por preguntarlo.
    /// </summary>
    public async Task RecheckForUpdatesIfDueAsync(CancellationToken ct = default)
    {
        if (_updates is null || !_updates.PeriodicRecheckDue())
        {
            return;
        }

        await CheckForUpdatesAsync(ct);
    }

    /// <summary>
    /// Abre la página de la Release en el navegador. Ahí acaba el trabajo de Atalaya: descargar y
    /// reemplazar es del usuario, y el banner se retira porque ya ha hecho lo suyo.
    /// </summary>
    [RelayCommand]
    private void OpenUpdate()
    {
        string url = UpdateUrl;
        if (url.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Sin navegador que abrir no hay nada que decir: el banner sigue ahí con su enlace.
            return;
        }

        UpdateAvailable = false;
    }

    /// <summary>
    /// Descarta el aviso de ESTA versión. No vuelve con la misma; sí con la siguiente, que es lo
    /// que separa «ya me he enterado» de «no me avises nunca más».
    /// </summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        _updates?.Dismiss(_offeredUpdate);
        UpdateAvailable = false;
    }

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
    private Task ShowFindings() => Navigation.NavigateOrResumeAsync<FindingsViewModel>();

    [RelayCommand]
    private Task ShowMetrics() => Navigation.NavigateToAsync<MetricsViewModel>();

    /// <summary>V7 Informes (F6.3): la lista de todo lo que las auditorías han dejado escrito.</summary>
    [RelayCommand]
    private Task ShowReports() => Navigation.NavigateToAsync<ReportsViewModel>();

    [RelayCommand]
    private Task ShowSettings() => Navigation.NavigateToAsync<SettingsViewModel>();

    /// <summary>
    /// «Acerca de» (F26 §C). Es una página del grupo Sistema y no un modal de Ajustes: no se edita
    /// nada ahí dentro, así que no era un ajuste — y al fondo de Ajustes → Avanzado no lo
    /// encontraba nadie.
    /// </summary>
    [RelayCommand]
    private Task ShowAbout() => Navigation.NavigateToAsync<AboutViewModel>();

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
