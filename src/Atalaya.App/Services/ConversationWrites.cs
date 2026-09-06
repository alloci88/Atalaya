using System.Windows;
using System.Windows.Threading;

namespace Atalaya.App.Services;

/// <summary>
/// <b>Una escritura a la vez en la conversación</b> (BUGFIX-RELEASE §1, generalizado en F30 §3).
/// <para>
/// <b>El defecto que cierra.</b> Marshalear al hilo de interfaz ejecuta <b>en línea</b> cuando ya
/// se está en el hilo bueno —y siempre, cuando no hay <c>Application</c>—. Así que quien reaccione
/// a un cambio de la colección escribiendo en ella, directamente o soltando una continuación que lo
/// haga, entra en el segundo <c>Add</c> mientras el primero todavía reparte su
/// <c>CollectionChanged</c>, y <c>ObservableCollection</c> lanza «Cannot change ObservableCollection
/// during a CollectionChanged event». Con eso muere la sesión entera, en medio del trabajo.
/// </para>
/// <para>
/// <b>Por qué aquí y no difiriendo siempre al dispatcher.</b> Un <c>BeginInvoke</c> incondicional no
/// arregla el caso sin <c>Application</c> —no hay a quién diferir— y cambiaría el orden de todo lo
/// demás. La regla que hace falta es más pequeña y es la de verdad: <b>una escritura a la vez, y las
/// que lleguen mientras tanto van detrás, en orden</b>. La reentrada deja de ser reentrada y pasa a
/// ser el siguiente elemento de la cola.
/// </para>
/// <para>
/// <b>Por qué es una pieza y no un método privado.</b> Lo era del arreglo asistido desde
/// BUGFIX-RELEASE, y la sesión en vivo escribe en la misma clase de colección desde el mismo hilo de
/// fondo: dos conversaciones con la misma forma y una sola con la red puesta. Ahora las dos vistas
/// escriben por aquí, incluidos el volcado diferido del texto (F30 §2d) y las colas de §2e.
/// </para>
/// <para>
/// El precio, dicho: quien escribe desde dentro de otra escritura <b>vuelve antes de que lo suyo
/// esté puesto</b>. Es correcto —lo estará al acabar la de fuera, que es inmediatamente después— y
/// es la única ventana en la que ocurre.
/// </para>
/// </summary>
public sealed class ConversationWrites
{
    private readonly object _gate = new();

    private readonly Queue<Action> _queued = new();

    private bool _writing;

    /// <summary>
    /// Marshalea al hilo de interfaz <b>esperando</b> y escribe. Es el camino de los eventos que no
    /// vienen del hilo que lee al proveedor —cerrar una pasada, terminar una unidad—:
    /// <c>Dispatcher.Invoke</c> encola en <c>Send</c>, la prioridad más alta, que es la misma en la
    /// que entra <see cref="Post"/> para que haya <b>una sola cola</b> servida en orden.
    /// </summary>
    public void Send(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Run(action));
            return;
        }

        Run(action);
    }

    /// <summary>
    /// Como <see cref="Send"/> pero <b>sin esperar</b> (F30 §2d): se deja puesto y se vuelve.
    /// <para>
    /// Por aquí pasa todo lo que emite el hilo que lee al proveedor: el texto, el consumo y las
    /// líneas del hilo. Ese hilo consume la tubería de salida de un proceso vivo, y un proceso cuya
    /// tubería se llena <b>se bloquea escribiendo</b> hasta que alguien lea. La regla es: lee,
    /// encola y sigue.
    /// </para>
    /// </summary>
    public void Post(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Run(action);
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Run(action)));
    }

    /// <summary>Ejecuta una escritura, o la <b>encola</b> si ya hay otra en vuelo.</summary>
    private void Run(Action action)
    {
        lock (_gate)
        {
            _queued.Enqueue(action);
            if (_writing)
            {
                // Ya hay alguien drenando: se lleva ésta también y volvemos sin tocar nada.
                return;
            }

            _writing = true;
        }

        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (_queued.Count == 0)
                {
                    _writing = false;
                    return;
                }

                next = _queued.Dequeue();
            }

            try
            {
                next();
            }
            catch
            {
                // Se suelta el turno para que lo que quede en la cola lo drene el siguiente que
                // escriba: una excepción dentro de una escritura no puede dejar la conversación
                // muda para siempre.
                lock (_gate)
                {
                    _writing = false;
                }

                throw;
            }
        }
    }
}
