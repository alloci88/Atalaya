using System.Net;
using Atalaya.App.Services;
using Atalaya.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// The account pipeline (F2.2/F2.3): profile fetch, DPAPI storage, the diagnosis of GitHub's
/// policy errors, and the "a later 401 requires reconnection" contract of D3.
/// </summary>
public sealed class GitHubAccountTests : IDisposable
{
    private const string UserJson = """
        {"id":4242,"login":"alloci88","name":"Ana L.","avatar_url":"https://avatars/1","email":null}
        """;

    private readonly string _root;
    private readonly AppPaths _paths;

    public GitHubAccountTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-account", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
    }

    [Fact]
    public async Task Profile_is_read_from_the_user_endpoint()
    {
        var stub = new HttpStub().Json(UserJson);
        var api = new GitHubApiClient(stub.Client());

        GitHubUser user = await api.GetCurrentUserAsync("gho_x", CancellationToken.None);

        user.Login.Should().Be("alloci88");
        user.DisplayName.Should().Be("Ana L.");
        user.AvatarUrl.Should().Be("https://avatars/1");
        stub.Requests[0].Headers.Authorization!.ToString().Should().Be("Bearer gho_x");
    }

    [Fact]
    public void Private_email_falls_back_to_the_github_noreply_address()
    {
        var user = new GitHubUser(4242, "alloci88", "Ana L.", null, null);
        user.CommitEmail.Should().Be("4242+alloci88@users.noreply.github.com");

        var withEmail = user with { Email = "ana@example.com" };
        withEmail.CommitEmail.Should().Be("ana@example.com");
    }

    [Fact]
    public async Task Organization_membership_is_checked_against_user_orgs()
    {
        var api = new GitHubApiClient(new HttpStub()
            .Json("""[{"login":"otra"},{"login":"AlloCi88"}]""")
            .Client());

        (await api.IsOrganizationMemberAsync("gho_x", "alloci88", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Non_member_is_reported_as_such()
    {
        var api = new GitHubApiClient(new HttpStub().Json("""[{"login":"otra"}]""").Client());

        (await api.IsOrganizationMemberAsync("gho_x", "alloci88", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Saml_sso_403_gets_the_sso_instruction()
    {
        var api = new GitHubApiClient(new HttpStub()
            .Status(HttpStatusCode.Forbidden, """{"message":"Resource protected by organization SAML enforcement."}""",
                ("X-GitHub-SSO", "required; url=https://github.com/orgs/x/sso"))
            .Client());

        Func<Task> act = () => api.GetCurrentUserAsync("gho_x", CancellationToken.None);

        GitHubApiException ex = (await act.Should().ThrowAsync<GitHubApiException>()).Which;
        ex.Problem.Should().Be(GitHubApiProblem.SamlRequired);
        ex.Message.Should().Contain("SSO");
    }

    [Fact]
    public async Task Plain_403_gets_the_oauth_app_policy_instruction()
    {
        var api = new GitHubApiClient(new HttpStub()
            .Status(HttpStatusCode.Forbidden, """{"message":"Forbidden"}""")
            .Client());

        Func<Task> act = () => api.GetCurrentUserAsync("gho_x", CancellationToken.None);

        GitHubApiException ex = (await act.Should().ThrowAsync<GitHubApiException>()).Which;
        ex.Problem.Should().Be(GitHubApiProblem.OrgPolicyBlocked);
        ex.Message.Should().Contain("Request").And.Contain("owner");
    }

    [Fact]
    public async Task Offline_is_distinguished_from_a_rejection()
    {
        var api = new GitHubApiClient(new HttpStub()
            .Throws(new System.Net.Http.HttpRequestException("No such host is known."))
            .Client());

        Func<Task> act = () => api.GetCurrentUserAsync("gho_x", CancellationToken.None);

        (await act.Should().ThrowAsync<GitHubApiException>()).Which.Problem.Should().Be(GitHubApiProblem.Offline);
    }

    [Theory]
    [InlineData("https://github.com/alloci88/atalaya-hub", "alloci88", "atalaya-hub")]
    [InlineData("https://github.com/alloci88/atalaya-hub.git", "alloci88", "atalaya-hub")]
    [InlineData("https://github.com/Org/Hub/", "Org", "Hub")]
    public void Repository_urls_split_into_owner_and_repo(string url, string owner, string repo)
        => GitHubApiClient.ParseRepositoryUrl(url).Should().Be((owner, repo));

    [Theory]
    [InlineData("https://gitlab.com/a/b")]
    [InlineData("https://github.com/soloowner")]
    [InlineData("C:\\ruta\\local\\hub")]
    [InlineData(null)]
    public void Non_github_urls_are_not_parsed(string? url)
        => GitHubApiClient.ParseRepositoryUrl(url).Should().BeNull();

    [Fact]
    public async Task Read_without_write_is_detected_so_the_404_can_be_explained()
    {
        // GitHub answers a push without write access with 404, exactly like "does not exist".
        // Only the API can tell them apart.
        var api = new GitHubApiClient(new HttpStub()
            .Json("""{"full_name":"o/r","permissions":{"admin":false,"push":false,"pull":true}}""")
            .Client());

        (await api.GetRepositoryAccessAsync("gho_x", "o", "r", CancellationToken.None))
            .Should().Be(RepositoryAccess.ReadOnly);
    }

    [Fact]
    public async Task Write_access_is_detected()
    {
        var api = new GitHubApiClient(new HttpStub()
            .Json("""{"full_name":"o/r","permissions":{"admin":true,"push":true,"pull":true}}""")
            .Client());

        (await api.GetRepositoryAccessAsync("gho_x", "o", "r", CancellationToken.None))
            .Should().Be(RepositoryAccess.ReadWrite);
    }

    [Fact]
    public async Task A_404_on_the_repository_means_it_is_not_visible()
    {
        var api = new GitHubApiClient(new HttpStub()
            .Status(HttpStatusCode.NotFound, """{"message":"Not Found"}""")
            .Client());

        (await api.GetRepositoryAccessAsync("gho_x", "o", "r", CancellationToken.None))
            .Should().Be(RepositoryAccess.NotVisible);
    }

    [Fact]
    public async Task An_org_that_has_not_approved_the_app_is_told_apart_from_a_missing_repo()
    {
        var api = new GitHubApiClient(new HttpStub()
            .Status(HttpStatusCode.Forbidden, """{"message":"Forbidden"}""")
            .Client());

        (await api.GetRepositoryAccessAsync("gho_x", "o", "r", CancellationToken.None))
            .Should().Be(RepositoryAccess.OrgPolicyBlocked);
    }

    [Fact]
    public void Read_only_help_names_the_account_and_the_repository()
    {
        string help = ConnectionHelp.HubReadOnly("alopezciller", "https://github.com/alloci88/atalaya-hub");

        help.Should().Contain("alopezciller").And.Contain("atalaya-hub").And.Contain("Write");
    }

    [Fact]
    public void Token_is_stored_encrypted_and_round_trips()
    {
        var store = new AccountStore(_paths);
        var account = GitHubAccount.From(
            new GitHubUser(4242, "alloci88", "Ana L.", null, null), "gho_supersecret", DateTimeOffset.UtcNow);

        store.Save(account);

        File.ReadAllBytes(_paths.AuthDat).Should().NotBeEmpty();
        File.ReadAllText(_paths.AuthDat).Should().NotContain("gho_supersecret");
        store.Load()!.Token.Should().Be("gho_supersecret");
        store.Load()!.CommitEmail.Should().Be("4242+alloci88@users.noreply.github.com");
    }

    [Fact]
    public void Disconnect_forgets_the_token_but_leaves_the_hub_clone_alone()
    {
        var service = new GitHubAccountService(new AccountStore(_paths), SystemClock.Instance);
        service.Connect("gho_x", new GitHubUser(1, "a", null, null, null));
        string hubMarker = Path.Combine(_paths.Hub, "marker.txt");
        Directory.CreateDirectory(_paths.Hub);
        File.WriteAllText(hubMarker, "hub data");

        service.Disconnect();

        service.IsConnected.Should().BeFalse();
        service.Token.Should().BeNull();
        File.Exists(_paths.AuthDat).Should().BeFalse();
        File.Exists(hubMarker).Should().BeTrue("desconectar nunca toca el clon del hub");
    }

    [Fact]
    public void Reconnecting_with_another_account_replaces_the_identity()
    {
        var service = new GitHubAccountService(new AccountStore(_paths), SystemClock.Instance);
        service.Connect("gho_a", new GitHubUser(1, "ana", "Ana", null, null));
        service.Disconnect();
        service.Connect("gho_b", new GitHubUser(2, "beto", "Beto", null, null));

        service.GitIdentity.Should().Be(("Beto", "2+beto@users.noreply.github.com"));
        new GitHubAccountService(new AccountStore(_paths), SystemClock.Instance).Current!.Login.Should().Be("beto");
    }

    [Fact]
    public void A_later_401_flags_the_account_for_reconnection()
    {
        var service = new GitHubAccountService(new AccountStore(_paths), SystemClock.Instance);
        service.Connect("gho_x", new GitHubUser(1, "ana", null, null, null));
        int changes = 0;
        service.Changed += () => changes++;

        bool noted = service.NoteFailure(
            new GitHubApiException(GitHubApiProblem.TokenRejected, ConnectionHelp.TokenRejected));

        noted.Should().BeTrue();
        service.NeedsReconnect.Should().BeTrue();
        service.ReconnectReason.Should().Be(ConnectionHelp.TokenRejected);
        changes.Should().Be(1);

        // A successful operation clears it again.
        service.ClearNeedsReconnect();
        service.NeedsReconnect.Should().BeFalse();
    }

    [Fact]
    public void Git_authentication_failures_also_flag_the_account()
    {
        var service = new GitHubAccountService(new AccountStore(_paths), SystemClock.Instance);
        service.Connect("gho_x", new GitHubUser(1, "ana", null, null, null));

        service.NoteFailure(new InvalidOperationException(
            "too many redirects or authentication replays")).Should().BeTrue();
        service.NeedsReconnect.Should().BeTrue();
    }

    [Fact]
    public void Failures_without_an_account_are_not_reconnection_problems()
    {
        var service = new GitHubAccountService(new AccountStore(_paths), SystemClock.Instance);

        service.NoteFailure(new InvalidOperationException("401 unauthorized")).Should().BeFalse();
        service.NeedsReconnect.Should().BeFalse();
    }

    [Fact]
    public void Refreshing_the_profile_keeps_the_token()
    {
        var service = new GitHubAccountService(new AccountStore(_paths), SystemClock.Instance);
        service.Connect("gho_x", new GitHubUser(1, "ana", "Ana", null, null));

        service.RefreshProfile(new GitHubUser(1, "ana", "Ana Nueva", "https://avatars/9", "ana@x.com"));

        service.Token.Should().Be("gho_x");
        service.Current!.AvatarUrl.Should().Be("https://avatars/9");
        service.GitIdentity.Should().Be(("Ana Nueva", "ana@x.com"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
