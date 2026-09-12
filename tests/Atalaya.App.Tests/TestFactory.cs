using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Copilot;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Inventory;
using Atalaya.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.App.Tests;

/// <summary>
/// Builds the app services the coordinator/governance tests need. Uses an EMPTY
/// <see cref="DeployConfig"/> so the hub stays unconfigured and no test ever touches the network.
/// </summary>
internal static class TestFactory
{
    public static GitHubAccountService Account(AppPaths paths, IClock? clock = null)
        => new(new AccountStore(paths), clock ?? SystemClock.Instance);

    /// <summary>
    /// El Inventario montado con lo mínimo (F13). Existe para los tests que necesitan el panel de
    /// gobernanza —la política de tamaño y la oferta de mudanza— sin levantar media aplicación.
    /// </summary>
    public static ViewModels.InventoryViewModel Inventory(
        HubContext hub,
        AppPaths paths,
        MachineConfigStore machines,
        IUlidFactory ulids,
        SettingsService settings,
        ToastCenter toasts)
    {
        var ingestion = new FindingIngestionService(hub, ulids);
        var agent = new FakeCopilotAgent();
        var openSession = new OpenSessionStore(paths);
        var live = new LiveSessionService(
            () => new SessionCoordinator(
                hub, ingestion, new ReconciliationService(hub), machines, ulids, agent, settings),
            agent, openSession, hub);
        var governance = new GovernanceService(hub, ulids);
        var directives = new DirectiveService(hub, new Atalaya.Inventory.DirectiveScanner(), ulids);

        return new ViewModels.InventoryViewModel(
            hub, ulids, new NavigationService(new EmptyServiceProvider()), live, settings,
            new CostEstimator(hub), new AlwaysConfirms(), new GroupExpansionMemory(), toasts,
            Links(hub, paths), LinkFlow(hub, paths, toasts),
            new InventoryRescanService(hub, new Atalaya.Inventory.InventoryScanner()),
            governance, new NoPatternSilencesDialog(),
            directives, new NoDirectivesDialog(),
            new DriftQuery(hub), new NoDeletedUnitsDialog(),
            new ThresholdPolicyService(hub), new NoThresholdsDialog(),
            CostGaps(hub), new ModelRatesService(hub), new NoReconcileCostsDialog());
    }

    /// <summary>Las sesiones sin coste de cada aplicación (F29 §1), montadas sobre este hub.</summary>
    public static CostReconciliationService CostGaps(HubContext hub)
        => new(hub, new ModelRatesService(hub));

    /// <summary>
    /// El diálogo de reconciliar que no abre nada: el inventario lo ofrece y en un test no hay
    /// ventana. Devuelve el mismo view-model, igual que el real.
    /// </summary>
    public sealed class NoReconcileCostsDialog : Views.IReconcileCostsDialog
    {
        public ViewModels.ReconcileCostsViewModel Show(ViewModels.ReconcileCostsViewModel viewModel)
            => viewModel;
    }

    /// <summary>El confirmador que dice que sí: los tests que no ejercitan el diálogo no lo montan.</summary>
    public sealed class AlwaysConfirms : IAuditLaunchConfirmer
    {
        public bool Confirm(AuditLaunchConfirmation confirmation) => true;
    }

    /// <summary>El diálogo de umbrales que no abre nada: el inventario lo pide y no hay ventana.</summary>
    public sealed class NoThresholdsDialog : IThresholdsDialog
    {
        public ThresholdsViewModel Show(ThresholdsViewModel viewModel) => viewModel;
    }

    /// <summary>Los ajustes de esta máquina, ya cargados. De aquí sale la frescura (§8).</summary>
    public static SettingsService Settings(AppPaths paths)
    {
        var settings = new SettingsService(paths);
        settings.Load();
        return settings;
    }

