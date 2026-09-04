using System.Net.Http;
using System.Text;
using System.Windows;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Inventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Atalaya.App;

public partial class App : Application
{
    private IHost? _host;

    /// <summary>Exposes the DI container to views that need to resolve dependencies.</summary>
    public static IServiceProvider Services =>
        ((App)Current)._host?.Services ?? throw new InvalidOperationException("Host not started.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // F8.1 — LO PRIMERO DE TODO. Atalaya escribe sus números y sus fechas en es-ES, no en la
        // cultura de la máquina: la aplicación es monolingüe en español y sus informes se comparten
        // entre personas (ver AppCulture). Va antes del log y antes de cualquier ventana porque a
        // partir de aquí cualquier hilo que nazca la hereda.
        AppCulture.Apply();

        var paths = new AppPaths();
        Directory.CreateDirectory(paths.Logs);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.Logs, "atalaya-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        // `--selfcheck`: el arranque entero sin abrir nada, y 0 o 1 (BUGFIX-ARRANQUE). Lo corre el
        // workflow de release sobre el paquete recién comprimido; un binario que no arranca no
        // puede volver a publicarse.
        if (StartupSelfCheck.IsRequested(e.Args))
        {
            Shutdown(await RunSelfCheckAsync(e.Args, paths));
            return;
        }

        // Un fallo aquí mataba el proceso EN SILENCIO: `OnStartup` es `async void`, así que la
        // excepción no la recogía nadie, no llegaba al log —que se escribe desde el contenedor que
        // no llegó a existir— y quien lo sufría veía Atalaya no abrirse y ya está. Es lo que pasó
        // con la 1.1.3. Ahora se apunta y se dice, que es lo mínimo que se le debe a alguien cuya
        // aplicación no arranca.
        try
        {
            await StartAsync(paths);
        }
        catch (Exception ex)
        {
            FailToStart(ex);
        }
    }

    /// <summary>El arranque de verdad. Lo que antes era el cuerpo de <c>OnStartup</c>.</summary>
    private async Task StartAsync(AppPaths paths)
    {
        _host = BuildHost(paths);

        await _host.StartAsync();

        // Load settings and apply theme before showing any window.
        SettingsService settings = _host.Services.GetRequiredService<SettingsService>();
        settings.Load();
        // One-time, silent migration of pre-F2 connection settings (D4): existing users keep working.
        settings.MigrateConnection(_host.Services.GetRequiredService<DeployConfig>());
        // Promoción única del interruptor del arreglo asistido (D-563): las máquinas anteriores a
        // F6.9 traen un `false` escrito, no una clave ausente, y el nuevo valor por defecto no las
        // alcanza. Sin esto el botón «Arreglar con agente» no aparece en ninguna de ellas.
        settings.MigrateAssistedFixDefault();
        // F16 §D: y el tope del barrido, por lo mismo — las máquinas traen el 5 escrito y el valor
        // por defecto nuevo no las alcanza. La frase que devuelve se enseña una vez, abajo.
        string? sweepNotice = settings.MigrateSweepCapDefault();
        ThemeService.Apply(settings.Current.Theme);

        MainViewModel main = _host.Services.GetRequiredService<MainViewModel>();

        // F26 §A (D-963): cómo dejaste el raíl, ANTES de enseñar la ventana. El primer
        // `SizeChanged` llega al mostrarla, y es el que decide el plegado automático por ancho; si
        // la preferencia se leyera después, o la pisaría el ancho, o ella pisaría al ancho — que
        // es lo que pasaba: el raíl se quedaba desplegado en una ventana estrecha.
        main.RestoreRail();

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();

        await main.InitializeAsync();

        // F11: ¿venimos de una actualización? Lo cuenta la versión NUEVA, ya arrancada — que es
        // justo la prueba que faltaba para poder borrar la copia de la anterior.
        main.ReportUpdateAftermath();
        main.ReportSettingsPromotion(sweepNotice);

        // F8 §3: el chequeo de versión va DESPUÉS de que todo esté en marcha y sin await. Nada de
        // lo que hace la aplicación depende de su respuesta, así que nada puede esperarla: una
        // comprobación de cortesía que retrasa el arranque ya ha dejado de ser cortés.
        _ = main.CheckForUpdatesAsync();
    }

