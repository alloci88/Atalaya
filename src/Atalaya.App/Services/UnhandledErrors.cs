using System.Text;
using System.Windows.Threading;

namespace Atalaya.App.Services;

/// <summary>
/// <b>Una excepción que nadie recoge deja de matar la aplicación en silencio</b> (BUGFIX-F32).
/// <para>
/// <b>El defecto que cierra, medido.</b> D-802 puso un <c>try</c> alrededor del ARRANQUE porque
/// <c>OnStartup</c> es <c>async void</c> y un fallo ahí mataba el proceso sin dejar traza. Lo que
/// nadie puso fue el equivalente para todo lo demás: <b>después de arrancar no había ningún
/// manejador</b> — ni <c>DispatcherUnhandledException</c>, ni
/// <c>AppDomain.CurrentDomain.UnhandledException</c>, ni <c>TaskScheduler.UnobservedTaskException</c>—,
/// así que cualquier excepción no capturada en cualquier sitio cerraba Atalaya sin diálogo, sin
/// toast y sin una línea en el registro. Se vio con F32: el usuario pulsó un botón y la aplicación
/// desapareció; lo único que quedó fue un evento 1026 de .NET Runtime en el Visor de sucesos de
/// Windows, que es el sitio donde nadie mira.
/// </para>
/// <para>
/// <b>Es un defecto INDEPENDIENTE del que lo destapó.</b> Arreglar la excepción de F32 no habría
/// arreglado esto: la siguiente habría cerrado la aplicación igual. Por eso va aparte, con su
/// propia prueba.
/// </para>
/// <para>
/// <b>Qué se hace con cada una, que no es lo mismo.</b> Las del hilo de interfaz se pueden
/// <b>recoger</b>: se apuntan, se dicen y la aplicación sigue viva — perder la ventana es peor que
/// perder la operación—. Las de un hilo suelto no: el CLR ya ha decidido terminar cuando avisa, así
/// que lo único que se puede hacer es <b>dejar constancia</b> antes de que se apague la luz. Y una
/// tarea cuya excepción nadie observó se apunta y se marca observada, que es lo que evita que el
/// recolector la convierta en lo primero.
/// </para>
/// </summary>
public static class UnhandledErrors
{
    /// <summary>Lo que se le dice a quien está delante. El detalle va al registro.</summary>
    public const string UserMessage =
        "Atalaya ha tropezado con un error que no esperaba, pero sigue abierta. El detalle está en "
        + "%LOCALAPPDATA%\\Atalaya\\logs.";