    /// <param name="account">
    /// La cuenta que verá este hub. Se admite de fuera para los tests que necesitan CONECTARLA a
    /// mitad —la identidad de commit de D-037 sale del perfil, así que sin cuenta no hay perfil que
    /// mirar—; sin ella se monta una desconectada, que es como arranca todo test.
    /// </param>
    /// <param name="providers">
    /// Quiénes pueden auditar, para lo que el coste necesita saber de cada casa (PROV-2 §3). Por
    /// defecto <b>las dos de verdad</b>, que es lo que monta la aplicación: sin ellas el hub no
    /// sabría de quién son sus tarifas ni qué declara la casa que escribió cada sesión, y los
    /// tests medirían un comportamiento que en producción no existe.
    /// </param>
    public static HubContext Hub(
        AppPaths paths,
        SettingsService settings,
        DeployConfig? deploy = null,
        GitHubAccountService? account = null,
        AuditorProviderRegistry? providers = null)
    {
        AssertIsolated(paths);
        return new HubContext(
            paths, settings, account ?? Account(paths), deploy ?? new DeployConfig(),
            NullLoggerFactory.Instance, providers ?? TestProviders.Registry());
    }

    /// <inheritdoc cref="CloneLinkService"/>
    public static CloneLinkService Links(HubContext hub, AppPaths paths)
        => new(hub, new MachineConfigStore(paths.MachinesJson));

    /// <summary>
    /// El flujo de vincular con el diálogo y el selector desactivados (F5.8). Los view-models que
    /// no ejercitan la vinculación lo reciben así: el gesto existe y no abre nada.
    /// </summary>
    public static LinkCloneFlow LinkFlow(
        HubContext hub,
        AppPaths paths,
        ToastCenter? toasts = null,
        IFolderPicker? picker = null,
        ILinkCloneDialog? dialog = null,
        SettingsService? settings = null)
        => new(
            hub,
            Links(hub, paths),
            new InventoryRescanService(hub, new InventoryScanner()),
            picker ?? new NoFolderPicker(),
            dialog ?? new NoLinkCloneDialog(),
            toasts ?? new ToastCenter());

    /// <summary>
    /// Convierte una carpeta en un clon creíble: repo git de verdad con un <c>origin</c> que
    /// apunta a <paramref name="repoUrl"/> (F5.8 §1).
    /// <para>
    /// Hace falta porque desde F5.8 «tener el clon» no es «tener una carpeta»: el piloto exige
    /// que sea un repo y que su remoto sea el de la app. Un test que audita tiene que partir del
    /// mismo estado del que parte un usuario que puede auditar.
    /// </para>
    /// </summary>
    public static void MakeClone(string folder, string repoUrl)
    {
        Directory.CreateDirectory(folder);
        if (!LibGit2Sharp.Repository.IsValid(folder))
        {
            LibGit2Sharp.Repository.Init(folder);
        }

        // OMPT-BUGFIX-CI — LA IDENTIDAD, AQUÍ Y NO TEST A TEST. Un clon que nace sin ella acaba
        // commiteando con la identidad GLOBAL de la máquina: verde en el puesto de quien
        // desarrolla, rojo en un runner limpio. Se pone SIEMPRE, también sobre una carpeta que ya
        // era un repo — es idempotente, y lo que se compra es que el resultado no dependa de dónde
        // se ejecute. Ver Shared/TestGit.cs.
        Atalaya.Tests.TestGit.SetIdentity(folder);

        using var repo = new LibGit2Sharp.Repository(folder);
        if (repo.Network.Remotes["origin"] is null)
        {
            repo.Network.Remotes.Add("origin", repoUrl);
        }
        else
        {
            repo.Network.Remotes.Update("origin", r => r.Url = repoUrl);
        }
    }

