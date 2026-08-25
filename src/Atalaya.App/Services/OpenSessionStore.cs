using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atalaya.Domain;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Marca de sesión ABIERTA (F5.2 Hito 3, cierre de D-110).
/// <para>
/// La parada ordenada de F5.1b cubre el cierre cooperativo, pero no que el proceso muera de golpe
/// (kill, cuelgue, corte de luz). Como la ingesta persiste los hallazgos EN VIVO, un corte así
/// dejaba el hub mutado sin registro de sesión y con los claims retenidos hasta el TTL.
/// </para>
/// <para>
/// Vive en <c>%LOCALAPPDATA%</c> y NO en el hub: es un detalle de esta máquina y de este proceso,
/// no un hecho compartido del equipo. Publicarlo haría que la marca de una máquina apagada
/// pareciera una sesión viva para todas las demás.
/// </para>
/// </summary>
public sealed class OpenSessionMarker
{
    public required string SessionId { get; set; }

    public required string Slug { get; set; }

    public string Mode { get; set; } = nameof(AuditMode.Lotes);

    public string Commit { get; set; } = string.Empty;

    public string By { get; set; } = string.Empty;

    public string Machine { get; set; } = string.Empty;

    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>Unidades reclamadas por la sesión: son los claims que hay que liberar al recuperar.</summary>
    public List<string> Units { get; set; } = new();

    /// <summary>Unidades ya cerradas cuando se escribió la marca por última vez.</summary>
    public int UnitsDone { get; set; }

    public int ProcessId { get; set; }

    /// <summary>
    /// Instante de arranque del proceso. Sin esto, un PID reutilizado por otro programa haría
    /// pasar por viva una sesión muerta y la recuperación no ocurriría nunca.
    /// </summary>
    public DateTimeOffset? ProcessStartedUtc { get; set; }
}

/// <summary>Lee y escribe la marca. Nunca lanza: una marca corrupta es como no tener marca.</summary>
public sealed class OpenSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public OpenSessionStore(AppPaths paths) => _path = Path.Combine(paths.Root, "open-session.json");

    public string Path_ => _path;

    public void Write(OpenSessionMarker marker)
    {
        try
        {
            marker.ProcessId = Environment.ProcessId;
            marker.ProcessStartedUtc ??= CurrentProcessStart();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(marker, JsonOptions));
        }
        catch (IOException)
        {
            // Perder la marca degrada la recuperación, no la sesión: nunca debe tumbar la auditoría.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public OpenSessionMarker? TryRead()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<OpenSessionMarker>(File.ReadAllText(_path), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // idem
        }
    }

    private static DateTimeOffset? CurrentProcessStart()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ¿Sigue vivo el proceso que dejó la marca? Comprueba PID <b>y</b> instante de arranque: los
    /// PID se reciclan, y confundir un proceso nuevo con el nuestro dejaría la sesión sin recuperar
    /// para siempre.
    /// </summary>
    public static bool IsProcessAlive(int processId, DateTimeOffset? startedUtc)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (startedUtc is null)
            {
                return true;
            }

            DateTimeOffset actual = process.StartTime.ToUniversalTime();
            return Math.Abs((actual - startedUtc.Value).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;   // no existe ese PID
        }
        catch (InvalidOperationException)
        {
            return false;   // ya ha terminado
        }
        catch
        {
            // Sin permiso para inspeccionarlo: asumimos vivo. Recuperar una sesión que en realidad
            // sigue corriendo sería peor que no recuperarla.
            return true;
        }
    }
}

/// <summary>Qué hizo la recuperación, para poder contarlo en un toast.</summary>
public sealed record RecoveredSession(string Slug, string SessionId, int UnitsDone, int ClaimsReleased, int Findings)
{
    public string Message =>
        $"Se recuperó una sesión interrumpida: {UnitsDone} unidad(es) procesadas antes del corte"
        + (ClaimsReleased > 0 ? $", {ClaimsReleased} claim(s) liberado(s)" : "")
        + ".";
}

