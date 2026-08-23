using Atalaya.Copilot;
using Atalaya.Storage.Sync;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>State of one step of the chained verification (D2.3).</summary>
public enum CheckState
{
    Pending,
    Running,
    Ok,
    Failed,

    /// <summary>Not applicable in this deployment (e.g. no organization configured).</summary>
    Skipped,
}

/// <summary>
/// One live row of the chained verification. Bound directly by the Cuenta page, so it updates
/// with a check mark as each step resolves.
/// </summary>
public sealed partial class ConnectionStep : ObservableObject
{
    public ConnectionStep(string key, string title)
    {
        Key = key;
        _title = title;
    }

    public string Key { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private CheckState _state = CheckState.Pending;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private string? _helpUrl;

    public bool IsVisible => State != CheckState.Skipped;

    partial void OnStateChanged(CheckState value) => OnPropertyChanged(nameof(IsVisible));

    internal void Reset(string title)
    {
        Title = title;
        State = CheckState.Pending;
        Detail = string.Empty;
        HelpUrl = null;
    }

    internal void Skip()
    {
        State = CheckState.Skipped;
        Detail = string.Empty;
        HelpUrl = null;
    }

    internal void Succeed(string title, string detail = "")
    {
        Title = title;
        Detail = detail;
        HelpUrl = null;
        State = CheckState.Ok;
    }

    internal void Fail(string detail, string? helpUrl = null)
    {
        Detail = detail;
        HelpUrl = helpUrl;
        State = CheckState.Failed;
    }
}

/// <summary>Outcome of a full run of the chain.</summary>
public sealed record ConnectionCheckResult(bool AllOk, string? FirstProblem);

/// <summary>
/// Runs the four chained checks of the Cuenta page (D2.3), each reporting live and each failing
/// with an actionable diagnosis instead of a raw error:
/// <list type="number">
/// <item>authenticated as {login},</item>
/// <item>member of {org} — only when the deployment configures one,</item>
/// <item>access to the hub (clone it here on first run, else fetch),</item>
/// <item>Copilot available (SDK client with the account token, no session created).</item>
/// </list>
/// </summary>
public sealed class ConnectionChecker
{
    private readonly GitHubAccountService _account;
    private readonly GitHubApiClient _api;
    private readonly DeployConfig _deploy;
    private readonly HubContext _hub;
    private readonly ICopilotAgent _agent;

    public ConnectionChecker(
        GitHubAccountService account,
        GitHubApiClient api,
        DeployConfig deploy,
        HubContext hub,
        ICopilotAgent agent)
    {
        _account = account;
        _api = api;
        _deploy = deploy;
        _hub = hub;
        _agent = agent;

        Steps = new[]
        {
            new ConnectionStep("auth", "Autenticado"),
            new ConnectionStep("org", "Miembro de la organización"),
            new ConnectionStep("hub", "Acceso al hub"),
            new ConnectionStep("copilot", "Copilot disponible"),
        };
    }

    /// <summary>The four rows, created once and mutated in place (safe to bind).</summary>
    public IReadOnlyList<ConnectionStep> Steps { get; }

    private ConnectionStep Step(string key) => Steps.First(s => s.Key == key);

    public async Task<ConnectionCheckResult> RunAsync(CancellationToken ct)
    {
        foreach (ConnectionStep step in Steps)
        {
            step.Reset(step.Key switch
            {
                "auth" => "Autenticado",
                "org" => $"Miembro de {(_deploy.ChecksOrgMembership ? _deploy.OrganizationLogin : "la organización")}",
                "hub" => "Acceso al hub",
                _ => "Copilot disponible",
            });
        }

        string? firstProblem = null;

        // 1 — Authenticated. Re-reads the profile so a renamed account or a new avatar refreshes,
        //     and so a revoked token is caught here rather than deep inside a clone.
        ConnectionStep auth = Step("auth");
        auth.State = CheckState.Running;
        string? token = _account.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            auth.Fail("No hay ninguna cuenta conectada. Pulsa «Conectar con GitHub».");
            SkipRest("org", "hub", "copilot");
            return new ConnectionCheckResult(false, auth.Detail);
        }

        try
        {
            GitHubUser user = await _api.GetCurrentUserAsync(token, ct);
            _account.RefreshProfile(user);
            auth.Succeed($"Autenticado como {user.Login}", user.DisplayName);
        }
        catch (GitHubApiException ex)
        {
            _account.NoteFailure(ex);
            auth.Fail(ex.Message, HelpFor(ex.Problem));
            SkipRest("org", "hub", "copilot");
            return new ConnectionCheckResult(false, ex.Message);
        }