    /// <summary>
    /// El parte de una excepción, para el registro: tipo, mensaje y pila, y lo mismo de cada
    /// excepción interna. Se escribe a mano y no con <c>ex.ToString()</c> porque lo que hace falta
    /// leer es el <b>tipo</b> y el <b>sitio</b> de cada capa, y una pila anidada sin encabezados se
    /// lee como un muro.
    /// </summary>
    public static string Describe(Exception? ex, string origin)
    {
        var sb = new StringBuilder();
        sb.Append("Excepción no capturada (").Append(origin).Append("): ");

        if (ex is null)
        {
            sb.Append("el origen no entregó ninguna excepción.");
            return sb.ToString();
        }

        for (Exception? layer = ex; layer is not null; layer = layer.InnerException)
        {
            sb.AppendLine();
            sb.Append(layer.GetType().FullName).Append(": ").Append(layer.Message);
            if (layer.StackTrace is { Length: > 0 } stack)
            {
                sb.AppendLine();
                sb.Append(stack);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// <b>La ventana en la que una excepción repetida deja de avisar</b> (BUGFIX-F36-1).
    /// <para>
    /// Diez segundos es un número elegido para el caso que lo destapó: una excepción de LAYOUT se
    /// repite en cada pasada de render, o sea decenas de veces por segundo. Cualquier ventana de más
    /// de un latido de render agrupa el bucle entero; diez deja además que dos pulsaciones distintas
    /// del mismo botón —que no son un bucle— avisen las dos.
    /// </para>
    /// </summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// La cuenta de lo que se calló, para el aviso que sí se enseña: «(y 22 repeticiones más del
    /// mismo error, que no se han avisado.)». Vacío cuando no hubo ninguna.
    /// </summary>
    internal static string RepeatSuffix(int repeats)
        => repeats <= 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine
              + (repeats == 1
                  ? "(Hubo 1 repetición más del mismo error, que no se avisó.)"
                  : $"(Hubo {repeats} repeticiones más del mismo error, que no se avisaron.)");

    /// <summary>
    /// Engancha el manejador del hilo de interfaz. Devuelve un testigo para soltarlo, que es lo que
    /// permite probarlo sobre un <see cref="Dispatcher"/> propio sin tocar el de la aplicación.
    /// </summary>
    /// <param name="report">Dónde se apunta. En producción, el registro.</param>
    /// <param name="show">
    /// Qué se le enseña al usuario. Opcional: en un test no hay a quién enseñárselo, y en
    /// producción es un cuadro de diálogo.
    /// </param>
    /// <param name="clock">El reloj de la agrupación. Existe para poder probarla sin esperar.</param>
    public static IDisposable Install(
        Dispatcher dispatcher,
        Action<string> report,
        Action<string>? show = null,
        Func<DateTimeOffset>? clock = null)
    {
        var repeats = new RepeatGate(RepeatWindow, clock ?? (() => DateTimeOffset.UtcNow));

        void OnDispatcher(object? _, DispatcherUnhandledExceptionEventArgs e)
        {
            (bool first, int swallowed) = repeats.Admit(e.Exception);

            if (first)
            {
                Safely(() => report(Describe(e.Exception, "hilo de interfaz")));
                Safely(() => show?.Invoke(UserMessage + RepeatSuffix(swallowed)));
            }
            else
            {
                // La repetición se apunta en UNA línea y sin pila: la pila ya está escrita arriba, y
                // veintitrés copias de la misma convierten el registro en un muro donde no se
                // encuentra la primera — que es la única que dice algo.
                Safely(() => report(Repeated(e.Exception, swallowed)));
            }

            // ESTO es lo que impide que la ventana desaparezca. Un error dentro de una operación no
            // puede llevarse por delante la aplicación entera: lo que se pierde es la operación, y
            // el usuario se entera de las dos cosas.
            e.Handled = true;
        }

        dispatcher.UnhandledException += OnDispatcher;
        return new Unsubscriber(() => dispatcher.UnhandledException -= OnDispatcher);
    }

    /// <summary>La línea de una repetición: qué fue y cuántas van. Sin pila.</summary>
    internal static string Repeated(Exception? ex, int count)
        => $"Excepción repetida (hilo de interfaz, nº {count}, misma pila): "
           + (ex is null ? "sin excepción" : $"{ex.GetType().FullName}: {ex.Message}");

    /// <summary>
    /// <b>Un bucle avisa UNA vez</b> (BUGFIX-F36-1).
    /// <para>
    /// <b>El defecto que lo trajo, medido.</b> Un estilo aplicado a un tipo que no era el suyo
    /// reventaba al COLOCAR, y colocar se reintenta en cada pasada de render: 23 excepciones en 460
    /// ms. Cada una abría su propio <c>MessageBox</c>, y un modal <b>bombea mensajes</b> — dentro de
    /// su bucle corría otra pasada de layout, que volvía a lanzar, que abría otro modal <b>sobre la
    /// misma pila</b>. Veintitrés bucles modales anidados agotaron la pila del hilo de interfaz y
    /// Windows mató el proceso con <c>0xC00000FD</c> (desbordamiento de pila). <b>Eso no lo puede
    /// contener ningún manejador gestionado</b>: cuando la pila se acaba, el CLR ni siquiera intenta
    /// llamar a nadie. Por eso el manejador «no contuvo» el final — no llegó a verlo.
    /// </para>
    /// <para>
    /// <b>La regla que queda</b>: un aviso por pila y por ventana. Lo que se agrupa es el AVISO, que
    /// es lo que tapa la pantalla y lo que anida bucles modales; el registro conserva la primera
    /// entera y una línea corta por repetición, así que no se pierde ni el diagnóstico ni la cuenta.
    /// </para>
    /// <para>
    /// La firma es <b>tipo + mensaje + pila</b>. La pila sola no basta —dos fallos distintos pueden
    /// romper en el mismo sitio— y el mensaje solo tampoco. No se guarda la excepción: solo su
    /// firma y su marca de tiempo, para que esto no retenga nada.
    /// </para>
    /// </summary>
    internal sealed class RepeatGate
    {
        private readonly TimeSpan _window;
        private readonly Func<DateTimeOffset> _clock;
        private readonly object _gate = new();

        private string? _signature;
        private DateTimeOffset _last;
        private int _count;

        public RepeatGate(TimeSpan window, Func<DateTimeOffset> clock)
        {
            _window = window;
            _clock = clock;
        }

        /// <summary>
        /// ¿Se avisa de ésta? <c>Repeats</c> es cuántas se callaron desde el último aviso: va en el
        /// aviso siguiente, para que una ráfaga no desaparezca sin dejar su cuenta.
        /// </summary>
        public (bool First, int Repeats) Admit(Exception? ex)
        {
            string signature = Signature(ex);
            DateTimeOffset now = _clock();

            lock (_gate)
            {
                bool same = signature == _signature && now - _last < _window;
                _signature = signature;
                _last = now;

                if (!same)
                {
                    int swallowed = _count;
                    _count = 0;
                    return (true, swallowed);
                }

                _count++;
                return (false, _count);
            }
        }

        private static string Signature(Exception? ex)
            => ex is null
                ? "(sin excepción)"
                : $"{ex.GetType().FullName}|{ex.Message}|{ex.StackTrace}";
    }

    /// <summary>
    /// Los dos que <b>no</b> se pueden recoger, solo apuntar. Se enganchan una vez, al arrancar, y
    /// no se sueltan: viven lo que vive el proceso.
    /// </summary>
    public static void InstallProcessWide(Action<string> report)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Safely(() => report(Describe(e.ExceptionObject as Exception, "hilo de fondo")));

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Safely(() => report(Describe(e.Exception, "tarea sin observar")));
            e.SetObserved();
        };
    }

    /// <summary>
    /// Apuntar no puede ser lo que mate el proceso. Si el registro falla —el disco lleno, el
    /// fichero bloqueado— se traga: estamos precisamente en el camino de los errores.
    /// </summary>
    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _release;

        public Unsubscriber(Action release) => _release = release;

        public void Dispose() => _release();
    }
}
