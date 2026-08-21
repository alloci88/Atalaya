using System.Windows;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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
        services.AddSingleton<HubContext>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton(sp => new MachineConfigStore(paths.MachinesJson));
        services.AddSingleton<InventoryScanner>();
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton(sp => new PortfolioQuery(sp.GetRequiredService<HubContext>().Store));
        services.AddSingleton(sp => new MetricsQuery(sp.GetRequiredService<HubContext>()));
        services.AddSingleton<ImportService>();

        // Copilot: the real SDK agent by default (falls back to a help screen when no seat).
        // BaseDirectory is left to the SDK default so UseLoggedInUser finds the `copilot` CLI login;
        // an explicit override can be set in Settings if ever needed.
        services.AddSingleton<ICopilotAgent>(sp =>
        {
            AppSettings s = sp.GetRequiredService<SettingsService>().Current;
            return new RealCopilotAgent(
                s.CopilotBaseDirectory,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Copilot"),
                sendTimeout: TimeSpan.FromMinutes(Math.Max(1, s.CopilotTimeoutMinutes)));
        });
        services.AddTransient<SessionCoordinator>();
        services.AddTransient<VerifyCoordinator>();
        services.AddSingleton<GovernanceService>();
        services.AddSingleton<EditorLauncher>();
        services.AddSingleton<CycleService>();
        services.AddSingleton<StatusExporter>();

        // Shell
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        // Pages (fresh instance per navigation)
        services.AddTransient<PortfolioViewModel>();
        services.AddTransient<InventoryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<SessionViewModel>();
        services.AddTransient<FindingsViewModel>();
        services.AddTransient<FindingDetailViewModel>();
        services.AddTransient<MetricsViewModel>();
        services.AddTransient<ImportViewModel>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