        // 2 — Organization membership. Only when the deployment names one: while the hub is still
        //     a personal repo, the clone itself is the real gate.
        ConnectionStep org = Step("org");
        if (!_deploy.ChecksOrgMembership)
        {
            org.Skip();
        }
        else
        {
            org.State = CheckState.Running;
            string login = _account.Current!.Login;
            try
            {
                bool member = await _api.IsOrganizationMemberAsync(token, _deploy.OrganizationLogin, ct);
                if (member)
                {
                    org.Succeed($"Miembro de {_deploy.OrganizationLogin}");
                }
                else
                {
                    org.Fail(ConnectionHelp.NotAnOrgMember(login, _deploy.OrganizationLogin),
                        ConnectionHelp.DocsOAuthPolicy);
                    firstProblem ??= org.Detail;
                }
            }
            catch (GitHubApiException ex)
            {
                _account.NoteFailure(ex);
                org.Fail(ex.Message, HelpFor(ex.Problem));
                firstProblem ??= ex.Message;
            }
        }

        // 3 — Hub access: a real clone/fetch with the token. This is the check that matters.
        ConnectionStep hub = Step("hub");
        hub.State = CheckState.Running;
        if (!_hub.IsConfigured)
        {
            hub.Fail(ConnectionHelp.NoHubUrl);
            firstProblem ??= hub.Detail;
        }
        else
        {
            try
            {
                // A full clone + pull with the account token. EnsureHub swallows a failed pull
                // (offline is a normal state), so the health is what tells us whether the sync
                // actually happened — otherwise this step would go green over a broken hub.
                await Task.Run(_hub.EnsureHub, ct);

                if (_hub.Health == SyncHealth.Green && _hub.LastSync is { } t)
                {
                    hub.Succeed("Acceso al hub", $"Sincronizado {t.ToLocalTime():g} · {_hub.HubPaths.Root}");
                }
                else
                {
                    (string detail, string? help) = _hub.LastSyncError is { } error
                        ? DescribeHubFailure(new InvalidOperationException(error), _account.Current?.Login ?? "?")
                        : ("El clon existe pero no se ha podido sincronizar con el hub.", null);
                    hub.Fail(detail, help);
                    firstProblem ??= detail;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _account.NoteFailure(ex);
                (string detail, string? help) = DescribeHubFailure(ex, _account.Current?.Login ?? "?");
                hub.Fail(detail, help);
                firstProblem ??= detail;
            }
        }

        // 4 — Copilot: start the SDK client with the account token and ask for auth status; no
        //     session is created (F2.1: cheapest check the 1.0.11 API allows).
        ConnectionStep copilot = Step("copilot");
        copilot.State = CheckState.Running;
        try
        {
            AgentReadiness readiness = await _agent.CheckAsync(ct);
            if (readiness.Ready)
            {
                copilot.Succeed("Copilot disponible", readiness.Message);
            }
            else
            {
                copilot.Fail(readiness.Message,
                    readiness.Problem == AgentProblem.NoSeat ? ConnectionHelp.DocsCopilotSeat : null);
                firstProblem ??= readiness.Message;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            copilot.Fail($"No se pudo comprobar Copilot: {ex.Message}");
            firstProblem ??= copilot.Detail;
        }

        return new ConnectionCheckResult(Steps.All(s => s.State is CheckState.Ok or CheckState.Skipped), firstProblem);
    }

    private void SkipRest(params string[] keys)
    {
        foreach (string key in keys)
        {
            Step(key).Skip();
        }
    }

    private static string? HelpFor(GitHubApiProblem problem) => problem switch
    {
        GitHubApiProblem.SamlRequired => ConnectionHelp.DocsSamlSso,
        GitHubApiProblem.OrgPolicyBlocked => ConnectionHelp.DocsOAuthPolicy,
        _ => null,
    };

    /// <summary>
    /// Turns a LibGit2Sharp/network failure into the org-policy vocabulary the user can act on:
    /// git only ever reports "authentication replays" or a 404 for a repo it may not see.
    /// </summary>
    internal static (string Detail, string? HelpUrl) DescribeHubFailure(Exception ex, string login)
    {
        string m = ex.Message?.ToLowerInvariant() ?? string.Empty;

        if (m.Contains("could not resolve host") || m.Contains("failed to send request")
            || m.Contains("timed out") || m.Contains("failed to connect"))
        {
            return (ConnectionHelp.Offline, null);
        }

        if (m.Contains("authentication replays") || m.Contains("401") || m.Contains("unauthorized")
            || m.Contains("403") || m.Contains("404") || m.Contains("not found")
            || m.Contains("too many redirects"))
        {
            return (ConnectionHelp.HubAccessDenied(login), ConnectionHelp.DocsOAuthPolicy);
        }

        return ($"No se pudo acceder al hub: {ex.Message}", null);
    }
}
