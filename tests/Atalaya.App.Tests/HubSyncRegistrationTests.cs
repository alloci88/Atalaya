using Atalaya.App.Services;
using Atalaya.Storage.Sync;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// The first sync after connecting, against a local <c>--bare</c> remote (no network): connecting
/// must clone, pull, register the timestamp and turn the indicator green — and it must initialise
/// a brand-new empty hub instead of leaving it unusable.
/// </summary>
public sealed class HubSyncRegistrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _remote;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;

    public HubSyncRegistrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-hubsync", Guid.NewGuid().ToString("N"));
        _remote = Path.Combine(_root, "remote.git");
        Repository.Init(_remote, isBare: true);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _account = TestFactory.Account(_paths);
    }

    private HubContext Hub() => new(
        _paths, _settings, _account,
        new DeployConfig { HubUrl = _remote },
        NullLoggerFactory.Instance);

    [Fact]
    public void Connecting_clones_pulls_and_records_the_sync()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        HubContext hub = Hub();

        hub.LastSync.Should().BeNull("aún no se ha sincronizado nada");

        hub.EnsureHub();

        hub.IsCloned.Should().BeTrue();
        hub.Health.Should().Be(SyncHealth.Green, "el piloto tiene que pasar a verde tras conectar");
        hub.LastSync.Should().NotBeNull("«Última sincronización» no puede quedarse en «nunca»");
        hub.LastSyncError.Should().BeNull();
    }

    [Fact]
    public void An_empty_hub_is_initialised_and_pushed_on_first_connect()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        HubContext hub = Hub();

        hub.EnsureHub();

        hub.Store.TryReadHub().Should().NotBeNull("un hub vacío debe quedar montado, no inservible");

        // And it reached the remote: a second, independent clone sees hub.json.
        string second = Path.Combine(_root, "second");
        Repository.Clone(_remote, second);
        File.Exists(Path.Combine(second, "hub.json")).Should().BeTrue();
    }

    [Fact]
    public void The_init_commit_carries_the_identity_derived_from_the_github_profile()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));

        Hub().EnsureHub();

        using var remote = new Repository(_remote);
        Commit tip = remote.Commits.First();
        tip.Author.Name.Should().Be("Ana L.");
        tip.Author.Email.Should().Be("7+ana@users.noreply.github.com");
    }

    /// <summary>
    /// <b>Conectar siembra las tarifas y las publica</b> (R2 §2). Es la mitad que faltaba para que
    /// una instalación limpia audite con coste desde la primera sesión: no basta con escribir el
    /// fichero en el clon local, tiene que llegarle a la organización entera.
    /// </summary>
    [Fact]
    public void The_first_connect_seeds_the_model_rates_and_publishes_them()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));

        Hub().EnsureHub();

        // Un clon independiente ve la tabla: llegó al remoto, no se quedó en esta máquina.
        string second = Path.Combine(_root, "second-rates");
        Repository.Clone(_remote, second);
        File.Exists(Path.Combine(second, "model-rates.json")).Should().BeTrue();

        using var remote = new Repository(_remote);
        remote.Commits.Select(c => c.MessageShort)
            .Should().Contain(m => m.StartsWith("tarifas:", StringComparison.Ordinal),
                "el commit dice que lo que cambió fue una tarifa: es la atribución de esta tabla");
    }

    /// <summary>
    /// Y la segunda vez no escribe nada. El número de commits ya no es 1 desde R2 —conectar siembra
    /// las tarifas—, así que lo que se compara es contra lo que dejó la primera vez: lo que este test
    /// vigila es que «Comprobar conexión» no genere commits NUEVOS, no cuántos hubo al principio.
    /// </summary>
    [Fact]
    public void Re_running_the_check_does_not_re_initialise_the_hub()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        HubContext hub = Hub();
        hub.EnsureHub();

        int afterFirst;
        using (var first = new Repository(_remote))
        {
            afterFirst = first.Commits.Count();
        }

        hub.EnsureHub();

        using var remote = new Repository(_remote);
        remote.Commits.Count().Should().Be(afterFirst, "«Comprobar conexión» no debe generar commits nuevos");
    }

    [Fact]
    public void Sync_state_changes_are_announced_so_the_indicator_updates()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        HubContext hub = Hub();
        int raised = 0;
        hub.SyncStateChanged += () => raised++;

        hub.EnsureHub();

        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_failed_pull_is_reported_instead_of_passing_as_healthy()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        HubContext hub = Hub();
        hub.EnsureHub();
        hub.Health.Should().Be(SyncHealth.Green);

        // The remote disappears (revoked access, network gone, repo removed).
        DeleteTree(_remote);
        await hub.PullAsync();

        hub.Health.Should().NotBe(SyncHealth.Green);
        hub.LastSyncError.Should().NotBeNullOrEmpty("sin esto el fallo solo se ve en el log");
    }

    [Fact]
    public void Migrating_the_hub_repoints_an_existing_clone_and_keeps_its_history()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        Hub().EnsureHub();

        // The deployment now names a different hub (e.g. the organization's repo).
        string moved = Path.Combine(_root, "organizacion.git");
        Repository.Init(moved, isBare: true);
        var migrated = new HubContext(
            _paths, _settings, _account, new DeployConfig { HubUrl = moved }, NullLoggerFactory.Instance);

        migrated.EnsureHub();

        migrated.RemoteRepointedTo.Should().Be(moved, "el usuario tiene que enterarse del cambio");
        using (var local = new Repository(_paths.Hub))
        {
            local.Network.Remotes["origin"].Url.Should().Be(moved,
                "si no, el clon seguiria sincronizando contra el hub viejo en silencio");
        }

        // The local history reached the new remote.
        string check = Path.Combine(_root, "check");
        Repository.Clone(moved, check);
        File.Exists(Path.Combine(check, "hub.json")).Should().BeTrue();
    }

    [Fact]
    public void An_unchanged_hub_url_is_not_treated_as_a_migration()
    {
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
        Hub().EnsureHub();

        HubContext again = Hub();
        again.EnsureHub();

        again.RemoteRepointedTo.Should().BeNull();
    }

    [Fact]
    public void An_unconfigured_hub_is_a_no_op()
    {
        var hub = new HubContext(_paths, _settings, _account, new DeployConfig(), NullLoggerFactory.Instance);

        hub.EnsureHub();

        hub.IsConfigured.Should().BeFalse();
        hub.LastSync.Should().BeNull();
    }

    private static void DeleteTree(string path)
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    public void Dispose()
    {
        try { DeleteTree(_root); } catch { }
    }
}