    /// <summary>
    /// El asistente de alta con el importador v4 conectado (F5.9 §1) y sin nada que abra ventanas.
    /// </summary>
    public static OnboardingViewModel Onboarding(
        HubContext hub,
        AppPaths paths,
        MachineConfigStore machines,
        ToastCenter toasts,
        IUlidFactory ulids,
        NavigationService navigation,
        IFolderPicker? picker = null,
        SettingsService? settings = null,
        CycleConfigFlow? flow = null,
        RepositoryCatalog? catalog = null)
    {
        settings ??= Settings(paths);
        return new OnboardingViewModel(
            hub,
            navigation,
            new FindingIngestionService(hub, ulids),
            toasts,
            Links(hub, paths),
            LinkFlow(hub, paths, toasts, settings: settings),
            picker ?? new NoFolderPicker(),
            catalog ?? OfflineCatalog(hub, paths),
            OnboardingService(hub, machines, ulids),
            flow);
    }

    /// <summary>El servicio que da de alta, con sus piezas de verdad (F30 §4).</summary>
    public static AppOnboardingService OnboardingService(
        HubContext hub, MachineConfigStore machines, IUlidFactory ulids)
        => new(
            hub,
            new InventoryScanner(),
            machines,
            new ImportService(hub),
            new MeasuredFindingService(hub, new FindingIngestionService(hub, ulids), machines));

    /// <summary>
    /// El catálogo de repositorios SIN cuenta conectada (R3): pedirle la lista falla, que es
    /// exactamente el estado del que parte un test que no ejercita el desplegable. Ninguna llamada
    /// sale a la red.
    /// </summary>
    public static RepositoryCatalog OfflineCatalog(HubContext hub, AppPaths paths)
        => new(Account(paths), new GitHubApiClient(new System.Net.Http.HttpClient()), new DeployConfig(), hub);

    /// <summary>
    /// El Portafolio con lo mínimo. Existe desde F26 §A: la regla de que salir al portafolio deja
    /// de haber «aplicación activa» solo se puede comprobar navegando al portafolio de verdad.
    /// </summary>
    public static ViewModels.PortfolioViewModel Portfolio(
        HubContext hub, AppPaths paths, NavigationService navigation, SettingsService? settings = null)
    {
        settings ??= Settings(paths);
        var machines = new MachineConfigStore(paths.MachinesJson);
        var openSession = new OpenSessionStore(paths);
        var agent = new FakeCopilotAgent();
        var live = new LiveSessionService(
            () => new SessionCoordinator(
                hub, new FindingIngestionService(hub, new UlidFactory(SystemClock.Instance)),
                new ReconciliationService(hub), machines, new UlidFactory(SystemClock.Instance), agent, settings),
            agent, openSession);

        return new ViewModels.PortfolioViewModel(
            new PortfolioQuery(hub.Store),
            navigation,
            new AppDeletionService(hub, machines, openSession),
            new NeverConfirms(),
            live,
            hub,
            new ToastCenter(),
            Links(hub, paths),
            LinkFlow(hub, paths),
            new DriftQuery(hub),
            new ActiveApp(),
            CostGaps(hub));
    }

    /// <summary>Un confirmador que siempre dice que no: los tests que lo reciben no borran nada.</summary>
    private sealed class NeverConfirms : IDeleteAppConfirmer
    {
        public bool Confirm(DeleteAppConfirmation confirmation) => false;
    }

    /// <summary>El panel de métricas (F5.9) sin nada que abra una ventana ni un fichero.</summary>
    public static MetricsViewModel Metrics(
        HubContext hub,
        AppPaths paths,
        SettingsService settings,
        NavigationService? navigation = null,
        ToastCenter? toasts = null)
        => new(
            new MetricsQuery(hub),
            navigation ?? new NavigationService(new EmptyServiceProvider()),
            settings,
            hub,
            toasts ?? new ToastCenter());

    /// <summary>La vista Informes (F6.3) sin nada que abra una ventana ni un dialogo del sistema.</summary>
    public static ReportsViewModel Reports(
        HubContext hub,
        NavigationService? navigation = null,
        IFileSaver? saver = null,
        ToastCenter? toasts = null,
        SettingsService? settings = null)
        => new(
            new ReportsQuery(hub),
            navigation ?? new NavigationService(new EmptyServiceProvider()),
            saver ?? new RecordingFileSaver(null),
            toasts ?? new ToastCenter(),
            hub,
            // F36 — el tema decide el paso de cada color de serie (D-317). Sin ajustes cargados
            // vale el de por defecto: lo que se comprueba en los tests son las cifras y el reparto,
            // no el tono.
            settings ?? Settings(new AppPaths(Path.GetDirectoryName(hub.HubPaths.Root)!)));