    /// <summary>
    /// El contenedor, montado en UN solo sitio. Lo usan el arranque de verdad y el autochequeo:
    /// dos formas de montarlo serían dos grafos que pueden divergir, y entonces el chequeo dejaría
    /// de decir nada sobre lo que arranca.
    /// </summary>
    internal static IHost BuildHost(AppPaths paths)
        => Host.CreateDefaultBuilder()
            .ConfigureLogging(b =>
            {
                b.ClearProviders();
                b.AddSerilog(Log.Logger, dispose: false);
            })
            .ConfigureServices(services => ConfigureServices(services, paths))
            .Build();

    /// <summary>
    /// Corre el autochequeo y deja el parte donde se pueda leer: por la consola de quien lo lanzó
    /// —una aplicación de ventana no tiene consola propia, así que hay que engancharse a la del
    /// padre— y, si se pidió, en un fichero.
    /// </summary>
    private static async Task<int> RunSelfCheckAsync(string[] args, AppPaths paths)
    {
        SelfCheckReport report;
        try
        {
            report = await StartupSelfCheck.RunAsync(paths, shell: true);
        }
        catch (Exception ex)
        {
            report = new SelfCheckReport(new[]
            {
                new SelfCheckStep("autochequeo", false, $"{ex.GetType().Name}: {ex.Message}"),
            });
        }

        NativeConsole.Write(report.Text);
        Log.Information("Autochequeo de arranque: {Result}{NewLine}{Report}",
            report.Ok ? "arranca" : "NO ARRANCA", Environment.NewLine, report.Text);

        if (StartupSelfCheck.ReportPath(args) is { Length: > 0 } path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                // Con BOM: quien lo va a leer es PowerShell 5.1 del workflow, que sin él
                // interpreta el fichero en la página de códigos de la máquina y convierte
                // cada acento del parte en un jeroglífico.
                await File.WriteAllTextAsync(path, report.Text, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                NativeConsole.Write($"(no se pudo escribir el parte en {path}: {ex.Message})");
            }
        }

        Log.CloseAndFlush();
        return report.ExitCode;
    }

