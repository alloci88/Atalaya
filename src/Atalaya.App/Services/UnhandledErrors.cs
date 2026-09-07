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
    /// Engancha el manejador del hilo de interfaz. Devuelve un testigo para soltarlo, que es lo que
    /// permite probarlo sobre un <see cref="Dispatcher"/> propio sin tocar el de la aplicación.
    /// </summary>
    /// <param name="report">Dónde se apunta. En producción, el registro.</param>
    /// <param name="show">
    /// Qué se le enseña al usuario. Opcional: en un test no hay a quién enseñárselo, y en
    /// producción es un cuadro de diálogo.
    /// </param>
    public static IDisposable Install(
        Dispatcher dispatcher, Action<string> report, Action<string>? show = null)
    {
        void OnDispatcher(object? _, DispatcherUnhandledExceptionEventArgs e)
        {
            Safely(() => report(Describe(e.Exception, "hilo de interfaz")));
            Safely(() => show?.Invoke(UserMessage));

            // ESTO es lo que impide que la ventana desaparezca. Un error dentro de una operación no
            // puede llevarse por delante la aplicación entera: lo que se pierde es la operación, y
            // el usuario se entera de las dos cosas.
            e.Handled = true;
        }

        dispatcher.UnhandledException += OnDispatcher;
        return new Unsubscriber(() => dispatcher.UnhandledException -= OnDispatcher);
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