    /// <summary>
    /// La carcasa (MainViewModel) con lo mínimo para poder construirla. Existe para los avisos que
    /// viven en ella y no en ninguna página —el de versión nueva, el de cierre de ciclo (F12 §G)—,
    /// que si no solo se podrían comprobar montando una sesión entera.
    /// </summary>
    public static MainViewModel Shell(
        AppPaths paths, HubContext hub, ToastCenter? toasts = null, UpdateCheckService? updates = null,
        SettingsService? settings = null, CycleConfigService? cycleConfig = null, CycleConfigFlow? configFlow = null,
        NavigationService? navigation = null, ActiveApp? activeApp = null)
    {
        settings ??= Settings(paths);
        var ulids = new UlidFactory(SystemClock.Instance);
        var machines = new MachineConfigStore(paths.MachinesJson);
        var agent = new FakeCopilotAgent();
        var openSession = new OpenSessionStore(paths);
        var busy = new AgentBusyGate();
        ToastCenter center = toasts ?? new ToastCenter();

        var live = new LiveSessionService(
            () => new SessionCoordinator(
                hub, new FindingIngestionService(hub, ulids), new ReconciliationService(hub),
                machines, ulids, agent, settings),
            agent, openSession, hub, busy: busy);

        var fix = new LiveFixService(
            hub, () => agent, machines, ulids, settings, new ReferenceCollector(),
            new FixSnapshotStore(paths), new AssistedFixLauncher(settings, Links(hub, paths), machines, busy), busy);

        return new MainViewModel(
            navigation ?? new NavigationService(new EmptyServiceProvider()),
            hub,
            settings,
            Account(paths),
            live,
            fix,
            new InterruptedSessionRecovery(hub, openSession),
            new DisplayIdService(hub),
            center,
            updates,
            cycleConfig: cycleConfig,
            configFlow: configFlow,
            activeApp: activeApp);
    }

    /// <summary>
    /// Una navegacion que sabe resolver las paginas que se le den. Existe porque los enlaces entre
    /// vistas —«ver informe» desde Metricas o desde V5— solo se pueden comprobar si el destino se
    /// puede construir de verdad.
    /// </summary>
    public static NavigationService NavigationWith(params object[] pages)
        => new(new PageServiceProvider(pages));

    /// <summary>Un guardador que no abre dialogo: anota lo que le propusieron y responde lo pactado.</summary>
    public sealed class RecordingFileSaver : IFileSaver
    {
        private readonly string? _answer;

        public RecordingFileSaver(string? answer) => _answer = answer;

        /// <summary>Los nombres por defecto que se le ofrecieron, en orden.</summary>
        public List<string> Suggested { get; } = new();

        public string? Pick(string title, string suggestedFileName, string filter)
        {
            Suggested.Add(suggestedFileName);
            return _answer;
        }
    }

    /// <summary>Resuelve por tipo exacto las paginas que le pasaron; lo demas, null.</summary>
    private sealed class PageServiceProvider : IServiceProvider
    {
        private readonly object[] _pages;

        public PageServiceProvider(object[] pages) => _pages = pages;

        public object? GetService(Type serviceType)
            => _pages.FirstOrDefault(p => serviceType.IsInstanceOfType(p));
    }

    /// <summary>Un abridor que no abre nada y anota lo que le pidieron (F5.9 §3, gráfica 4).</summary>
    public sealed class RecordingFileOpener : IFileOpener
    {
        public List<string> Opened { get; } = new();

