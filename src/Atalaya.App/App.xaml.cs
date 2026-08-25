using System.Net.Http;
using System.Windows;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
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

        var paths = new AppPaths();
        Directory.CreateDirectory(paths.Logs);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.Logs, "atalaya-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(b =>
            {
                b.ClearProviders();
                b.AddSerilog(Log.Logger, dispose: true);
            })
            .ConfigureServices(services => ConfigureServices(services, paths))
            .Build();

        await _host.StartAsync();

        // Load settings and apply theme before showing any window.
        SettingsService settings = _host.Services.GetRequiredService<SettingsService>();
        settings.Load();
        // One-time, silent migration of pre-F2 connection settings (D4): existing users keep working.
        settings.MigrateConnection(_host.Services.GetRequiredService<DeployConfig>());
        ThemeService.Apply(settings.Current.Theme);

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();

        MainViewModel main = _host.Services.GetRequiredService<MainViewModel>();
        await main.InitializeAsync();
    }

    private static void ConfigureServices(IServiceCollection services, AppPaths paths)
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

        services.AddSingleton<HubContext>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton(sp => new MachineConfigStore(paths.MachinesJson));
        services.AddSingleton<InventoryScanner>();
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton(sp => new PortfolioQuery(sp.GetRequiredService<HubContext>().Store));
        services.AddSingleton(sp => new MetricsQuery(sp.GetRequiredService<HubContext>()));
        services.AddSingleton<ImportService>();

        // Copilot: the real SDK agent, authenticated with the account token (D3) and always
        // running the CLI bundled with the SDK package — no npm install, no `copilot /login`.
        // The token is read lazily on every start, so connecting or switching account takes
        // effect immediately. With no account token the adapter falls back to the pre-F2
        // behaviour (UseLoggedInUser = true) so existing machines keep working.
        services.AddSingleton<ICopilotAgent>(sp =>
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
                sendTimeout: TimeSpan.FromMinutes(Math.Max(1, s.CopilotTimeoutMinutes)),
                tokenProvider: () => account.Token,
                loginProvider: () => account.Current?.Login);
        });
        services.AddTransient<SessionCoordinator>();
        services.AddTransient<VerifyCoordinator>();

        // F5.2: el estado de la sesión es un SINGLETON que sobrevive a la navegación; V5 es solo
        // una vista sobre él. El coordinador sigue siendo transient (uno por sesión), así que se
        // inyecta como fábrica.
        services.AddSingleton<OpenSessionStore>();
        services.AddSingleton(sp => new InterruptedSessionRecovery(
            sp.GetRequiredService<HubContext>(), sp.GetRequiredService<OpenSessionStore>()));
        services.AddSingleton(sp => new LiveSessionService(
            sp.GetRequiredService<SessionCoordinator>,
            sp.GetRequiredService<ICopilotAgent>(),
            sp.GetRequiredService<OpenSessionStore>(),
            sp.GetRequiredService<HubContext>()));
        services.AddSingleton<GovernanceService>();

        // F5.3 §4: el hard-reset de una app. El "quién pregunta" se inyecta para que el
        // view-model no dependa de una ventana y los tests puedan ejercitar el flujo entero.
        services.AddSingleton<AppDeletionService>();
        services.AddSingleton<IDeleteAppConfirmer, DeleteAppDialogConfirmer>();

        services.AddSingleton<EditorLauncher>();
        services.AddSingleton<CycleService>();
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
        services.AddTransient<FindingsViewModel>();
        services.AddTransient<FindingDetailViewModel>();
        services.AddTransient<MetricsViewModel>();
        services.AddTransient<ImportViewModel>();
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
