using Atalaya.Storage;
using Atalaya.Storage.Sync;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Logging;

namespace Atalaya.App.Services;

/// <summary>
/// Owns the live hub: the <see cref="HubStore"/> and the <see cref="HubSyncService"/> built
/// from settings. Resolves the git identity (falling back to the global git config, §3) and
/// the credentials (from the DPAPI-protected PAT). Everything the views need to read/write
/// hub state goes through here.
/// </summary>
public sealed class HubContext
{
    private readonly SettingsService _settings;
    private readonly ILoggerFactory _loggerFactory;

    public HubContext(AppPaths paths, SettingsService settings, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _loggerFactory = loggerFactory;
        HubPaths = new HubPaths(paths.Hub);
        Store = new HubStore(HubPaths, loggerFactory.CreateLogger<HubStore>());
    }

    public HubPaths HubPaths { get; }

    public HubStore Store { get; }

    public HubSyncService? Sync { get; private set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.HubRepoUrl);

    public SyncHealth Health => Sync?.Health ?? SyncHealth.Amber;

    /// <summary>Raised (on a background thread) when a pull brought in changes.</summary>
    public event Action<PullResult>? Changed;

    /// <summary>Clones/opens the hub and does an initial pull. Safe to call repeatedly.</summary>
    public void EnsureHub()
    {
        if (!IsConfigured)
        {
            return;
        }

        if (Sync is null)
        {
            Sync = new HubSyncService(
                HubPaths, ResolveIdentity(), BuildCredentials(),
                _loggerFactory.CreateLogger<HubSyncService>());
            Sync.Pulled += r => Changed?.Invoke(r);
        }

        Sync.EnsureCloned(_settings.Current.HubRepoUrl!);
        Sync.Pull();
    }

    public Task<PullResult> PullAsync() => Task.Run(() => Sync?.Pull() ?? PullResult.Empty);

    /// <summary>Git identity: explicit setting, else the global git config, else a placeholder.</summary>
    public (string Name, string Email) ResolveIdentity()
    {
        string? name = _settings.Current.GitUserName;
        string? email = _settings.Current.GitUserEmail;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            try
            {
                using Configuration cfg = Configuration.BuildFrom(null);
                name ??= cfg.Get<string>("user.name")?.Value;
                email ??= cfg.Get<string>("user.email")?.Value;
            }
            catch
            {
                // no global config available
            }
        }

        return (name ?? Environment.UserName, email ?? $"{Environment.UserName}@localhost");
    }

    private CredentialsHandler? BuildCredentials()
    {
        string? pat = _settings.GetPat();
        if (string.IsNullOrEmpty(pat))
        {
            return null; // fall back to the OS credential manager
        }

        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = "x-access-token",
            Password = pat,
        };
    }
}
