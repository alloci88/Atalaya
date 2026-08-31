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

/// <summary>
/// Suelta lo que una sesión anunciaba (BUGFIX-ACTIVIDAD).
/// <para>
/// Está aquí, y en un solo sitio, porque los claims son <b>la</b> fuente de «alguien está
/// auditando esto»: el Portafolio no tiene otra. Antes solo los soltaba el cierre ordenado del
/// coordinador, así que cualquier final que no pasara por ahí —una excepción del proveedor, por
/// ejemplo— dejaba a la tarjeta anunciando actividad hasta que caducara el TTL. Y la marca de
/// sesión abierta se borraba igualmente en el <c>finally</c>, con lo que la recuperación del
/// arranque tampoco encontraba nada que limpiar.
/// </para>
/// </summary>
public static class SessionClaims
{
    /// <summary>Suelta los claims de esas unidades. Devuelve cuántos se soltaron.</summary>
    public static int Release(HubContext hub, string slug, IEnumerable<string> units)
    {
        int released = 0;
        foreach (string unit in units)
        {
            try
            {
                hub.Store.DeleteClaim(slug, HashUtil.UnitHash(unit));
                released++;
            }
            catch (Exception)
            {
                // Un claim que no se puede borrar caduca solo; seguimos con los demás.
            }
        }

        return released;
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
/// Lo que la aplicación limpió al arrancar (BUGFIX-ACTIVIDAD). Son DOS cosas distintas y por eso
/// van separadas:
/// <list type="bullet">
/// <item><see cref="Session"/> — había una marca de sesión abierta de un proceso que ya no está:
/// se cierra como interrumpida, con su registro. Es el caso de D-110.</item>
/// <item><see cref="OrphanClaims"/> — había claims de ESTA máquina sin ninguna marca detrás. Eso
/// no puede recuperarse como sesión (no hay identidad que registrar), pero sí soltarse: son
/// nuestros y no hay nada corriendo. Es lo que dejaba al Portafolio anunciando «auditando ahora»
/// después de reiniciar.</item>
/// </list>
/// </summary>
public sealed record StartupCleanup(RecoveredSession? Session, int OrphanClaims, IReadOnlyList<string> Apps)
{
    public bool DidSomething => Session is not null || OrphanClaims > 0;

    /// <summary>Lo que se le cuenta al usuario. Null cuando no hubo nada que limpiar.</summary>
    public string? Message
    {
        get
        {
            if (Session is { } recovered)
            {
                return OrphanClaims > 0
                    ? recovered.Message + $" Y se soltaron {OrphanClaims} más que quedaron sueltos."
                    : recovered.Message;
            }

            if (OrphanClaims == 0)
            {
                return null;
            }

            string apps = Apps.Count == 1 ? $"«{Apps[0]}»" : $"{Apps.Count} aplicaciones";
            return $"Se soltaron {OrphanClaims} claim(s) de una sesión de esta máquina que no llegó a "
                + $"cerrarse: {apps} ya no aparece(n) como «auditando ahora».";
        }
    }
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
    /// Lo que hay que limpiar al arrancar: la marca de sesión abierta que dejó un proceso muerto
    /// <b>y</b> los claims de esta máquina que se quedaron sueltos sin marca detrás.
    /// <para>
    /// Si hay otra instancia de Atalaya auditando en esta misma máquina, no se toca NADA: ni su
    /// marca ni sus claims, que son justamente los que está usando.
    /// </para>
    /// </summary>
    public StartupCleanup CleanUpAtStartup()
    {
        OpenSessionMarker? marker = _store.TryRead();
        if (marker is not null && _isAlive(marker.ProcessId, marker.ProcessStartedUtc))
        {
            return new StartupCleanup(null, 0, Array.Empty<string>());
        }

        RecoveredSession? recovered = marker is null ? null : Recover(marker);
        (int orphans, IReadOnlyList<string> apps) = ReleaseOwnOrphanClaims();

        if (orphans > 0)
        {
            _hub.Sync?.CommitAndPush(
                $"claims: sueltos de {Environment.MachineName} liberados al arrancar ({orphans})");
        }

        return new StartupCleanup(recovered, orphans, apps);
    }

    /// <inheritdoc cref="CleanUpAtStartup"/>
    /// <remarks>Se conserva el nombre viejo: la marca sigue siendo la mitad importante del trabajo.</remarks>
    public RecoveredSession? RecoverIfNeeded() => CleanUpAtStartup().Session;

    /// <summary>
    /// Los claims que dejó ESTA máquina y que ya no tienen nada detrás (BUGFIX-ACTIVIDAD).
    /// <para>
    /// Es el caso que la recuperación de D-110 no cubría: si la sesión murió por una excepción, el
    /// <c>finally</c> borraba la marca —y sin marca no hay nada que recuperar—, pero los claims se
    /// quedaban. Al arrancar no hay ningún proceso nuestro auditando (lo garantiza el guardia de
    /// arriba), así que un claim de esta máquina es basura por definición.
    /// </para>
    /// <para>
    /// <b>Solo los nuestros.</b> Los de otras máquinas no se tocan: no sabemos si están vivos, y
    /// borrar el claim de un compañero que sí está auditando es exactamente el fallo que los
    /// claims existen para evitar. Para ésos, el margen de lectura de
    /// <see cref="ClaimRules.MaxSilence"/> hace que dejen de anunciarse sin borrar nada.
    /// </para>
    /// </summary>
    private (int Released, IReadOnlyList<string> Apps) ReleaseOwnOrphanClaims()
    {
        string machine = Environment.MachineName;
        int released = 0;
        var apps = new List<string>();

        try
        {
            foreach (string slug in _hub.Store.ListAppSlugs())
            {
                var mine = _hub.Store.ListClaims(slug)
                    .Where(c => string.Equals(c.Machine, machine, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (mine.Count == 0)
                {
                    continue;
                }

                released += SessionClaims.Release(_hub, slug, mine.Select(c => c.Unit));
                apps.Add(_hub.Store.TryReadApp(slug)?.Name ?? slug);
            }
        }
        catch (Exception)
        {
            // Un hub ilegible al arrancar no puede impedir arrancar. Lo que no se suelte aquí
            // deja de anunciarse igualmente por el margen de lectura.
        }

        return (released, apps);
    }

    private RecoveredSession Recover(OpenSessionMarker marker)
    {
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

        int released = SessionClaims.Release(_hub, marker.Slug, marker.Units);
        WriteSessionRecord(marker, findings, released);

        _store.Delete();
        _hub.Sync?.CommitAndPush(
            $"session: recuperada tras cierre forzado {marker.Slug} ({marker.UnitsDone} unidades)");

        return new RecoveredSession(marker.Slug, marker.SessionId, marker.UnitsDone, released, findings);
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
