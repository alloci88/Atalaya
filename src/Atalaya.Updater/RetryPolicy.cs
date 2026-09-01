namespace Atalaya.Updater;

/// <summary>
/// Reintentar, con espera, lo que en un disco normal no falla nunca (F11 · BUGFIX-SYNC).
/// <para>
/// La razón es un vecino: con Atalaya instalada bajo <b>OneDrive</b> —o Dropbox, o Google Drive—
/// el cliente de sincronización mantiene manejadores abiertos sobre los ficheros mientras los
/// sube, y un movimiento masivo se topa con «Access denied» en cualquiera de ellos. No es un
/// problema de permisos: es un bloqueo que <b>se suelta solo en segundos</b>. Rendirse al primer
/// intento convierte una espera de dos segundos en una actualización que no se puede hacer.
/// </para>
/// <para>
/// Las esperas son cortas al principio y largas al final —200 ms, 400, 800, 1,6 s, 3 s: seis
/// intentos en poco más de seis segundos—. El caso normal no espera nada; el caso malo espera lo
/// que tarda un cliente de sincronización en soltar un fichero, y no más: pasado eso, el aborto
/// limpio de siempre, que sigue siendo lo correcto.
/// </para>
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>Lo que se espera entre intentos, en milisegundos. Seis intentos, ~6 s en total.</summary>
    public static readonly int[] DefaultWaitsMs = { 200, 400, 800, 1_600, 3_000 };

    /// <summary>La de producción. Se comparte porque no tiene estado.</summary>
    public static readonly RetryPolicy Default = new(DefaultWaitsMs);

    /// <summary>Ninguna espera y un solo intento: para quien no puede permitirse esperar.</summary>
    public static readonly RetryPolicy None = new(Array.Empty<int>());

    private readonly int[] _waitsMs;

    /// <summary>
    /// La espera, sustituible SOLO por los tests. Un test que esperase de verdad seis segundos
    /// para comprobar que se rinde estaría probando <c>Thread.Sleep</c>; y para comprobar que un
    /// bloqueo transitorio se supera, soltarlo justo aquí es determinista, mientras que soltarlo
    /// con un temporizador es una moneda al aire.
    /// </summary>
    private readonly Action<int>? _wait;

    public RetryPolicy(IReadOnlyList<int> waitsMs)
        : this(waitsMs, null)
    {
    }

    internal RetryPolicy(IReadOnlyList<int> waitsMs, Action<int>? wait)
    {
        _waitsMs = waitsMs.ToArray();
        _wait = wait;
    }

    /// <summary>Cuántas veces se intenta en total, contando la primera.</summary>
    public int Attempts => _waitsMs.Length + 1;

    /// <summary>
    /// Hace la operación, reintentándola mientras el fallo sea de los que se sueltan solos.
    /// Agotados los intentos, la excepción del último sale tal cual: quien llama decide qué
    /// hacer con ella, y aquí no se disfraza un fallo real de éxito.
    /// </summary>
    public void Do(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < _waitsMs.Length && IsTransient(ex))
            {
                Wait(_waitsMs[attempt]);
            }
        }
    }

    /// <summary>Como <see cref="Do"/>, pero para lo que es cortesía y no puede bloquear nada.</summary>
    public bool Try(Action action)
    {
        try
        {
            Do(action);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Borra la carpeta entera. Que no esté ya es el resultado que se buscaba.</summary>
    public void Delete(string dir) => Do(() =>
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Alguien se nos adelantó: es exactamente lo que queríamos.
        }
    });

    public bool TryDelete(string dir) => Try(() => Delete(dir));

    /// <summary>Renombra, sea fichero o carpeta. Mismo volumen: es instantáneo.</summary>
    public void Move(string from, string to) => Do(() =>
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite: false);
        }
    });

    /// <summary>
    /// ¿Es de los fallos que se sueltan solos? Un bloqueo de un cliente de sincronización llega
    /// como <see cref="IOException"/> («lo está usando otro proceso») o como
    /// <see cref="UnauthorizedAccessException"/> («acceso denegado») según qué manejador tenga
    /// abierto y sobre qué. Los dos, entonces — y el precio de equivocarse es esperar seis
    /// segundos antes de dar el mismo error que se habría dado al instante.
    /// </summary>
    internal static bool IsTransient(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private void Wait(int milliseconds)
    {
        if (_wait is not null)
        {
            _wait(milliseconds);
            return;
        }

        Thread.Sleep(milliseconds);
    }
}
