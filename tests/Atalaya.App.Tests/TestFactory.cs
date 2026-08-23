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
        => new(paths, settings, Account(paths), deploy ?? new DeployConfig(), NullLoggerFactory.Instance);
}
