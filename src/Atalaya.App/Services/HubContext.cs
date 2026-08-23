using Atalaya.Domain.Model;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Logging;

namespace Atalaya.App.Services;

/// <summary>Where the hub's git credential comes from (D3).</summary>
public enum HubCredentialSource
{
    /// <summary>The token from the in-app GitHub login. The normal path.</summary>
    Account,

    /// <summary>The DPAPI-protected PAT: hidden fallback for orgs that block OAuth Apps.</summary>
    Pat,

    /// <summary>Neither: LibGit2Sharp falls back to the OS credential manager.</summary>
    OsCredentialManager,
}

/// <summary>
/// Owns the live hub: the <see cref="HubStore"/> and the <see cref="HubSyncService"/>.
/// <para>
/// Since F2 the hub URL comes from the deployment configuration (D1) — opaque to the user — and
/// the credentials come from the connected GitHub account (D3). The DPAPI-protected PAT survives
/// as a hidden fallback for teams whose organization blocks OAuth Apps; when an account token
/// exists, the PAT is ignored.
/// </para>
/// </summary>
public sealed class HubContext
{
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;
    private readonly DeployConfig _deploy;
    private readonly ILoggerFactory _loggerFactory;
    private string? _builtWithCredential;

    public HubContext(
        AppPaths paths,
        SettingsService settings,
        GitHubAccountService account,
        DeployConfig deploy,
        ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _account = account;
        _deploy = deploy;
        _loggerFactory = loggerFactory;
        HubPaths = new HubPaths(paths.Hub);
        Store = new HubStore(HubPaths, loggerFactory.CreateLogger<HubStore>());
    }

    public HubPaths HubPaths { get; }

    public HubStore Store { get; }

    public HubSyncService? Sync { get; private set; }

