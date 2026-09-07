using System.Diagnostics;
using System.IO;
using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Inventory;
using Atalaya.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.Carga;

/// <summary>El ritmo con el que un sesionista trabaja. Es lo que hace que se pisen o no.</summary>
public enum Ritmo
{
    /// <summary>Publica cada pocos segundos: el que más veces choca con los demás.</summary>
    Rapido,

    /// <summary>Sesiones largas y pausas largas: el que llega tarde y se encuentra el hub movido.</summary>
    Lento,

    /// <summary>Se cae a mitad de una sesión, sin cerrarla y sin soltar sus reclamaciones.</summary>
    QueSeCae,
}

/// <summary>
/// Una persona del equipo: su clon, su hub, su coordinador y su agente falso. Todo separado de los
/// demás salvo lo único que se comparte, que es el <c>--bare</c>.
/// </summary>
public sealed class Sesionista
{
    private readonly string _raiz;
    private readonly string _clon;
    private readonly string _bare;
    private readonly string _slug;
    private readonly Guion.Libreto _libreto;

    public Sesionista(string nombre, Ritmo ritmo, string raiz, string bare, string slug)
    {
        Nombre = nombre;
        Ritmo = ritmo;
        _raiz = raiz;
        _bare = bare;
        _slug = slug;
        _clon = Path.Combine(raiz, "clon");
        _libreto = new Guion.Libreto(nombre);
        Medidas = new Medidas(nombre, ritmo);
    }

    public string Nombre { get; }

    public Ritmo Ritmo { get; }

    public Medidas Medidas { get; }

    /// <summary>Escupe lo que dice el agente falso, incluido POR QUE se rechaza un hallazgo.</summary>
    public static bool Verboso { get; set; }

    /// <summary>
    /// Monta la máquina de esta persona: ajustes, hub apuntado al <c>--bare</c> compartido, clon
    /// de mentira con su código, y el coordinador de verdad con el agente falso.
    /// </summary>
    private (HubContext Hub, SessionCoordinator Coordinador, string[] Unidades) Montar()
    {
        Directory.CreateDirectory(_raiz);
        var paths = new AppPaths(_raiz);
        var settings = new SettingsService(paths);
        settings.Load();

        // ASÍ ES COMO SE APUNTA AL BANCO Y NO AL HUB DE VERDAD (N-1): el `--bare` de la tanda entra
        // por el mismo sitio por el que un usuario pondría otro hub, así que no hay una segunda
        // ruta de configuración que pudiera divergir de la que se usa.
        settings.Current.HubUrlOverride = _bare;

        // CADA UNA CON SU NOMBRE. Sin esto las tres publican con la identidad de git de la maquina
        // y el parte no puede decir que llego al hub DE CADA UNA, que es la mitad de lo que se
        // viene a medir.
        settings.Current.GitUserName = Nombre;
        settings.Current.GitUserEmail = Nombre.Replace(' ', '.').ToLowerInvariant() + "@example.invalid";
        settings.Save(settings.Current);

        var account = new GitHubAccountService(new AccountStore(paths), SystemClock.Instance);
        var hub = new HubContext(paths, settings, account, new DeployConfig(), NullLoggerFactory.Instance);
        hub.EnsureHub();

        string[] unidades = Guion.EscribirCodigo(_clon, 8);

        var machines = new MachineConfigStore(paths.MachinesJson);
        machines.SetClonePath(_slug, _clon);

        var ulids = new UlidFactory(SystemClock.Instance);
        var agente = new FakeCopilotAgent(
            auditScript: _libreto.Auditar,
            modelName: "banco-de-carga");

        if (Verboso)
        {
            agente.TextStreamed += t => Console.Write($"[{Nombre}] {t}");
        }

        var coordinador = new SessionCoordinator(
            hub,
            new FindingIngestionService(hub, ulids),
            new ReconciliationService(hub),
            machines, ulids, agente, settings);

        // Lo que hay que mirar mientras pasa: cada reintento de publicación y cada conflicto que
        // el rebase tuvo que resolver. Los dos son invisibles si nadie se suscribe.
        if (hub.Sync is { } sync)
        {
            sync.PushRetrying += _ => Medidas.Reintento();
            sync.Published += (_, cuanto) => Medidas.Publicacion(cuanto);
        }

        hub.Changed += r => Medidas.Conflictos(r.Notifications);

        return (hub, coordinador, unidades);
    }

    /// <summary>
    /// Trabaja hasta que se acabe el tiempo, con el ritmo que le toque. Nunca lanza: una persona
    /// que revienta es un dato de la tanda, no el final de la tanda.
    /// </summary>
    public async Task TrabajarAsync(DateTimeOffset hasta, CancellationToken ct)
    {
        HubContext hub = null;
        try
        {
            (HubContext h, SessionCoordinator coordinador, string[] unidades) = Montar();
            hub = h;

            int vuelta = 0;
            while (DateTimeOffset.UtcNow < hasta && !ct.IsCancellationRequested)
            {
                vuelta++;

                // EL QUE SE CAE se cae a mitad: deja la sesión sin cerrar y sus reclamaciones en el
                // hub, que es exactamente lo que deja un cierre forzado (F5, §5).
                if (Ritmo == Ritmo.QueSeCae && vuelta == 2)
                {
                    Medidas.SeCayo();
                    return;
                }

                var cuantas = Ritmo == Ritmo.Lento ? 4 : 2;
                string[] loMio = unidades.Skip((vuelta * cuantas) % unidades.Length).Take(cuantas).ToArray();
                if (loMio.Length == 0)
                {
                    loMio = unidades.Take(cuantas).ToArray();
                }

                var reloj = Stopwatch.StartNew();
                try
                {
                    SessionResult resultado = await coordinador
                        .RunAsync(new SessionRequest(_slug, AuditMode.Lotes, loMio), ct)
                        .ConfigureAwait(false);

                    Medidas.SesionTerminada(resultado.Counters.New, reloj.Elapsed);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Medidas.SesionRota(ex);
                }

                // El reloj del push, que es la cifra por la que existe la fase: cuánto llegó a
                // quedarse bloqueada una publicación.
                Medidas.Publicacion(MedirSincronizacion(hub));

                TimeSpan pausa = Ritmo switch
                {
                    Ritmo.Rapido => TimeSpan.FromSeconds(2),
                    Ritmo.Lento => TimeSpan.FromSeconds(12),
                    _ => TimeSpan.FromSeconds(5),
                };

                try
                {
                    await Task.Delay(pausa, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Medidas.SesionRota(ex);
        }
        finally
        {
            // `HubContext` no es desechable: lo que hay que soltar es el servicio de sincronizacion,
            // que es quien tiene el clon abierto.
            hub?.Sync?.Dispose();
        }
    }

    /// <summary>Una sincronización cronometrada: es donde se ve un push que no vuelve.</summary>
    private static TimeSpan MedirSincronizacion(HubContext hub)
    {
        var reloj = Stopwatch.StartNew();
        try
        {
            hub.SyncNow();
        }
        catch
        {
            // Lo que falla se cuenta por la salud, no por una excepción que pare la tanda.
        }

        return reloj.Elapsed;
    }
}
