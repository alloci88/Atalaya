using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// The setup surface of F2: the opaque deployment configuration (D1), the one-token-three-consumers
/// wiring (D3), and the silent migration of users configured before F2 (D4).
/// </summary>
public sealed class ConnectionSetupTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public ConnectionSetupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-conn", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
    }

    // ---------- D1: deployment configuration ----------

    [Fact]
    public void Deploy_config_is_read_from_the_file_next_to_the_executable()
    {
        string dir = Path.Combine(_root, "deploy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(DeployConfig.OverridePath(dir), """
            {"hubUrl":"https://github.com/org/hub","gitHubClientId":"Iv1.abc","organizationLogin":"org"}
            """);

        DeployConfig config = DeployConfig.Load(dir);

        config.HubUrl.Should().Be("https://github.com/org/hub");
        config.GitHubClientId.Should().Be("Iv1.abc");
        config.ChecksOrgMembership.Should().BeTrue();
        config.HasClientId.Should().BeTrue();
    }

    [Fact]
    public void A_corrupt_override_falls_back_to_the_embedded_default()
    {
        string dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(DeployConfig.OverridePath(dir), "{ not json");

        DeployConfig config = DeployConfig.Load(dir);

        // The embedded default ships the hub URL, so the app still knows where the hub lives.
        config.HubUrl.Should().Be(DeployConfig.LoadEmbedded().HubUrl);
    }

    [Fact]
    public void An_empty_organization_means_the_membership_check_is_skipped()
    {
        DeployConfig.Parse("""{"hubUrl":"u","gitHubClientId":"c","organizationLogin":""}""")
            .ChecksOrgMembership.Should().BeFalse();
    }

    [Fact]
    public void The_embedded_default_carries_the_hub_url_so_users_never_type_it()
    {
        DeployConfig.LoadEmbedded().HasHubUrl.Should().BeTrue();
    }

    // ---------- D3: one token, three consumers ----------

    [Fact]
    public void The_account_token_wins_over_the_pat_fallback()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        settings.SetPat("github_pat_legacy");
        var account = TestFactory.Account(_paths);
        var hub = new HubContext(_paths, settings, account, Deploy(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        hub.CredentialSource.Should().Be(HubCredentialSource.Pat);

        account.Connect("gho_new", new GitHubUser(7, "ana", "Ana", null, null));

        hub.CredentialSource.Should().Be(HubCredentialSource.Account);
    }

    [Fact]
    public void Without_any_credential_git_falls_back_to_the_os_manager()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        var hub = new HubContext(_paths, settings, TestFactory.Account(_paths), Deploy(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        hub.CredentialSource.Should().Be(HubCredentialSource.OsCredentialManager);
    }

    [Fact]
    public void Commit_identity_comes_from_the_github_profile()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        settings.Current.GitUserName = "Nombre Viejo";
        settings.Current.GitUserEmail = "viejo@example.com";
        var account = TestFactory.Account(_paths);
        var hub = new HubContext(_paths, settings, account, Deploy(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        hub.ResolveIdentity().Should().Be(("Nombre Viejo", "viejo@example.com"));

        account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));

        hub.ResolveIdentity().Should().Be(("Ana L.", "7+ana@users.noreply.github.com"));
    }

    [Fact]
    public void The_hub_url_comes_from_the_deployment_not_from_the_user()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        var hub = new HubContext(_paths, settings, TestFactory.Account(_paths), Deploy(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        hub.HubUrl.Should().Be("https://github.com/org/hub");
        hub.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void The_development_override_wins_over_the_deployment()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        settings.Current.HubUrlOverride = "https://github.com/dev/hub";
        var hub = new HubContext(_paths, settings, TestFactory.Account(_paths), Deploy(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        hub.HubUrl.Should().Be("https://github.com/dev/hub");
    }

    // ---------- D4: migration of pre-F2 users ----------

    [Fact]
    public void A_legacy_hub_url_equal_to_the_deployment_is_simply_dropped()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        settings.Current.HubRepoUrl = "https://github.com/org/hub.git";
        settings.SetPat("github_pat_legacy");

        settings.MigrateConnection(Deploy());

        settings.Current.HubRepoUrl.Should().BeNull();
        settings.Current.HubUrlOverride.Should().BeNull();
        settings.GetPat().Should().Be("github_pat_legacy", "el PAT sigue siendo el fallback");
    }

    [Fact]
    public void A_legacy_hub_url_that_differs_is_kept_as_an_override()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        settings.Current.HubRepoUrl = "https://github.com/otra/hub";

        settings.MigrateConnection(Deploy());

        settings.Current.HubUrlOverride.Should().Be("https://github.com/otra/hub",
            "un equipo con otro hub no puede romperse al actualizar");
        settings.Current.HubRepoUrl.Should().BeNull();
    }

    [Fact]
    public void Migration_runs_once()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        settings.Current.HubRepoUrl = "https://github.com/otra/hub";
        settings.MigrateConnection(Deploy());

        settings.Current.HubUrlOverride = null;
        settings.MigrateConnection(Deploy());

        settings.Current.HubUrlOverride.Should().BeNull();
        settings.Current.ConnectionMigrated.Should().BeTrue();
    }

    [Fact]
    public void Migration_survives_a_reload()
    {
        var settings = new SettingsService(_paths);
        settings.Load();
        settings.Current.HubRepoUrl = "https://github.com/otra/hub";
        settings.MigrateConnection(Deploy());

        AppSettings reloaded = new SettingsService(_paths).Load();

        reloaded.ConnectionMigrated.Should().BeTrue();
        reloaded.HubUrlOverride.Should().Be("https://github.com/otra/hub");
    }

    [Theory]
    [InlineData("https://github.com/org/hub", "https://github.com/org/hub.git", true)]
    [InlineData("https://github.com/org/hub/", "https://github.com/org/hub", true)]
    [InlineData("https://GITHUB.com/Org/Hub", "https://github.com/org/hub", true)]
    [InlineData("https://github.com/org/otro", "https://github.com/org/hub", false)]
    [InlineData("", "https://github.com/org/hub", false)]
    public void Repo_urls_compare_ignoring_case_slash_and_dot_git(string a, string b, bool same)
        => SettingsService.SameRepo(a, b).Should().Be(same);

    // ---------- F2.1: the bundled CLI ----------

    [Fact]
    public void The_bundled_copilot_cli_ships_next_to_the_app()
    {
        // The SDK targets copy it into runtimes/{rid}/native, which flows into every referencing
        // project — this is what removes `npm install -g @github/copilot` from the setup.
        string? path = CopilotCliLocator.ResolveBundled();

        path.Should().NotBeNull();
        File.Exists(path).Should().BeTrue();
        Path.GetFileName(path).Should().Be(CopilotCliLocator.BinaryName);
        path.Should().Contain(Path.Combine("runtimes"));
    }

    [Fact]
    public void A_deployment_without_the_bundled_cli_reports_null_rather_than_probing_PATH()
        => CopilotCliLocator.ResolveBundled(Path.Combine(_root, "empty")).Should().BeNull();

    // ---------- F2.3: hub failure diagnostics ----------

    [Theory]
    [InlineData("too many redirects or authentication replays")]
    [InlineData("unexpected http status code: 404")]
    [InlineData("request failed with status code: 403")]
    public void Git_rejections_are_explained_as_an_org_policy_problem(string message)
    {
        (string detail, string? help) = ConnectionChecker.DescribeHubFailure(new Exception(message), "ana");

        detail.Should().Contain("ana").And.Contain("organización");
        help.Should().Be(ConnectionHelp.DocsOAuthPolicy);
    }

    [Theory]
    // The exact libgit2 wording, plus the shape it arrives in when wrapped by the transport.
    [InlineData("certificate revocation status could not be verified")]
    [InlineData("failed to send request: certificate revocation is offline or stale")]
    public void A_blocked_revocation_check_is_named_as_a_TLS_problem(string message)
    {
        (string detail, _) = ConnectionChecker.DescribeHubFailure(new Exception(message), "ana");

        detail.Should().Be(ConnectionHelp.TlsRevocationUnavailable);
        detail.Should().Contain("No es un problema de tus credenciales")
            .And.Contain("CRL/OCSP", "el usuario tiene que poder decírselo a IT tal cual");
    }

    [Theory]
    [InlineData("user rejected certificate for github.com")]
    [InlineData("the certificate cannot be verified")]
    [InlineData("certificate root is not trusted")]
    public void An_untrusted_certificate_points_at_the_corporate_CA(string message)
        => ConnectionChecker.DescribeHubFailure(new Exception(message), "ana").Detail
            .Should().Be(ConnectionHelp.TlsUntrusted);

    [Fact]
    public void Network_failures_are_reported_as_offline()
    {
        (string detail, _) = ConnectionChecker.DescribeHubFailure(
            new Exception("failed to connect to github.com"), "ana");

        detail.Should().Be(ConnectionHelp.Offline);
    }

    [Fact]
    public void The_no_seat_case_is_not_an_authentication_case()
    {
        CopilotHelp.NoSeat.Should().Contain("asiento").And.Contain("NO es un problema de autenticación");
        CopilotHelp.NoAccount.Should().Contain("Cuenta");
    }

    private static DeployConfig Deploy() => new()
    {
        HubUrl = "https://github.com/org/hub",
        GitHubClientId = "Iv1.abc",
        OrganizationLogin = string.Empty,
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
