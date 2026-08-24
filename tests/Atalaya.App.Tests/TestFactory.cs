using Atalaya.App.Services;
using Atalaya.Domain.Abstractions;
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
