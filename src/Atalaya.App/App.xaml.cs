using System.Net.Http;
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
        // One-time, silent migration of pre-F2 connection settings (D4): existing users keep working.
        settings.MigrateConnection(_host.Services.GetRequiredService<DeployConfig>());
        ThemeService.Apply(settings.Current.Theme);

        // F3.1 Bloque 1: comando oculto para re-vincular pares "resuelto ↔ duplicado nuevo"
        // detectados por la 2ª pasada en una sesión concreta. Uso:
        //   Atalaya.exe --repair-session <slug> <sessionUlid>
        // Escribe a stdout el reporte y sale sin abrir la UI.
        if (TryHandleRepairSession(e.Args))
        {
            Shutdown();
            return;
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();

        MainViewModel main = _host.Services.GetRequiredService<MainViewModel>();
        await main.InitializeAsync();
    }

    private bool TryHandleRepairSession(string[] args)
    {
        int i = Array.IndexOf(args, "--repair-session");
        if (i < 0 || i + 2 >= args.Length)
        {
            return false;
        }

        string slug = args[i + 1];
        string sessionUlid = args[i + 2];
        SessionRepairTool tool = _host!.Services.GetRequiredService<SessionRepairTool>();
        SessionRepairReport report = tool.Repair(slug, sessionUlid, DateTimeOffset.UtcNow, Environment.UserName);

        string logPath = Path.Combine(new AppPaths().Logs, $"repair-{sessionUlid}.log");
        using var sw = new StreamWriter(logPath, append: false);
        sw.WriteLine($"repair-session {sessionUlid} · app={slug} · {DateTimeOffset.UtcNow:o}");
        sw.WriteLine($"pairs reparados: {report.Pairs.Count}");
        foreach (SessionRepairPair p in report.Pairs)
        {
            sw.WriteLine($"  · reabierto {p.ReopenedFindingUlid} · duplicado borrado {p.DeletedDuplicateUlid} · score={p.MatchScore:0.00}");
        }

        sw.WriteLine($"nuevos sin par (skipped): {report.Skipped.Count}");
        foreach (string s in report.Skipped)
        {
            sw.WriteLine($"  · {s}");
        }

        MessageBox.Show(
            $"repair-session completado.\n\nPares reparados: {report.Pairs.Count}\nSaltados: {report.Skipped.Count}\n\nDetalle: {logPath}",
            "Atalaya · repair-session", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private bool TryHandleConsolidate(string[] args)
    {
        int i = Array.IndexOf(args, "--consolidate");
        if (i < 0 || i + 1 >= args.Length)
        {
            return false;
        }

        string slug = args[i + 1];
        bool apply = Array.IndexOf(args, "--apply") >= 0;
        SessionRepairTool tool = _host!.Services.GetRequiredService<SessionRepairTool>();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        var appPaths = new AppPaths();
        string stamp = nowUtc.ToString("yyyyMMdd");
        string markerPath = Path.Combine(appPaths.Logs, $"consolidate-dryrun-{slug}-{stamp}.marker");

        ConsolidationPlan plan = tool.PlanConsolidation(slug, nowUtc);

        if (!apply)
        {
            string logPath = Path.Combine(appPaths.Logs, $"consolidate-{slug}-{stamp}.log");
            using (var sw = new StreamWriter(logPath, append: false))
            {
                sw.WriteLine($"consolidate DRY-RUN · app={slug} · {nowUtc:o}");
                sw.WriteLine($"clusters: {plan.Clusters.Count} · purges: {plan.Purges.Count}");
                foreach (ConsolidationCluster c in plan.Clusters)
                {
                    sw.WriteLine($"  cluster canónico={c.CanonicalUlid} · «{c.CanonicalTitle}» · fp={c.CanonicalFingerprint}");
                    for (int k = 0; k < c.MergeUlids.Count; k++)
                    {
                        sw.WriteLine($"    ← absorbe {c.MergeUlids[k]} · fp={c.MergeFingerprints[k]}");
                    }
                }

                foreach (ConsolidationPurge p in plan.Purges)
                {
                    sw.WriteLine($"  purge {p.Ulid} · «{p.Title}» · {p.Reason}");
                }
            }

            File.WriteAllText(markerPath, nowUtc.ToString("o"));
            MessageBox.Show(
                $"consolidate DRY-RUN completado.\n\nClusters: {plan.Clusters.Count}\nPurges: {plan.Purges.Count}\n\nDetalle: {logPath}\nMarca: {markerPath}\n\nRelanza con --apply el mismo día para ejecutar.",
                "Atalaya · consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        if (!File.Exists(markerPath))
        {
            MessageBox.Show(
                $"consolidate --apply requiere un dry-run previo del mismo día (D-074).\nMarca esperada: {markerPath}\n\nRelanza sin --apply primero.",
                "Atalaya · consolidate", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }

        ConsolidationResult result = tool.Apply(plan, nowUtc, Environment.UserName);
        string applyLog = Path.Combine(appPaths.Logs, $"consolidate-apply-{slug}-{stamp}.log");
        using (var sw = new StreamWriter(applyLog, append: false))
        {
            sw.WriteLine($"consolidate APPLY · app={slug} · {nowUtc:o}");
            sw.WriteLine($"clusters={result.ClustersConsolidated} · absorbed={result.FindingsAbsorbed} · purged={result.FindingsPurged}");
        }

        MessageBox.Show(
            $"consolidate APPLY completado.\n\nClusters: {result.ClustersConsolidated}\nAbsorbidos: {result.FindingsAbsorbed}\nPurgados: {result.FindingsPurged}\n\nDetalle: {applyLog}",
            "Atalaya · consolidate", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
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
        services.AddSingleton<SessionRepairTool>();
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
            AppSettings s = sp.GetRequiredService<SettingsService>().Current;
            var account = sp.GetRequiredService<GitHubAccountService>();
            return new RealCopilotAgent(
                s.CopilotBaseDirectory,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Copilot"),
                sendTimeout: TimeSpan.FromMinutes(Math.Max(1, s.CopilotTimeoutMinutes)),
                tokenProvider: () => account.Token,
                loginProvider: () => account.Current?.Login);
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
        services.AddTransient<AccountViewModel>();
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
