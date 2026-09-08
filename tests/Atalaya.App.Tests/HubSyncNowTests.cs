using Atalaya.App.Services;
using Atalaya.Domain.Model;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using Atalaya.Tests;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.1 — «Sincronizar ahora» es pull Y push, contra un remoto local <c>--bare</c> (norma N-1,
/// sin red).
/// <para>
/// El botón solo hacía pull: un commit local que no hubiera logrado publicarse se quedaba esperando
/// a la siguiente escritura del usuario, así que el panel decía «sincronizado» y en GitHub no había
/// nada. Estos tests fijan las dos direcciones y el resumen que las nombra.
/// </para>
/// </summary>
public sealed class HubSyncNowTests : IDisposable
{
    private readonly string _root;
    private readonly string _remote;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;

    public HubSyncNowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-syncnow", Guid.NewGuid().ToString("N"));
        _remote = Path.Combine(_root, "remote.git");
        TestGit.Init(_remote, isBare: true);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _account = TestFactory.Account(_paths);
        _account.Connect("gho_x", new GitHubUser(7, "ana", "Ana L.", null, null));
    }

    private HubContext Hub() => new(
        _paths, _settings, _account,
        new DeployConfig { HubUrl = _remote },
        NullLoggerFactory.Instance);

    /// <summary>Escribe un hallazgo y lo COMMITEA sin publicarlo: el commit local pendiente.</summary>
    private static void CommitLocallyWithoutPushing(HubContext hub, string title)
    {
        hub.Store.WriteApp(new AppConfig { Slug = "app", Name = title, RepoUrl = "u" });
        hub.Sync!.Commit($"app: {title}").Should().BeTrue();
    }

    [Fact]
    public void Sync_now_publishes_the_local_commits_that_were_still_pending()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        CommitLocallyWithoutPushing(hub, "pendiente");

        HubSyncReport report = hub.SyncNow();

        report.PendingCommits.Should().Be(1, "había exactamente un commit local sin publicar");
        report.Pushed.Should().BeTrue(hub.Why());

        // Y llegó al remoto de verdad: un clon independiente lo ve.
        string check = Path.Combine(_root, "check");
        Repository.Clone(_remote, check);
        File.Exists(Path.Combine(check, "apps", "app", "app.json")).Should().BeTrue(
            "sin el push, el commit se quedaría esperando a la siguiente escritura del usuario");
    }

    [Fact]
    public void Sync_now_reports_what_it_brought_in()
    {
        HubContext hub = Hub();
        hub.EnsureHub();

        // Otro usuario publica desde su propio clon.
        var otherPaths = new HubPaths(Path.Combine(_root, "otro"));
        using var other = new HubSyncService(otherPaths, ("Otro", "otro@example.com"));
        other.EnsureCloned(_remote);
        new HubStore(otherPaths).WriteApp(new AppConfig { Slug = "suya", Name = "Suya", RepoUrl = "u" });
        other.CommitAndPush("app: suya").Should().BeTrue(other.Why());

        HubSyncReport report = hub.SyncNow();

        report.PulledFiles.Should().BeGreaterThan(0);
        report.Describe().Should().Contain("Trajo");
        hub.Store.TryReadApp("suya").Should().NotBeNull();
    }

    [Fact]
    public void With_nothing_pending_sync_now_says_so_instead_of_claiming_a_push()
    {
        HubContext hub = Hub();
        hub.EnsureHub();

        HubSyncReport report = hub.SyncNow();

        report.PendingCommits.Should().Be(0);
        report.Pushed.Should().BeTrue("no haber tenido nada que publicar no es un fallo · " + hub.Why());
        report.Describe().Should().Contain("nada pendiente de publicar");
    }

    [Fact]
    public void The_summary_always_names_both_directions()
    {
        HubContext hub = Hub();
        hub.EnsureHub();
        CommitLocallyWithoutPushing(hub, "pendiente");

        string summary = hub.SyncNow().Describe();

        summary.Should().Contain("publicó 1 commit(s) local(es)");
        summary.Should().Contain("No trajo cambios");
    }

    [Fact]
    public void Sync_now_still_clones_the_hub_on_first_use()
    {
        HubContext hub = Hub();
        hub.IsCloned.Should().BeFalse();

        HubSyncReport report = hub.SyncNow();

        hub.IsCloned.Should().BeTrue();
        hub.Health.Should().Be(SyncHealth.Green, hub.Why());
        hub.Store.TryReadHub().Should().NotBeNull("un hub vacío se sigue inicializando");
        report.Pushed.Should().BeTrue(hub.Why());
    }

    [Fact]
    public void An_unconfigured_hub_is_a_no_op()
    {
        var hub = new HubContext(_paths, _settings, _account, new DeployConfig(), NullLoggerFactory.Instance);

        HubSyncReport report = hub.SyncNow();

        report.Should().Be(HubSyncReport.None);
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Un_commit_sin_publicar_sale_SOLO_en_el_arranque_siguiente()
    {
        HubContext primero = Hub();
        primero.EnsureHub();
        CommitLocallyWithoutPushing(primero, "lo que no llego a salir");
        primero.PendingCommits.Should().Be(1);

        // Se cierra sin sincronizar: el commit se queda en el clon, como cuando el hub no contesta.
        primero.Sync!.Dispose();

        // Y AL ARRANCAR OTRA VEZ SALE SOLO, sin que el usuario pulse nada (F31 §2). Antes esto lo
        // hacia unicamente «Sincronizar ahora», asi que un commit que no logro publicarse esperaba
        // a que a alguien se le ocurriera pulsar un boton.
        HubContext segundo = Hub();
        segundo.EnsureHub();

        segundo.PendingCommits.Should().Be(0, "el arranque es una sincronizacion como las demas");
        segundo.PendingLabel.Should().BeEmpty();

        // Y esta de verdad en el hub, no solo en la cuenta local.
        string testigo = Path.Combine(_root, "testigo");
        using var sync = new HubSyncService(new HubPaths(testigo), ("Testigo", "t@example.invalid"));
        sync.EnsureCloned(_remote);
        sync.Pull();
        new HubStore(new HubPaths(testigo)).TryReadApp("app")!.Name
            .Should().Be("lo que no llego a salir");

        segundo.Sync!.Dispose();
    }

    [Fact]
    public void Y_mientras_no_sale_se_dice_cuanto_hay_pendiente()
    {
        HubContext hub = Hub();
        hub.EnsureHub();

        hub.PendingLabel.Should().BeEmpty("al dia no se dice nada");

        CommitLocallyWithoutPushing(hub, "uno");
        hub.PendingLabel.Should().Be("1 commit pendiente de publicar");

        hub.Store.WriteApp(new AppConfig { Slug = "otra", Name = "dos", RepoUrl = "u" });
        hub.Sync!.Commit("app: dos").Should().BeTrue();
        hub.PendingLabel.Should().Be("2 commits pendientes de publicar");

        hub.Sync.Dispose();
    }
}
