using System.Collections.Concurrent;
using System.Text;

namespace Atalaya.Agents;

/// <summary>
/// <b>El volcado de eventos crudos del proveedor</b>, para diagnosticar sin adivinar (F30 §2b, §2e).
/// <para>
/// Se enciende con la variable de entorno <c>ATALAYA_TRACE_EVENTS=1</c> y escribe una línea por
/// evento en <c>%LOCALAPPDATA%\Atalaya\logs\eventos-<i>casa</i>.log</c>: la hora con milésimas, el
/// tipo y lo que ese tipo tenga de interesante. Con eso se contesta la única pregunta que no se
/// puede contestar leyendo código —cuándo llega cada evento de verdad— y, desde §2e, la que la
/// motivó: si un hueco de ochenta segundos es el modelo callado o Atalaya sin leer.
/// </para>
/// <para>
/// <b>Y escribe en un hilo aparte, con búfer.</b> La primera versión hacía un
/// <c>File.AppendAllText</c> por evento —abrir, escribir, cerrar— desde el mismo hilo que consume
/// la salida del proveedor. Eso es exactamente lo que un lector no puede hacer: mientras abre un
/// fichero no lee la tubería, y un CLI que llena su tubería de salida <b>se bloquea escribiendo</b>
/// hasta que alguien lea. Un instrumento de diagnóstico que altere lo que mide no vale para nada.
/// Ahora la línea se encola —una cola sin bloqueo— y un hilo de fondo la vuelca con
/// <c>AutoFlush</c> apagado, en tandas.
/// </para>
/// <para>
/// <b>Apagado por defecto y sin ajuste en la interfaz.</b> Es un instrumento, no una función: una
/// casilla en Ajustes obligaría a explicarla, y un fichero que crece solo en la máquina de todo el
/// mundo es exactamente lo que nadie pidió. Una variable de entorno se pone para una sesión y se
/// olvida.
/// </para>
/// </summary>
public sealed class EventTrace
{
    /// <summary>Encendida o no, para todo el proceso. Se lee una vez: no cambia a mitad de sesión.</summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("ATALAYA_TRACE_EVENTS") is "1" or "true";

    private static readonly ConcurrentDictionary<string, EventTrace> Open = new(StringComparer.OrdinalIgnoreCase);

    private readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>());
    private readonly string _path;

    private EventTrace(string path)
    {
        _path = path;
        var pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "atalaya-traza-eventos",
        };
        pump.Start();
    }

    /// <summary>
    /// La traza de una casa (<c>claude</c>, <c>copilot</c>), o <c>null</c> si está apagada. Una
    /// sola por casa y por proceso: dos escritores sobre el mismo fichero se pisarían las líneas.
    /// </summary>
    public static EventTrace? For(string house)
    {
        if (!Enabled)
        {
            return null;
        }

        return Open.GetOrAdd(house, h => new EventTrace(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Atalaya", "logs", $"eventos-{h}.log")));
    }

    /// <summary>
    /// Apunta una línea. Vuelve inmediatamente: lo único que hace en el hilo que llama es sellar la
    /// hora y encolar. La hora se pone AQUÍ y no en el volcado, o la traza mediría cuándo se
    /// escribió el fichero en vez de cuándo llegó el evento — que es lo contrario de para lo que
    /// existe.
    /// </summary>
    public void Note(string text)
    {
        try
        {
            _lines.Add($"{DateTimeOffset.Now:HH:mm:ss.fff} {text}");
        }
        catch (InvalidOperationException)
        {
            // La cola se cerró: el proceso está terminando y ya no hay nada que apuntar.
        }
    }

    private void Pump()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var writer = new StreamWriter(
                new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1 << 16),
                new UTF8Encoding(false))
            {
                AutoFlush = false,
            };

            foreach (string line in _lines.GetConsumingEnumerable())
            {
                writer.WriteLine(line);

                // Se vuelca cuando la cola se queda vacía: en ráfaga, una escritura por tanda; en
                // silencio, la línea está en disco enseguida — que es cuando se quiere leer.
                if (_lines.Count == 0)
                {
                    writer.Flush();
                }
            }
        }
        catch (Exception)
        {
            // Un diagnóstico que tumbe la sesión que diagnostica no vale para nada.
        }
    }
}