    /// <summary>
    /// No arrancó. Se deja escrito en el log y se dice en pantalla — con la causa, no con un
    /// «error inesperado»: quien lo lea es quien va a tener que contarlo.
    /// </summary>
    private void FailToStart(Exception ex)
    {
        try
        {
            Log.Fatal(ex, "Atalaya no pudo arrancar.");
            Log.CloseAndFlush();
        }
        catch (Exception)
        {
            // Si ni el log va, queda el mensaje.
        }

        try
        {
            MessageBox.Show(
                $"Atalaya no ha podido arrancar.\n\n{ex.GetType().Name}: {ex.Message}\n\n"
                + "El detalle está en %LOCALAPPDATA%\\Atalaya\\logs.",
                "Atalaya",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Sin ventana tampoco se puede hacer más.
        }

        Shutdown(1);
    }

    internal static void ConfigureServices(IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IUlidFactory>(sp => new UlidFactory(sp.GetRequiredService<IClock>()));
        services.AddSingleton<SettingsService>();

        // Connection (F2): deployment config → device flow → one account token → three consumers.
        services.AddSingleton(DeployConfig.Load());
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton(sp => new GitHubDeviceFlow(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new GitHubApiClient(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<AccountStore>();
        services.AddSingleton<GitHubAccountService>();
        services.AddSingleton<ConnectionChecker>();
        // R3: los repositorios de la organización, con el token de la cuenta y cacheados en la
        // sesión. Singleton por la caché: uno por navegación volvería a pedir la lista cada vez.
        services.AddSingleton<RepositoryCatalog>();
        // F8 §3: el aviso de versión nueva, con el MISMO token de cuenta. Cero credenciales nuevas.
        services.AddSingleton(sp => new UpdateCheckService(
            sp.GetRequiredService<DeployConfig>(),
            sp.GetRequiredService<GitHubAccountService>(),
            sp.GetRequiredService<GitHubApiClient>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ILogger<UpdateCheckService>>()));
        // F11: y actualizarse de verdad, con el MISMO token y contra la MISMA Release. El registro
        // de intentos es una pieza suya porque una actualización cruza dos procesos y dos
        // versiones: ninguna de las dos puede ser la dueña del registro.
        services.AddSingleton<UpdateJournal>();
        services.AddSingleton(sp => new SelfUpdateService(
            paths,
            sp.GetRequiredService<DeployConfig>(),
            sp.GetRequiredService<GitHubAccountService>(),
            sp.GetRequiredService<GitHubApiClient>(),
            sp.GetRequiredService<AgentBusyGate>(),
            sp.GetRequiredService<FixSnapshotStore>(),
            sp.GetRequiredService<UpdateJournal>(),
            sp.GetRequiredService<ILogger<SelfUpdateService>>()));

        services.AddSingleton<HubContext>();
        services.AddSingleton<NavigationService>();
        // F26 §A — en qué aplicación estás. Singleton porque es memoria compartida de la ventana:
        // si cada vista tuviera la suya, el raíl volvería a no saber a qué inventario llevar.
        services.AddSingleton<ActiveApp>();
        services.AddSingleton(sp => new MachineConfigStore(paths.MachinesJson));
        services.AddSingleton<InventoryScanner>();
        // F7: el escaneo de directivas es un recorrido distinto del árbol, con su propio catálogo.
        services.AddSingleton<DirectiveScanner>();
        services.AddSingleton<DirectiveService>();
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton(sp => new PortfolioQuery(sp.GetRequiredService<HubContext>().Store));
        services.AddSingleton(sp => new MetricsQuery(sp.GetRequiredService<HubContext>()));
        services.AddSingleton(sp => new ReportsQuery(sp.GetRequiredService<HubContext>()));
        services.AddSingleton(sp => new DriftQuery(sp.GetRequiredService<HubContext>()));
        services.AddSingleton<ImportService>();

        // Copilot: the real SDK agent, authenticated with the account token (D3) and always
        // running the CLI bundled with the SDK package — no npm install, no `copilot /login`.
        // The token is read lazily on every start, so connecting or switching account takes
        // effect immediately. With no account token the adapter falls back to the pre-F2
        // behaviour (UseLoggedInUser = true) so existing machines keep working.
        services.AddSingleton<IAssistedFixProvider>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            AppSettings s = settings.Current;
            var account = sp.GetRequiredService<GitHubAccountService>();
            return new RealCopilotAgent(
                s.CopilotBaseDirectory,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Copilot"),
                // F5.1: leído en cada sesión, no capturado aquí — cambiar el modelo en Ajustes
                // surte efecto en la siguiente auditoría sin reiniciar la app.
                modelProvider: () => settings.Current.CopilotModel,
                // F5.1 otra vez: leído en cada envío, no capturado aquí (BUGFIX-AJUSTES).
                sendTimeout: () => TimeSpan.FromMinutes(
                    Math.Max(SettingsLimits.MinCopilotTimeoutMinutes, settings.Current.CopilotTimeoutMinutes)),
                tokenProvider: () => account.Token,
                loginProvider: () => account.Current?.Login);
        });

        // F14 — el SEGUNDO proveedor: Claude Code, por el CLI que el usuario ya tiene.
        //
        // Atalaya no lo instala ni guarda credenciales de Anthropic: lo busca en el PATH y usa la
        // sesión que el CLI tenga iniciada, igual que con Copilot usa el login de GitHub. El
        // puente MCP viaja en la carpeta de la aplicación (ver el .csproj) y es lo que `claude`
        // lanza como servidor de herramientas.
        services.AddSingleton<ClaudeCodeProvider>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new ClaudeCodeProvider(
                bridgeExecutable: McpBridge.ResolvePath(),
                // Leído en CADA sesión, por la misma razón que el de Copilot (BUGFIX-AJUSTES).
                modelProvider: () => settings.Current.ClaudeCodeModel,
                workDirectory: () => Path.Combine(paths.Root, "claude"),
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger("ClaudeCode"));
        });

        // Los dos proveedores, EN ORDEN y nombrados uno a uno. Se listan aquí en vez de dejar que
        // el contenedor los recolecte por su interfaz porque ese orden es el que ve el usuario —el
        // primero es el de fábrica, y el que la pantalla Cuenta enseña arriba— y no puede depender
        // de en qué línea quedó registrado cada uno.
        services.AddSingleton(sp => new AuditorProviderRegistry(
            sp.GetRequiredService<SettingsService>(),
            new IAuditorProvider[]
            {
                sp.GetRequiredService<IAssistedFixProvider>(),
                sp.GetRequiredService<ClaudeCodeProvider>(),
            }));

        // Quien recibe UN proveedor recibe el elegido AHORA. Solo vale para servicios transitorios
        // —los coordinadores, que se crean uno por sesión—: un singleton que lo capturase se
        // quedaría con el proveedor que hubiera al arrancar, que es el fallo de BUGFIX-AJUSTES.
        // Los singletons reciben el registro y preguntan cuando toca.
        services.AddTransient(sp => sp.GetRequiredService<AuditorProviderRegistry>().Current);
        services.AddTransient<SessionCoordinator>();
        services.AddTransient<VerifyCoordinator>();

        // F5.2: el estado de la sesión es un SINGLETON que sobrevive a la navegación; V5 es solo
        // una vista sobre él. El coordinador sigue siendo transient (uno por sesión), así que se
        // inyecta como fábrica.
        services.AddSingleton<OpenSessionStore>();
        services.AddSingleton(sp => new InterruptedSessionRecovery(
            sp.GetRequiredService<HubContext>(), sp.GetRequiredService<OpenSessionStore>()));
        services.AddSingleton<ModelResolver>();
        services.AddSingleton(sp => new LiveSessionService(
            sp.GetRequiredService<SessionCoordinator>,
            () => sp.GetRequiredService<AuditorProviderRegistry>().Current,
            sp.GetRequiredService<OpenSessionStore>(),
            sp.GetRequiredService<HubContext>(),
            sp.GetRequiredService<ModelResolver>(),
            sp.GetRequiredService<AgentBusyGate>()));
        services.AddSingleton<GovernanceService>();

        // F5.10 · silencio con alcance: la pregunta de qué hacer con los hallazgos existentes y la
        // gestión de reglas excluidas. Ambas se inyectan para que ni la ficha ni el inventario
        // dependan de que haya una ventana.
        services.AddSingleton<IPatternSilencesDialog, PatternSilencesDialogHost>();
        // F7: la gestión de directivas del proyecto, inyectada por lo mismo que la de patrones.
        services.AddSingleton<IDirectivesDialog, DirectivesDialogHost>();
        // F9 §4: quién abre la lista de hallazgos sin código.
        services.AddSingleton<IDeletedUnitsDialog, DeletedUnitsDialogHost>();
        // F13: la política de tamaño de cada aplicación, y quién la abre.
        services.AddSingleton<ThresholdPolicyService>();

        // F15 — las tarifas por modelo: configuración de la ORGANIZACIÓN, en el hub, y editable
        // desde Métricas, que es donde se ve su consecuencia (mismo argumento que D-770).
        services.AddSingleton<ModelRatesService>();
        services.AddSingleton<IModelRatesDialog, ModelRatesDialogHost>();
        services.AddSingleton<IThresholdsDialog, ThresholdsDialogHost>();
        // F17 §4: configurar el ciclo —su lupa y su juez preferido— y quién lo pregunta. El
        // servicio escribe en el hub (política compartida, D-769); el flujo monta el diálogo con
        // la lista de modelos del proveedor de quien configura, y se inyecta como los demás.
        services.AddSingleton(sp => new CycleConfigService(
            sp.GetRequiredService<HubContext>(), sp.GetRequiredService<DriftQuery>()));
        services.AddSingleton<ICycleConfigDialog, CycleConfigDialogHost>();
        services.AddSingleton(sp => new CycleConfigFlow(
            sp.GetRequiredService<ICycleConfigDialog>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<AuditorProviderRegistry>()));
        // F5.6 §3 (D-228): el reparto de alias legibles, que nunca se había cableado.
        services.AddSingleton<DisplayIdService>();
        // F5.6 §2 (D-226): el re-anclaje que se persiste al abrir la ficha.
        services.AddSingleton<AnchorRepair>();
        // F6.8: quién usa el código de un hallazgo. Alimenta el prompt de arreglo, y el arreglo
        // integrado (H9) heredará el mismo servicio en vez de recolectar por su cuenta.
        services.AddSingleton<ReferenceCollector>();

        // F6.9 · Arreglo asistido. El cerrojo compartido va primero: auditar y arreglar usan el
        // mismo runtime, el mismo asiento y el mismo clon, así que solo puede correr uno.
        services.AddSingleton<AgentBusyGate>();
        services.AddSingleton<FixSnapshotStore>();
        services.AddSingleton(sp => new AssistedFixLauncher(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<CloneLinkService>(),
            sp.GetRequiredService<MachineConfigStore>(),
            sp.GetRequiredService<AgentBusyGate>(),
            sp.GetRequiredService<AuditorProviderRegistry>()));
        services.AddSingleton(sp => new BuildRunner(
            timeout: () => TimeSpan.FromMinutes(Math.Max(
                SettingsLimits.MinCopilotTimeoutMinutes,
                sp.GetRequiredService<SettingsService>().Current.CopilotTimeoutMinutes))));
        services.AddSingleton<IFixDiscardConfirmer, FixDiscardDialogConfirmer>();

        // BUGFIX-CIERRE: cerrar la pantalla y descartar los cambios son preguntas distintas, con
        // respuestas distintas. Cada una tiene su confirmador.
        services.AddSingleton<IFixCloseConfirmer, FixCloseDialogConfirmer>();
        services.AddSingleton(sp => new LiveFixService(
            sp.GetRequiredService<HubContext>(),
            // F16: se arregla con el proveedor ELEGIDO, preguntado en cada sesión — igual que se
            // audita con él (D-776). El tipo sigue exigiendo que sepa arreglar, y eso es lo que
            // impide que un proveedor futuro que no lo haga se cuele por accidente: cuando el
            // activo no arregla, esto devuelve null y la sesión no arranca diciendo por qué.
            () => sp.GetRequiredService<AuditorProviderRegistry>().CurrentFixer,
            sp.GetRequiredService<MachineConfigStore>(),
            sp.GetRequiredService<IUlidFactory>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ReferenceCollector>(),
            sp.GetRequiredService<FixSnapshotStore>(),
            sp.GetRequiredService<AssistedFixLauncher>(),
            sp.GetRequiredService<AgentBusyGate>(),
            sp.GetRequiredService<BuildRunner>(),
            sp.GetRequiredService<ModelResolver>(),
            sp.GetRequiredService<DirectiveService>()));

        // F5.3 §4: el hard-reset de una app. El "quién pregunta" se inyecta para que el
        // view-model no dependa de una ventana y los tests puedan ejercitar el flujo entero.
        services.AddSingleton<AppDeletionService>();
        services.AddSingleton<IDeleteAppConfirmer, DeleteAppDialogConfirmer>();

        // F5.6 §4: el diálogo previo a gastar. Mismo patrón que el borrado de app — quién
        // pregunta se inyecta, así que el flujo entero (incluido cancelar) se prueba sin ventana.
        services.AddSingleton<CostEstimator>();
        services.AddSingleton<IAuditLaunchConfirmer, AuditLaunchDialogConfirmer>();

        // F5.7 §5: el reset de fábrica. Mismo patrón que el borrado de app — el servicio hace la
        // operación ATÓMICA (hub primero, local después, y cualquier fallo aborta entero) y quién
        // pregunta se inyecta, así que el flujo se prueba sin abrir una ventana.
        services.AddSingleton<FactoryResetService>();
        services.AddSingleton<IFactoryResetConfirmer, FactoryResetDialogConfirmer>();

        // F5.8: el estado de vinculación local (el piloto de las tarjetas y el modo solo-lectura
        // del inventario) y el diálogo que lo apaga. El selector de carpetas y el diálogo se
        // inyectan tras un seam, así que el flujo entero se prueba sin abrir una ventana.
        services.AddSingleton<CloneLinkService>();
        services.AddSingleton<MeasuredFindingService>();
        services.AddSingleton<InventoryRescanService>();
        services.AddSingleton<IFolderPicker, SystemFolderPicker>();
        services.AddSingleton<ILinkCloneDialog, LinkCloneDialogHost>();
        services.AddSingleton<LinkCloneFlow>();

        services.AddSingleton<EditorLauncher>();
        // F5.9: abrir el informe de una sesion desde el registro de operaciones.
        services.AddSingleton<IFileOpener, ShellFileOpener>();
        services.AddSingleton<IFileSaver, SystemFileSaver>();
        services.AddSingleton<IAboutDialog, AboutDialogHost>();
        // La memoria de plegado es de la SESIÓN, no de la vista: V2 y V3 son transitorias y la
        // comparten (F5.6 §1).
        services.AddSingleton<GroupExpansionMemory>();
        // El cierre siembra el ciclo siguiente con la deriva del que termina (F9.2 §1), así que
        // necesita el historial del clon de ESTA máquina. Se inyecta explícito: con el constructor
        // opcional, una resolución que se quedara corta cerraría ciclos sembrando todo pendiente y
        // nadie se enteraría.
        services.AddSingleton(sp => new CycleService(
            sp.GetRequiredService<HubContext>(),
            sp.GetRequiredService<IUlidFactory>(),
            sp.GetRequiredService<DriftQuery>(),
            sp.GetRequiredService<MachineConfigStore>()));
        services.AddSingleton<StatusExporter>();

        // Shell
        // F5.3: los avisos son efímeros y hay uno solo de cierre de sesión. Singleton porque la
        // cola de avisos es de la ventana, no de una página.
        services.AddSingleton(sp => new ToastCenter());
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        // Pages (fresh instance per navigation)
        services.AddTransient<PortfolioViewModel>();
        services.AddTransient<InventoryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AccountViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<SessionViewModel>();
        // F26 §B: Hallazgos pide fichas para su vista rápida. Se le da la FÁBRICA y no una
        // instancia: cada hallazgo que se señala necesita una recién cargada.
        services.AddTransient<FindingsViewModel>(sp => new FindingsViewModel(
            sp.GetRequiredService<HubContext>(),
            sp.GetRequiredService<NavigationService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<GroupExpansionMemory>(),
            sp.GetRequiredService<FindingDetailViewModel>));
        services.AddTransient<FindingDetailViewModel>();
        services.AddTransient<MetricsViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<AssistedFixViewModel>();
    }