/// <summary>
/// Cierra al arrancar la sesión que se quedó abierta porque el proceso murió de golpe (D-110).
/// <para>
/// No inventa nada: los hallazgos ya están escritos en disco (ingesta en vivo). Lo que faltaba era
/// el <b>registro</b> de la sesión, la liberación de los claims y decírselo al usuario.
/// </para>
/// </summary>
public sealed class InterruptedSessionRecovery
{
    private readonly HubContext _hub;
    private readonly OpenSessionStore _store;
    private readonly Func<int, DateTimeOffset?, bool> _isAlive;

    public InterruptedSessionRecovery(
        HubContext hub, OpenSessionStore store, Func<int, DateTimeOffset?, bool>? isAlive = null)
    {
        _hub = hub;
        _store = store;
        _isAlive = isAlive ?? OpenSessionStore.IsProcessAlive;
    }

    /// <summary>
    /// Devuelve qué se recuperó, o null si no había nada que recuperar (ni marca, o la dejó un
    /// proceso que sigue vivo — otra instancia de Atalaya auditando ahora mismo).
    /// </summary>
    public RecoveredSession? RecoverIfNeeded()
    {
        OpenSessionMarker? marker = _store.TryRead();
        if (marker is null)
        {
            return null;
        }

        if (_isAlive(marker.ProcessId, marker.ProcessStartedUtc))
        {
            return null;   // hay otra instancia auditando: no se toca nada
        }

        // Los hallazgos creados por aquella sesión ya están en disco. No hay forma exacta de
        // atribuirlos (un hallazgo no guarda el ULID de su sesión), así que se cuentan los de esa
        // app detectados desde que arrancó: es una aproximación, y como tal se declara en la nota.
        int findings = 0;
        try
        {
            findings = _hub.Store.ListFindings(marker.Slug)
                .Count(f => f.FirstDetected.Utc >= marker.StartedUtc);
        }
        catch (Exception)
        {
            // Un hub ilegible no debe impedir liberar los claims ni avisar.
        }

        int released = ReleaseClaims(marker);
        WriteSessionRecord(marker, findings, released);

        _store.Delete();
        _hub.Sync?.CommitAndPush(
            $"session: recuperada tras cierre forzado {marker.Slug} ({marker.UnitsDone} unidades)");

        return new RecoveredSession(marker.Slug, marker.SessionId, marker.UnitsDone, released, findings);
    }

    private int ReleaseClaims(OpenSessionMarker marker)
    {
        int released = 0;
        foreach (string unit in marker.Units)
        {
            try
            {
                _hub.Store.DeleteClaim(marker.Slug, HashUtil.UnitHash(unit));
                released++;
            }
            catch (Exception)
            {
                // Un claim que no se puede borrar caduca solo por TTL; seguimos con los demás.
            }
        }

        return released;
    }

    private void WriteSessionRecord(OpenSessionMarker marker, int findings, int released)
    {
        try
        {
            if (!Ulid.TryParse(marker.SessionId, out Ulid id))
            {
                return;
            }

            var session = new AuditSession
            {
                Id = id,
                AppSlug = marker.Slug,
                Mode = Enum.TryParse(marker.Mode, out AuditMode mode) ? mode : AuditMode.Lotes,
                By = string.IsNullOrWhiteSpace(marker.By) ? Environment.UserName : marker.By,
                Machine = string.IsNullOrWhiteSpace(marker.Machine) ? Environment.MachineName : marker.Machine,
                StartedUtc = marker.StartedUtc,
                EndedUtc = DateTimeOffset.UtcNow,
                Commit = marker.Commit,
                Interrupted = true,
            };

            session.Notes.Add(
                "Sesión recuperada al arrancar: el proceso se cerró de golpe y no llegó a escribir su "
                + $"registro. {marker.UnitsDone} de {marker.Units.Count} unidad(es) se habían procesado "
                + $"antes del corte y {released} claim(s) se han liberado.");
            session.Notes.Add(
                $"Los {findings} hallazgo(s) de esta app detectados desde {marker.StartedUtc:yyyy-MM-dd HH:mm} UTC "
                + "ya estaban persistidos (la ingesta escribe en vivo). El recuento es aproximado: un "
                + "hallazgo no guarda el ULID de la sesión que lo creó.");

            _hub.Store.WriteSession(session);
        }
        catch (Exception)
        {
            // Recuperar es de mejor esfuerzo: si el hub no admite la escritura, al menos los claims
            // quedan liberados y el usuario recibe el aviso.
        }
    }
}
