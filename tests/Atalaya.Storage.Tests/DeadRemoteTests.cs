using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Atalaya.Storage.Sync;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// <b>BUGFIX-PUSH — un hub que no contesta no cuelga a nadie.</b>
/// <para>
/// <b>El parte, con la pila delante.</b> Una sesión no arrancaba y «Detener» no respondía. El hilo
/// de interfaz estaba <b>libre</b>, en su bucle de mensajes; el de la sesión llevaba más de un
/// minuto dentro de <c>LibGit2Sharp.Network.Push</c>, llamado desde
/// <c>SessionCoordinator.PublishClaims</c> — la primera cosa que hace una sesión. libgit2 no le
/// pone reloj a su transporte: una conexión que se queda a medias espera <b>indefinidamente</b>,
/// sin excepción, sin traza y sin punto de cancelación al que llegar. De ahí las dos mitades del
/// síntoma: la ventana respondía y la sesión no avanzaba.
/// </para>
/// <para>
/// <b>Por qué el remoto es un socket mudo y no un <c>--bare</c> local.</b> N-1 pide probar el sync
/// de verdad y sin red, y un remoto local cumple las dos cosas — pero contesta <b>al instante</b>,
/// así que no puede reproducir lo único que importa aquí, que es la <b>ausencia</b> de respuesta.
/// Este escucha en <c>127.0.0.1</c>, acepta la conexión y no dice nunca nada: es un remoto que no
/// contesta, sigue sin salir de la máquina, y es la forma exacta del cuelgue reportado.
/// </para>
/// <para>
/// <b>El hilo huérfano se queda dentro de libgit2</b>, aquí y en producción: no hay forma de
/// abortarlo. Por eso es de fondo —no puede mantener vivo el proceso— y por eso deja la puerta
/// echada, que es lo que impide que una segunda publicación entre a tocar el mismo repositorio.
/// </para>
/// </summary>
public sealed class DeadRemoteTests : IDisposable
{
    private readonly TcpListener _mute;
    private readonly CancellationTokenSource _accepting = new();
    private readonly TempRepo _repo = new();
    private readonly List<TcpClient> _held = new();

    public DeadRemoteTests()
    {
        _mute = new TcpListener(IPAddress.Loopback, 0);
        _mute.Start();
        _ = Task.Run(async () =>
        {
            while (!_accepting.IsCancellationRequested)
            {
                try
                {
                    // Se acepta y no se contesta NUNCA. Mantener el cliente vivo es lo que hace que
                    // libgit2 se quede esperando en vez de ver la conexión cerrada y fallar.
                    _held.Add(await _mute.AcceptTcpClientAsync(_accepting.Token));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
            }
        });
    }

    private string MuteRemote
        => $"http://127.0.0.1:{((IPEndPoint)_mute.LocalEndpoint).Port}/hub.git";

    public void Dispose()
    {
        _accepting.Cancel();
        foreach (TcpClient client in _held.ToList())
        {
            client.Dispose();
        }

        _mute.Stop();
        _accepting.Dispose();
        _repo.Dispose();
    }

    /// <summary>
    /// La regla entera: <b>una publicación que no responde vuelve dentro del reloj y dice por
    /// qué</b>. Sin esto, quien la llama se queda dentro para siempre — que es lo que pasó.
    /// </summary>
    [Fact]
    public void Un_push_que_no_contesta_vuelve_dentro_del_tope_y_con_su_motivo()
    {
        (HubSyncService sync, HubPaths paths) = CloneAgainstMuteRemote();
        File.WriteAllText(Path.Combine(paths.Root, "hub.json"), "{}");

        var clock = Stopwatch.StartNew();
        bool published = sync.CommitAndPush(
            "claims: alguien 1 unidades en app", TimeSpan.FromSeconds(2), CancellationToken.None);
        clock.Stop();

        published.Should().BeFalse("el hub no contestó, así que no se publicó nada");

        // LAS DOS COTAS, y las dos hacen falta. La de abajo es la que da valor a la de arriba: si
        // libgit2 fallara rápido contra este remoto, el test pasaría sin haber reproducido nada y
        // seguiría en verde el día que alguien quite el reloj. Que haya AGOTADO el tope es la prueba
        // de que el otro lado de verdad no contesta.
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5),
            "si esto volviera en el acto, el remoto no estaría colgando y no habría nada que probar");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "el tope eran 2 s: sin reloj esto no vuelve nunca, que es el defecto que se cierra");
        sync.LastError.Should().NotBeNullOrWhiteSpace("un fallo sin motivo no se puede enseñar");
        sync.Health.Should().Be(SyncHealth.Red);
    }

    /// <summary>
    /// Y la segunda mitad, que es de seguridad y no de comodidad: mientras la publicación vencida
    /// <b>sigue viva</b> dentro de libgit2, la siguiente <b>no entra</b>. <c>Repository</c> no es
    /// seguro entre hilos, así que dejarla pasar sería corromper el clon — y encima volvería a
    /// colgarse. Falla en el acto y con su motivo.
    /// </summary>
    [Fact]
    public void Mientras_la_anterior_sigue_dentro_la_siguiente_no_entra_a_la_vez()
    {
        (HubSyncService sync, HubPaths paths) = CloneAgainstMuteRemote();
        File.WriteAllText(Path.Combine(paths.Root, "hub.json"), "{}");

        sync.CommitAndPush("primera", TimeSpan.FromSeconds(2), CancellationToken.None)
            .Should().BeFalse();

        var clock = Stopwatch.StartNew();
        bool second = sync.CommitAndPush("segunda", TimeSpan.FromSeconds(30), CancellationToken.None);
        clock.Stop();

        second.Should().BeFalse();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "la segunda no espera al reloj: ve la puerta echada y vuelve en el acto");
        sync.LastError.Should().Contain("anterior");
    }

    /// <summary>El clon local ya existe; lo único que apunta al vacío es el remoto.</summary>
    private (HubSyncService Sync, HubPaths Paths) CloneAgainstMuteRemote()
    {
        var paths = new HubPaths(_repo.NewClonePath("mudo"));
        var sync = new HubSyncService(paths, ("alguien", "alguien@example.com"));
        sync.EnsureCloned(_repo.BareRemotePath);

        // Y ahora el origen apunta al vacío. `EnsureCloned` sobre un clon que ya existe reapunta el
        // remoto, que es justo lo que hace falta: el clon es de verdad y lo único que no contesta es
        // el otro lado.
        sync.EnsureCloned(MuteRemote);
        return (sync, paths);
    }
}
