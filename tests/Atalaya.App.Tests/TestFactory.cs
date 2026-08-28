using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Inventory;
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

    public static HubContext Hub(AppPaths paths, SettingsService settings, DeployConfig? deploy = null)
    {
        AssertIsolated(paths);
        return new HubContext(paths, settings, Account(paths), deploy ?? new DeployConfig(), NullLoggerFactory.Instance);
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
        ILinkCloneDialog? dialog = null)
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
        IFolderPicker? picker = null)
        => new(
            hub,
            new InventoryScanner(),
            machines,
            navigation,
            new FindingIngestionService(hub, ulids),
            toasts,
            Links(hub, paths),
            LinkFlow(hub, paths, toasts),
            new ImportService(hub),
            picker ?? new NoFolderPicker(),
            new MeasuredFindingService(hub, new FindingIngestionService(hub, ulids), machines));

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
        ToastCenter? toasts = null)
        => new(
            new ReportsQuery(hub),
            navigation ?? new NavigationService(new EmptyServiceProvider()),
            saver ?? new RecordingFileSaver(null),
            toasts ?? new ToastCenter());

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
