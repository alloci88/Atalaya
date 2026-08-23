using Atalaya.Domain.Abstractions;

namespace Atalaya.App.Services;

/// <summary>
/// THE owner of the GitHub account token (D3). One login feeds three consumers:
/// <list type="number">
/// <item>git access to the hub (<see cref="HubContext"/>, LibGit2Sharp credentials),</item>
/// <item>Copilot authentication (the SDK's <c>GitHubToken</c>),</item>
/// <item>the git commit identity of hub commits (derived from the profile).</item>
/// </list>
/// <para>
/// Expiry/revocation: <c>gho_</c> tokens do not expire by default, but an organization can revoke
/// them. Any consumer that sees a 401 calls <see cref="MarkNeedsReconnect"/>; the shell then shows
/// amber and every networked action routes the user to the Cuenta page.
/// </para>
/// </summary>
public sealed class GitHubAccountService
{
    private readonly AccountStore _store;
    private readonly IClock _clock;
    private GitHubAccount? _account;

    public GitHubAccountService(AccountStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
        _account = store.Load();
    }

    /// <summary>Raised whenever the account, or its reconnect state, changes.</summary>
    public event Action? Changed;

    public GitHubAccount? Current => _account;

    public bool IsConnected => _account is not null;

    /// <summary>True after a 401: the stored token exists but GitHub no longer accepts it.</summary>
    public bool NeedsReconnect { get; private set; }

    /// <summary>Why the account needs reconnecting, for the status bar tooltip.</summary>
    public string? ReconnectReason { get; private set; }

    /// <summary>The account token, or null when disconnected. Read by the Copilot adapter.</summary>
    public string? Token => _account?.Token;

    /// <summary>Git identity for hub commits, derived from the profile (D2.2). Null when disconnected.</summary>
    public (string Name, string Email)? GitIdentity =>
        _account is null ? null : (_account.DisplayName, _account.CommitEmail);

    /// <summary>Persists a freshly authenticated account and clears any reconnect flag.</summary>
    public void Connect(string token, GitHubUser profile)
    {
        _account = GitHubAccount.From(profile, token, _clock.UtcNow);
        _store.Save(_account);
        NeedsReconnect = false;
        ReconnectReason = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Updates the stored profile (display name, avatar, email) from a fresh <c>GET /user</c>,
    /// keeping the token and the original connection timestamp.
    /// </summary>
    public void RefreshProfile(GitHubUser profile)
    {
        if (_account is null)
        {
            return;
        }

        _account.Id = profile.Id;
        _account.Login = profile.Login;
        _account.Name = profile.Name;
        _account.AvatarUrl = profile.AvatarUrl;
        _account.Email = profile.Email;
        _store.Save(_account);
        NeedsReconnect = false;
        ReconnectReason = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Forgets the account: deletes <c>auth.dat</c> and drops the in-memory token. Does NOT touch
    /// the hub clone — reconnecting with another account must not re-clone or corrupt it.
    /// </summary>
    public void Disconnect()
    {
        _store.Delete();
        _account = null;
        NeedsReconnect = false;
        ReconnectReason = null;
        Changed?.Invoke();
    }

    /// <summary>Called by any consumer that got a 401 with this token.</summary>
    public void MarkNeedsReconnect(string reason)
    {
        if (_account is null || (NeedsReconnect && ReconnectReason == reason))
        {
            return;
        }

        NeedsReconnect = true;
        ReconnectReason = reason;
        Changed?.Invoke();
    }

    /// <summary>Called after a successful networked operation.</summary>
    public void ClearNeedsReconnect()
    {
        if (!NeedsReconnect)
        {
            return;
        }

        NeedsReconnect = false;
        ReconnectReason = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Inspects an exception from any consumer and flags the account when it looks like the
    /// credential was rejected. Returns true when it did.
    /// </summary>
    public bool NoteFailure(Exception ex)
    {
        if (_account is null)
        {
            return false;
        }

        if (ex is GitHubApiException { Problem: GitHubApiProblem.TokenRejected })
        {
            MarkNeedsReconnect(ConnectionHelp.TokenRejected);
            return true;
        }

        string message = ex.Message?.ToLowerInvariant() ?? string.Empty;
        if (message.Contains("401") || message.Contains("unauthorized")
            || message.Contains("authentication replays") || message.Contains("too many redirects or authentication replays"))
        {
            MarkNeedsReconnect(ConnectionHelp.TokenRejected);
            return true;
        }

        return false;
    }
}
