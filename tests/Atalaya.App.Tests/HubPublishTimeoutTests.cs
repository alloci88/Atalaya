using System.Net;
using System.Net.Sockets;
using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Tests;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>BUGFIX-PUSH — una sesión no se queda colgada porque el hub no conteste.</b>
/// <para>
/// <b>El parte, con la pila delante.</b> La sesión no arrancaba y «Detener» no respondía. El hilo
/// de interfaz estaba <b>libre</b> —en su bucle de mensajes—; el de la sesión llevaba más de un
/// minuto dentro de <c>LibGit2Sharp.Network.Push</c>, llamado desde <c>PublishClaims</c>, que es lo
/// PRIMERO que hace una sesión. libgit2 no le pone reloj a su transporte y no hay nada que
/// cancelar, así que «Detener» no tenía a qué llegar.
/// </para>
/// <para>
/// <b>Lo que fija este test, y por qué es de regla.</b> Que la sesión <b>termine</b> —con su motivo
/// escrito y sin girar— cuando el hub no contesta. Es lo que se rompe en silencio: una sesión
/// colgada no lanza, no falla y no escribe nada; se queda. Los tests de sync de siempre corren
/// contra un remoto local que contesta al instante, así que ninguno podía verlo.
/// </para>
/// <para>
/// El reloj se baja a dos segundos <b>en esta instancia</b> del servicio: lo que se prueba es que
/// existe y que la sesión lo respeta, no cuánto vale. En producción son treinta.
/// </para>
/// </summary>
public sealed class HubPublishTimeoutTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/app.git";
    private const string UnitPath = "src/A.cs";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly ServiceProvider _provider;
    private readonly TcpListener _mute;
    private readonly CancellationTokenSource _accepting = new();
    private readonly List<TcpClient> _held = new();

    public HubPublishTimeoutTests()
    {
        // Un remoto que ACEPTA y no contesta nunca. Un `--bare` local contesta al instante, así que
        // no puede reproducir la ausencia de respuesta, que es justo lo que colgaba. Y sigue sin
        // salir de la máquina (N-1).
        _mute = new TcpListener(IPAddress.Loopback, 0);
        _mute.Start();
        _ = Task.Run(async () =>
        {
            while (!_accepting.IsCancellationRequested)
            {
                try
                {
                    _held.Add(await _mute.AcceptTcpClientAsync(_accepting.Token));
                }
                catch (Exception)
                {
                    return;
                }
            }
        });

        _root = Path.Combine(Path.GetTempPath(), "atalaya-hubpush", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);

        // EL CLON, ANTES DE ESCRIBIR NADA: `git clone` exige un directorio vacío, y el hub se
        // escribe dentro de él. Primero el clon del `--bare` local, y con él ya montado se puebla.
        string bare = Path.Combine(_root, "remote.git");
        TestGit.Init(bare, isBare: true);
        _hub.EnsureSync();
        _hub.Sync!.EnsureCloned(bare);

        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        string clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(clone, RepoUrl);
        Directory.CreateDirectory(Path.Combine(clone, "src"));
        File.WriteAllText(Path.Combine(clone, "src", "A.cs"), "class A { void M() { } }");
        _machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = UnitPath, Module = "M", State = UnitState.Pendiente } },
        });

        // Y ahora el origen apunta al vacío: el clon es de verdad y lo único que no contesta es el
        // otro lado. `EnsureCloned` sobre un clon que ya existe reapunta el remoto.
        _hub.Sync.EnsureCloned($"http://127.0.0.1:{((IPEndPoint)_mute.LocalEndpoint).Port}/hub.git");
        _hub.Sync.PushTimeout = TimeSpan.FromSeconds(2);

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_paths);
        services.AddSingleton(_settings);
        services.AddSingleton(_machines);
        services.AddSingleton<IUlidFactory>(_ulids);
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<OpenSessionStore>();
        services.AddTransient<SessionCoordinator>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _accepting.Cancel();
        foreach (TcpClient client in _held.ToList())
        {
            client.Dispose();
        }

        _mute.Stop();
        _accepting.Dispose();
        _hub.CloseSync();
        try
        {
            // Los `pack` de git quedan en solo lectura: hay que quitarles el atributo antes de
            // borrar, como hace `TempRepo` en los tests de sync.
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Un temporal que no se deja borrar no puede tumbar un test que ya ha dicho lo suyo.
        }
    }

    [Fact]
    public async Task Un_hub_que_no_contesta_termina_la_sesion_con_motivo_y_no_la_deja_girando()
    {
        var agent = new FakeCopilotAgent();
        var live = new LiveSessionService(
            () => new SessionCoordinator(
                _hub, _provider.GetRequiredService<FindingIngestionService>(),
                _provider.GetRequiredService<ReconciliationService>(), _machines, _ulids, agent, _settings),
            agent, _provider.GetRequiredService<OpenSessionStore>(), _hub);

        await live.StartAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });

        // Con el tope en 2 s, esto tiene que haber terminado muchísimo antes de agotar la espera.
        // Sin el reloj, `IsRunning` se queda en true para siempre: eso es el defecto.
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        live.IsRunning.Should().BeFalse(
            "una sesión que no puede publicar termina; quedarse girando es el defecto que se cierra");
        live.HasFailed.Should().BeTrue("y termina como lo que es: un fallo, no un final normal");
        live.FailureMessage.Should().Contain("No se pudo publicar en el hub",
            "el motivo se lee tal cual y dice dónde mirar; «se ha interrumpido por un error» no dice nada");
    }
}
