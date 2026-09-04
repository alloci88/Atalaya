using Atalaya.App.Services;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-RELEASE §1 — La conversación del arreglo asistido no puede reventar por reentrada.
/// <para>
/// <b>El defecto.</b> Todo lo que escribe en <c>Conversation</c> pasa por <c>OnUi</c>, y <c>OnUi</c>
/// ejecuta <b>en línea</b> cuando ya está en el hilo bueno (o cuando no hay <c>Application</c>).
/// Si alguien reacciona a un cambio de la colección escribiendo en ella —directamente, o soltando
/// una continuación que lo hace—, el segundo <c>Add</c> entra mientras el primero todavía está
/// repartiendo su <c>CollectionChanged</c> y <c>ObservableCollection</c> lanza
/// «Cannot change ObservableCollection during a CollectionChanged event».
/// </para>
/// <para>
/// <b>Por qué es intermitente y no constante.</b> El guardia de <c>ObservableCollection</c> solo
/// salta cuando hay <b>más de un</b> suscriptor, y la ventana dura lo que dure el reparto del
/// evento. Con un suscriptor, o si la continuación llega tarde, pasa. Por eso caía una de cada
/// tres publicaciones y no todas.
/// </para>
/// <para>
/// Este test no espera a que el tiempo lo produzca: <b>provoca</b> la llamada anidada.
/// </para>
/// </summary>
public sealed class LiveFixReentrancyTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly FixSnapshotStore _snapshots;
    private readonly AgentBusyGate _busy = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public LiveFixReentrancyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-reentrada", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = TestFactory.Settings(_paths);
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _snapshots = new FixSnapshotStore(_paths);
    }

    /// <summary>
    /// La sesión no está corriendo, que es todo lo que hace falta: <c>SendUserMessageAsync</c>
    /// escribe en la conversación de forma SÍNCRONA antes de su primer <c>await</c>, y eso es la
    /// escritura que hay que poder anidar sin romper nada.
    /// </summary>
    private LiveFixService Service()
        => new(
            _hub,
            () => null,
            _machines,
            _ulids,
            _settings,
            new ReferenceCollector(),
            _snapshots,
            new AssistedFixLauncher(_settings, new CloneLinkService(_hub, _machines), _machines, _busy),
            _busy);

    /// <summary>
    /// La regla: <b>escribir en la conversación desde un manejador de la propia conversación no
    /// puede tirar la sesión</b>, y lo escrito no se pierde — se entrega después, en orden.
    /// <para>
    /// Los dos suscriptores son parte del caso, no decorado: con uno solo,
    /// <c>ObservableCollection</c> no comprueba la reentrada y el fallo no se ve. En la aplicación
    /// hay siempre al menos uno (el <c>ItemsControl</c> de la vista) y basta con que alguien añada
    /// el segundo —una prueba, un panel nuevo— para que la sesión empiece a caerse.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Escribir_desde_un_manejador_de_la_conversacion_no_revienta_la_sesion()
    {
        LiveFixService fix = Service();

        // El segundo suscriptor: es lo que ARMA el guardia de ObservableCollection.
        fix.Conversation.CollectionChanged += (_, _) => { };

        bool reentered = false;
        Exception? fault = null;
        fix.Conversation.CollectionChanged += (_, _) =>
        {
            if (reentered)
            {
                return;
            }

            reentered = true;
            try
            {
                // Reentrada de verdad: mismo hilo, dentro del reparto del evento. La sesión no
                // corre, así que este método entero es síncrono y el fallo, si lo hay, sale aquí.
                fix.SendUserMessageAsync("y además esto").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Se recoge en vez de dejarlo volar: en la aplicación esta llamada viaja dentro de
                // un Task que nadie observa, así que la sesión se queda muda en lugar de dar la
                // cara. El test SÍ la da.
                fault = ex;
            }
        };

        await fix.SendUserMessageAsync("primero");

        reentered.Should().BeTrue("el test no vale si no llegó a reentrar");
        fault.Should().BeNull("escribir en la conversación desde un manejador suyo no puede lanzar");
        fix.Conversation.OfType<FixMessage>().Select(m => m.Text)
            .Should().Contain("primero").And.Contain("y además esto");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Un temporal que Windows todavía tiene abierto no es un fallo del test.
        }
    }
}
