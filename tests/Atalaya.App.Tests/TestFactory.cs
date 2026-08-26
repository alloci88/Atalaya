using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Domain.Abstractions;
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

    /// <summary>Un selector que siempre cancela: ningún test abre el diálogo del sistema.</summary>
    public sealed class NoFolderPicker : IFolderPicker
    {
        public string? Pick(string title, string? initialDirectory = null) => null;
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