    /// <summary>
    /// Cierre determinista. Debe ser SÍNCRONO y tolerar fallos.
    /// <para>
    /// Antes era <c>async void</c>: WPF no espera a ese método, así que en cuanto se alcanzaba el
    /// primer <c>await</c> el hilo principal seguía con el apagado y la continuación podía no
    /// ejecutarse nunca. Resultado: el host no se liberaba y el runtime de Copilot
    /// (<c>copilot.exe</c>, lanzado por stdio) se quedaba vivo con sus tuberías abiertas,
    /// manteniendo el proceso <c>Atalaya.exe</c> en pie tras cerrar la ventana.
    /// </para>
    /// <para>
    /// Y aunque llegara a ejecutarse, <c>_host.Dispose()</c> reventaba: el contenedor guarda
    /// <see cref="RealCopilotAgent"/>, que implementa <c>IAsyncDisposable</c> pero NO
    /// <c>IDisposable</c>, y el camino síncrono de liberación lanza
    /// <c>InvalidOperationException</c> en ese caso. Hay que liberar por la vía asíncrona.
    /// </para>
    /// <para>
    /// Un proceso zombi no era solo ruido: seguía corriendo su temporizador de sondeo y lanzando
    /// auditorías con código antiguo sobre el hub compartido (ver D-085, D-086).
    /// </para>
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        IHost? host = _host;
        _host = null;
        if (host is not null)
        {
            // Acotado: si algo se atasca al parar, preferimos cerrar igual a colgarnos.
            Run(() => host.StopAsync(TimeSpan.FromSeconds(5)), "detener el host");
            // El host genérico implementa IAsyncDisposable; ésa es la vía que sabe liberar
            // servicios que solo son asíncronamente liberables, como el agente de Copilot.
            Run(
                () => host is IAsyncDisposable async
                    ? async.DisposeAsync().AsTask()
                    : Task.Run(host.Dispose),
                "liberar el host");
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Ejecuta una tarea de apagado bloqueando, con tope de tiempo, sin dejar escapar excepciones:
    /// en el camino de cierre nada debe impedir que el proceso termine.
    /// </summary>
    private static void Run(Func<Task> operation, string what)
    {
        try
        {
            if (!operation().Wait(TimeSpan.FromSeconds(10)))
            {
                Log.Warning("Cierre: se agotó el tiempo al {What}", what);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cierre: error al {What}", what);
        }
    }
}
