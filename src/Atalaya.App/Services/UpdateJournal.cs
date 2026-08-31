using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atalaya.App.Services;

/// <summary>Cómo acabó un intento de actualización. Es lo que se lee en el registro.</summary>
public enum UpdateOutcome
{
    /// <summary>Ni se intentó: había una sesión en curso, es un build local, no hay cuenta…</summary>
    NoOfrecida,

    /// <summary>Se abortó antes de tocar la instalación (red, checksum, permisos).</summary>
    Abortada,

    /// <summary>
    /// Descargada, verificada y cedida al relevo. Es la última línea que puede escribir la
    /// versión vieja: el desenlace lo apunta la que arranque después. Una «Iniciada» sin
    /// desenlace detrás es, por sí sola, el diagnóstico de que el relevo nunca llegó a correr.
    /// </summary>
    Iniciada,

    /// <summary>La carpeta se sustituyó y la versión nueva arrancó.</summary>
    Completada,

    /// <summary>Falló a mitad y se restauró la versión anterior.</summary>
    Restaurada,
}

/// <param name="WhenUtc">Cuándo.</param>
/// <param name="From">Versión de origen.</param>
/// <param name="To">Versión de destino.</param>
/// <param name="Outcome">Cómo acabó.</param>
/// <param name="Detail">La causa, cuando no salió bien. Vacío cuando sí.</param>
public sealed record UpdateAttempt(
    DateTimeOffset WhenUtc, string From, string To, UpdateOutcome Outcome, string Detail = "");

/// <summary>
/// El registro de intentos de actualización (F11).
/// <para>
/// <b>Aparte del log general y en %LOCALAPPDATA%</b>, por dos razones. La primera es que
/// sobrevive: el log de Serilog también, pero este es corto y se lee de un vistazo cuando alguien
/// pregunta «¿por qué sigo en la 1.0.3?». La segunda es que una actualización cruza dos procesos
/// y dos versiones distintas del programa — el intento lo apunta la vieja y el desenlace lo
/// apunta la nueva—, así que el registro no puede vivir dentro de ninguna de las dos.
/// </para>
/// <para>
/// Una línea de JSON por intento (JSONL): añadir es abrir y escribir al final, sin releer ni
/// reescribir el fichero, que es lo que hace que un corte a mitad pierda como mucho la última
/// línea en vez del registro entero.
/// </para>
/// </summary>
public sealed class UpdateJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public UpdateJournal(AppPaths paths) => _path = paths.UpdatesLog;

    public UpdateJournal(string path) => _path = path;

    public string Path_ => _path;

    /// <summary>Apunta un intento. Nunca lanza: perder el registro no puede tumbar nada.</summary>
    public void Record(UpdateAttempt attempt)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.AppendAllText(
                    _path,
                    JsonSerializer.Serialize(attempt, JsonOptions) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Un registro que no se puede escribir no es motivo para no actualizar.
        }
    }

    /// <summary>Lo apuntado, de lo más antiguo a lo más reciente. Una línea rota se salta.</summary>
    public IReadOnlyList<UpdateAttempt> Read()
    {
        var attempts = new List<UpdateAttempt>();
        try
        {
            if (!File.Exists(_path))
            {
                return attempts;
            }

            foreach (string line in File.ReadAllLines(_path))
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<UpdateAttempt>(line, JsonOptions) is { } attempt)
                    {
                        attempts.Add(attempt);
                    }
                }
                catch (JsonException)
                {
                    // Una línea a medias (un corte durante la escritura) no invalida las demás.
                }
            }
        }
        catch (IOException)
        {
        }

        return attempts;
    }
}
