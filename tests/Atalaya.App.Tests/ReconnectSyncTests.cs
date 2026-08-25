using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain.Model;
using Atalaya.Storage.Sync;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.1 — Desconectar → Conectar relanza la sincronización SIN reiniciar la app.
/// <para>
/// El síntoma era doble: el piloto se quedaba con el estado que había ganado la credencial
/// anterior, y lo que estuviera sin publicar seguía sin publicarse hasta la siguiente escritura.
/// Aquí se conduce la página de Cuenta de verdad —device flow con HTTP guionizado y un remoto
/// local <c>--bare</c>, sin red— y se comprueba que reconectar deja el hub verde y publica lo
/// pendiente.
/// </para>
/// </summary>
public sealed class ReconnectSyncTests : IDisposable
{
    private const string ClientId = "Iv1.testclientid";

    private readonly string _root;
    private readonly string _remote;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;
    private readonly HubContext _hub;
    private readonly DeployConfig _deploy;

    public ReconnectSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-reconnect", Guid.NewGuid().ToString("N"));
        _remote = Path.Combine(_root, "remote.git");
        Repository.Init(_remote, isBare: true);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _account = TestFactory.Account(_paths);
        _deploy = new DeployConfig { HubUrl = _remote, GitHubClientId = ClientId };
        TestFactory.AssertIsolated(_paths);
        _hub = new HubContext(_paths, _settings, _account, _deploy, NullLoggerFactory.Instance);
    }

    /// <summary>Reloj falso: el device flow no espera un solo segundo real.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            Now += d;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// La conversación completa de un login: código de dispositivo, token, perfil, y el perfil que
    /// vuelve a leer la verificación encadenada.
    /// </summary>
    private static HttpStub ScriptedLogin() => new HttpStub()
        .Json("""
            {"device_code":"dc","user_code":"WDJB-MJHT",
             "verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}
            """)
        .Json("""{"access_token":"gho_nuevo","token_type":"bearer","scope":"repo,read:org,read:user"}""")
        .Json("""{"id":7,"login":"ana","name":"Ana L.","avatar_url":null,"email":null}""")
        .Json("""{"id":7,"login":"ana","name":"Ana L.","avatar_url":null,"email":null}""");

    private AccountViewModel NewViewModel(HttpStub stub)
    {
        var clock = new FakeClock();
        var api = new GitHubApiClient(stub.Client());
        var agent = new FakeCopilotAgent();
        var checker = new ConnectionChecker(_account, api, _deploy, _hub, agent);

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(sp => new PortfolioQuery(_hub.Store));
        services.AddSingleton<NavigationService>();
        services.AddTransient<PortfolioViewModel>();
        ServiceProvider provider = services.BuildServiceProvider();

        return new AccountViewModel(
            _account,
            new GitHubDeviceFlow(stub.Client(), clock.Delay, () => clock.Now),
            api, _deploy, _hub, checker,
            provider.GetRequiredService<NavigationService>());
    }

    [Fact]
    public async Task Reconnecting_syncs_again_without_restarting_the_app()
    {
        // Estado de partida: cuenta conectada y hub sincronizado.
        _account.Connect("gho_viejo", new GitHubUser(7, "ana", "Ana L.", null, null));
        _hub.EnsureHub();
        _hub.Health.Should().Be(SyncHealth.Green);

        var stub = ScriptedLogin();
        AccountViewModel vm = NewViewModel(stub);

        // Y algo escrito en local que se quedó sin publicar.
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u" });
        _hub.Sync!.Commit("app: pendiente de publicar").Should().BeTrue();

        vm.DisconnectCommand.Execute(null);
        _account.IsConnected.Should().BeFalse();

        await vm.ConnectCommand.ExecuteAsync(null);

        _account.Token.Should().Be("gho_nuevo");
        _hub.Health.Should().Be(SyncHealth.Green, "el piloto tiene que refrescarse al reconectar");
        _hub.LastSync.Should().NotBeNull();
        vm.SyncSummary.Should().Contain("publicó 1 commit(s) local(es)");

        // Lo pendiente llegó al remoto: reconectar publicó, no solo trajo.
        string check = Path.Combine(_root, "check");
        Repository.Clone(_remote, check);
        File.Exists(Path.Combine(check, "apps", "app", "app.json")).Should().BeTrue();
    }

    [Fact]
    public void Disconnecting_stops_showing_the_previous_accounts_sync_state()
    {
        _account.Connect("gho_viejo", new GitHubUser(7, "ana", "Ana L.", null, null));
        _hub.EnsureHub();
        _hub.Health.Should().Be(SyncHealth.Green);

        AccountViewModel vm = NewViewModel(ScriptedLogin());
        int announcements = 0;
        _hub.SyncStateChanged += () => announcements++;

        vm.DisconnectCommand.Execute(null);

        _hub.Health.Should().NotBe(SyncHealth.Green,
            "ese verde lo ganó una credencial que ya no existe");
        vm.SyncState.Should().Be("pendiente de sincronizar");
        announcements.Should().BeGreaterThan(0, "el piloto de la barra se entera por este evento");
    }

    [Fact]
    public async Task The_first_ever_connection_still_clones_and_turns_the_indicator_green()
    {
        var stub = ScriptedLogin();
        AccountViewModel vm = NewViewModel(stub);
        _hub.IsCloned.Should().BeFalse();

        await vm.ConnectCommand.ExecuteAsync(null);

        _hub.IsCloned.Should().BeTrue();
        _hub.Health.Should().Be(SyncHealth.Green);
        vm.SyncSummary.Should().NotBeEmpty();
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
}