    /// <summary>
    /// The hub repository actually used: the developer override from advanced settings first
    /// (dev only), then the deployment configuration, then the legacy per-user setting kept for
    /// users configured before F2.
    /// </summary>
    public string? HubUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.Current.HubUrlOverride))
            {
                return _settings.Current.HubUrlOverride!.Trim();
            }

            return _deploy.HasHubUrl ? _deploy.HubUrl.Trim()
                : string.IsNullOrWhiteSpace(_settings.Current.HubRepoUrl) ? null
                : _settings.Current.HubRepoUrl!.Trim();
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(HubUrl);

    /// <summary>True when the local clone already exists (no network needed to read the hub).</summary>
    public bool IsCloned => Repository.IsValid(HubPaths.Root);

    public SyncHealth Health => Sync?.Health ?? SyncHealth.Amber;

    /// <summary>Why the last sync failed, or null when it succeeded. Surfaced in the Cuenta page.</summary>
    public string? LastSyncError => Sync?.LastError;

    /// <summary>
    /// The host whose certificate revocation status could not be checked, or null. Non-null means
    /// the connection worked but this network blocks the CRL/OCSP responders — worth showing.
    /// </summary>
    public string? RevocationUncheckedHost => Sync?.CertificatePolicy.RevocationUncheckedHost;

    /// <summary>Non-null when the deployment moved the hub and we re-pointed the existing clone.</summary>
    public string? RemoteRepointedTo => Sync?.RemoteRepointedTo;

    /// <summary>When the hub was last successfully pulled, for the Cuenta page.</summary>
    public DateTimeOffset? LastSync { get; private set; }

    /// <summary>Raised (on a background thread) when a pull brought in changes.</summary>
    public event Action<PullResult>? Changed;

    /// <summary>
    /// Raised (possibly on a background thread) whenever <see cref="Health"/>, <see cref="LastSync"/>
    /// or the credential changed, so the shell's indicator updates without waiting for a poll tick.
    /// </summary>
    public event Action? SyncStateChanged;

    /// <summary>
    /// Clones/opens the hub, does a full pull, and initializes an empty hub on first use. Safe to
    /// call repeatedly. Never throws for a merely failed pull (offline is a normal state, §3):
    /// inspect <see cref="Health"/> and <see cref="LastSyncError"/> for that.
    /// </summary>
    public void EnsureHub()
    {
        if (!IsConfigured)
        {
            return;
        }

        EnsureSync();
        Sync!.EnsureCloned(HubUrl!);
        Pull();
        PublishAfterMigration();
        InitializeIfEmpty();
    }

    /// <summary>
    /// After the deployment moved the hub and we re-pointed the clone, push the local history to
    /// the new remote. Without this the destination stays empty: <see cref="InitializeIfEmpty"/>
    /// does nothing (the local <c>hub.json</c> already exists) and no other write is pending, so
    /// the migration would silently publish nothing.
    /// </summary>
    private void PublishAfterMigration()
    {
        if (Sync?.RemoteRepointedTo is null || Health != SyncHealth.Green)
        {
            return;
        }

        Sync.Push();
        SyncStateChanged?.Invoke();
    }

    /// <summary>
    /// Writes <c>hub.json</c> and pushes it the first time we meet a brand-new, empty hub — what
    /// the old "Conectar / crear hub" button in Ajustes used to do before the connection moved to
    /// the Cuenta page. Only runs when the sync is healthy, so we never push over a broken pull.
    /// </summary>
    private void InitializeIfEmpty()
    {
        if (Sync is null || Health != SyncHealth.Green || Store.TryReadHub() is not null)
        {
            return;
        }

        string organization = _deploy.ChecksOrgMembership
            ? _deploy.OrganizationLogin
            : _account.Current?.DisplayName ?? ResolveIdentity().Name;

        Store.WriteHub(new HubInfo { OrganizationName = organization });
        Sync.CommitAndPush("hub: init");
        SyncStateChanged?.Invoke();
    }

    /// <summary>
    /// Builds the sync service, rebuilding it when the credential changed (connect / disconnect /
    /// switch account) so a new token takes effect without restarting the app.
    /// </summary>
    public void EnsureSync()
    {
        string credentialKey = CredentialKey();
        if (Sync is not null && _builtWithCredential == credentialKey)
        {
            return;
        }

        Sync?.Dispose();
        Sync = new HubSyncService(
            HubPaths, ResolveIdentity(), BuildCredentials(),
            _loggerFactory.CreateLogger<HubSyncService>(),
            new HubCertificatePolicy(
                _settings.Current.RequireTlsRevocationCheck,
                _loggerFactory.CreateLogger<HubCertificatePolicy>()));
        Sync.Pulled += r => Changed?.Invoke(r);
        _builtWithCredential = credentialKey;
    }

    public Task<PullResult> PullAsync() => Task.Run(Pull);

    private PullResult Pull()
    {
        if (Sync is null)
        {
            return PullResult.Empty;
        }

        try
        {
            PullResult result = Sync.Pull();
            if (Sync.Health == SyncHealth.Green)
            {
                // A successful fetch IS the sync, even when it brought nothing in: this is what
                // makes the first connection show a real timestamp instead of "nunca".
                LastSync = DateTimeOffset.Now;
                _account.ClearNeedsReconnect();
            }
            else if (Sync.LastError is { } error)
            {
                _account.NoteFailure(new InvalidOperationException(error));
            }

            return result;
        }
        catch (Exception ex)
        {
            _account.NoteFailure(ex);
            throw;
        }
        finally
        {
            SyncStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Git identity for hub commits: the connected account's profile (D2.2), else the explicit
    /// setting kept from before F2, else the global git config, else a placeholder.
    /// </summary>
    public (string Name, string Email) ResolveIdentity()
    {
        if (_account.GitIdentity is { } fromAccount)
        {
            return fromAccount;
        }

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

    /// <summary>
    /// The credential handed to LibGit2Sharp. GitHub over HTTPS accepts the user token as the
    /// password with the fixed username <c>x-access-token</c> — the same shape the PAT already
    /// used, so both paths share one code path.
    /// </summary>
    private CredentialsHandler? BuildCredentials()
    {
        string? token = ResolveCredentialToken();
        if (string.IsNullOrEmpty(token))
        {
            return null; // fall back to the OS credential manager
        }

        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = "x-access-token",
            Password = token,
        };
    }

    /// <summary>Account token wins; the PAT is the hidden fallback (D3).</summary>
    private string? ResolveCredentialToken() => _account.Token ?? _settings.GetPat();

    /// <summary>Which credential the hub is using right now. Shown in Ajustes → avanzadas.</summary>
    public HubCredentialSource CredentialSource =>
        _account.Token is not null ? HubCredentialSource.Account
        : _settings.GetPat() is not null ? HubCredentialSource.Pat
        : HubCredentialSource.OsCredentialManager;

    /// <summary>
    /// Identifies the credential + identity the current <see cref="Sync"/> was built with, so we
    /// know when to rebuild it. Never contains the token itself.
    /// </summary>
    private string CredentialKey()
    {
        (string name, string email) = ResolveIdentity();
        string source = _account.Token is not null ? $"account:{_account.Current?.Login}"
            : _settings.GetPat() is not null ? "pat"
            : "none";
        return $"{source}|{name}|{email}";
    }
}