        /// <summary>Si el fichero existe se cuenta como abierto; si no, falla igual que el real.</summary>
        public bool Open(string path)
        {
            Opened.Add(path);
            return File.Exists(path);
        }
    }

    /// <summary>Un contenedor vacío: la navegación existe y no puede resolver ninguna página.</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Un selector que siempre cancela: ningún test abre el diálogo del sistema.</summary>
    public sealed class NoFolderPicker : IFolderPicker
    {
        public string? Pick(string title, string? initialDirectory = null) => null;
    }

    /// <summary>Un selector que devuelve siempre la misma carpeta, para el paso opcional de F5.9.</summary>
    public sealed class FixedFolderPicker : IFolderPicker
    {
        private readonly string? _folder;

        public FixedFolderPicker(string? folder) => _folder = folder;

        public string? Pick(string title, string? initialDirectory = null) => _folder;
    }

    /// <summary>La gestión de patrones silenciados sin ventana: anota que se abrió y con qué app.</summary>
    public sealed class NoPatternSilencesDialog : IPatternSilencesDialog
    {
        public List<PatternSilencesViewModel> Shown { get; } = new();

        public PatternSilencesViewModel Show(PatternSilencesViewModel viewModel)
        {
            Shown.Add(viewModel);
            return viewModel;
        }
    }

    /// <summary>La gestión de directivas sin ventana: anota que se abrió y con qué app (F7).</summary>
    /// <summary>F9 §4: la lista de hallazgos sin código, sin ventana. Guarda lo que se le pidió.</summary>
    public sealed class NoDeletedUnitsDialog : IDeletedUnitsDialog
    {
        public List<DeletedUnitsViewModel> Shown { get; } = new();

        public DeletedUnitsViewModel Show(DeletedUnitsViewModel viewModel)
        {
            Shown.Add(viewModel);
            return viewModel;
        }
    }

    public sealed class NoDirectivesDialog : IDirectivesDialog
    {
        public List<DirectivesViewModel> Shown { get; } = new();

        public DirectivesViewModel Show(DirectivesViewModel viewModel)
        {
            Shown.Add(viewModel);
            return viewModel;
        }
    }

    /// <summary>Un diálogo que no se muestra: devuelve el view-model tal cual lo recibió.</summary>
    public sealed class NoLinkCloneDialog : ILinkCloneDialog
    {
        public List<LinkCloneViewModel> Shown { get; } = new();

        public LinkCloneViewModel Show(LinkCloneViewModel viewModel)
        {
            Shown.Add(viewModel);
            return viewModel;
        }
    }

    /// <summary>
    /// <b>BUGFIX-CI-2 — el motivo con el que afirma un test que mira la salud del hub.</b> Es el
    /// mismo <see cref="Atalaya.Tests.HubDiagnostics.Why"/> de los tests de almacenamiento, por el
    /// lado del <see cref="HubContext"/>: aquí el sync puede no existir todavía, y «no hay sync»
    /// también es un motivo que hay que poder leer en un <c>.trx</c>.
    /// </summary>
    public static string Why(this HubContext hub)
        => hub.Sync is null ? "el hub no tiene sync construido" : hub.Sync.Why();

    /// <summary>
    /// F3.1 Bloque 2: ningún test puede escribir en el almacén real. Si <see cref="AppPaths.Root"/>
    /// resuelve bajo <c>%LOCALAPPDATA%\Atalaya</c> (el almacén de producción) el arnés FALLA
    /// inmediatamente — evita reincidir en el episodio del "[Alta] test" y las clases nunca
    /// auditadas que aparecieron en el hub durante el desarrollo del agente falso.
    /// </summary>
    public static void AssertIsolated(AppPaths paths)
    {
        string real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Atalaya");
        string root = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar);
        string realNorm = Path.GetFullPath(real).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, realNorm, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(realNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Test isolation violation: AppPaths.Root='{root}' apunta al almacén real ('{realNorm}'). "
                + "Usa un directorio temporal (Path.GetTempPath()) para los tests.");
        }
    }
}
