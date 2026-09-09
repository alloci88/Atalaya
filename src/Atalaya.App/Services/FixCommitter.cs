using System.Text;
using LibGit2Sharp;

namespace Atalaya.App.Services;

/// <summary>Lo que devuelve commitear un arreglo. Un fallo es un resultado, nunca una excepción.</summary>
/// <param name="Sha">El commit creado, corto, como el resto de hashes que se enseñan.</param>
/// <param name="Error">
/// El motivo, ya escrito para una persona. Cuando el fallo viene de git —un <c>pre-commit</c> que
/// devuelve 1— lleva pegada la COLA de su salida, que es donde el hook dice qué le ha molestado
/// (mismo criterio que el resumen de compilar, D-548).
/// </param>
/// <param name="Author">
/// <b>La identidad con la que git firmó el commit</b>, como «Nombre &lt;correo&gt;»
/// (BUGFIX-F32-2). Se lee del commit YA CREADO y no de la configuración: es lo único que
/// prueba con quién salió — la configuración se puede haber leído de otro sitio, o venir del
/// entorno—. Y se enseña <b>sin juzgarla</b>: Atalaya no tiene forma de saber si «Su Nombre»
/// es un marcador o el nombre de alguien.
/// </param>
public sealed record FixCommitResult(
    bool Ok, string? Sha = null, string? Error = null, string? Author = null);

/// <summary>
/// <b>git no hizo el commit, y su motivo tiene que llegar a la línea del paso</b>
/// (BUGFIX-F32). Viaja como excepción porque <see cref="StepList"/> pinta el fallo de un paso
/// leyendo <c>ex.Message</c>; no es un error de programa —un <c>pre-commit</c> que rechaza es
/// un desenlace normal— y por eso se recoge en el mismo método que la lanza.
/// </summary>
public sealed class FixCommitRejected : Exception
{
    public FixCommitRejected(string message) : base(message)
    {
    }
}

/// <summary>
/// Commitea EXACTAMENTE los ficheros de un arreglo en el clon del usuario (F32, que revoca
/// D-556).
/// <para>
/// <b>Por qué la línea de comandos de git y no LibGit2Sharp</b>, que es lo que usa todo lo demás
/// de esta casa. Porque los <b>hooks</b> tienen que correr: un repositorio corporativo con un
/// <c>pre-commit</c> que formatea o que rechaza un fichero sin licencia es exactamente el sitio
/// donde Atalaya se despliega, y libgit2 <b>no ejecuta hooks</b> — commitearía por debajo de la
/// política de la casa sin que nadie se enterara. Con el CLI se respetan, y si uno falla el commit
/// no se hace y el usuario lee lo que el hook le dijo. El precio, declarado: <b>hace falta
/// <c>git</c> en el PATH</b>; si no está, el botón falla con su motivo y el clon se queda como
/// estaba, que es el mismo desenlace que cualquier otro fallo.
/// </para>
/// <para>
/// <b>Y por qué <c>--only</c>.</b> Es la única forma de commitear unas rutas concretas con el
/// contenido que tienen en el árbol <b>sin arrastrar el índice del usuario</b>: lo que él tenga
/// preparado para otros ficheros no entra en este commit y sigue preparado después (D-684 — su
/// árbol es suyo). Un <c>git add</c> seguido de <c>git commit</c> se habría llevado por delante
/// todo lo que estuviera en el índice.
/// </para>
/// <para>
/// <b>Y por qué además hace falta añadir</b> (BUGFIX-F32-3). <c>--only</c> solo acepta rutas que
/// git YA conoce: un fichero <b>nuevo</b> —y el arreglo los crea, un test que cubre el defecto es
/// el caso normal— no lo es, y el commit muere con
/// <c>pathspec '…' did not match any file(s) known to git</c> sin llegar a intentarse. Así que los
/// ficheros del arreglo que git no conoce se añaden al índice <b>ellos solos</b>, uno a uno y por
/// su ruta, justo antes del commit; los que ya conoce siguen entrando por <c>--only</c>, sin tocar
/// el índice. <b>Nunca</b> un <c>add -A</c> ni un <c>add .</c>: eso es exactamente lo que
/// <c>--only</c> existe para evitar. Y si el commit falla después, <b>ese añadido se deshace</b>
/// —la regla de D-1034 incluye el índice: un fallo deja el clon como estaba, también preparado—.
/// </para>
/// </summary>
public sealed class FixCommitter
{
    /// <summary>
    /// El reloj (D-1022). Un <c>pre-commit</c> puede tardar —formatear una solución entera lo
    /// hace—, pero no puede colgar la aplicación para siempre: al vencer se mata el proceso y se
    /// dice. Aquí sí se puede matar de verdad, que es más de lo que se podía hacer con libgit2.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Líneas de la COLA de la salida de git que se conservan en el motivo del fallo.</summary>
    public const int TailLines = 25;

    private readonly IProcessRunner _runner;
    private readonly Func<TimeSpan> _timeout;

    public FixCommitter(IProcessRunner? runner = null, Func<TimeSpan>? timeout = null)
    {
        _runner = runner ?? new SystemProcessRunner();
        _timeout = timeout ?? (() => DefaultTimeout);
    }

    public TimeSpan Timeout
    {
        get
        {
            TimeSpan configured = _timeout();
            return configured.TotalSeconds > 0 ? configured : DefaultTimeout;
        }
    }

    /// <summary>
    /// Commitea <paramref name="paths"/> —y nada más— con el mensaje dado.
    /// <para>
    /// <b>Nada se toca si algo falla.</b> Todos los rechazos ocurren ANTES de llamar a git, y el
    /// único que puede ocurrir después es el de git mismo, que no modifica el árbol de trabajo al
    /// abortar. Un commit fallido no descarta nada ni deja el repositorio a medias — y desde
    /// BUGFIX-F32-3 eso incluye el <b>índice</b>: lo único que se prepara aquí son los ficheros
    /// nuevos del arreglo, y si el commit no sale se despreparan.
    /// </para>
    /// </summary>
    /// <param name="title">La primera línea. Vacío se rechaza: un commit sin asunto no se hace.</param>
    /// <param name="description">El cuerpo, separado por una línea en blanco. Puede ir vacío.</param>
    public FixCommitResult Commit(
        string? cloneRoot,
        IReadOnlyList<string> paths,
        string? title,
        string? description,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cloneRoot) || !Directory.Exists(cloneRoot))
        {
            return Fail("No hay un clon local de esta aplicación en esta máquina.");
        }

        if (!Repository.IsValid(cloneRoot))
        {
            return Fail($"{cloneRoot} ya no es un repositorio git: no hay dónde commitear.");
        }

        if (paths.Count == 0)
        {
            return Fail("No hay ningún fichero del arreglo que commitear.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return Fail("El título del commit está vacío. Escríbelo y vuelve a intentarlo.");
        }

        if (MissingIdentity(cloneRoot!) is { Length: > 0 } missing)
        {
            return Fail(
                $"Este clon no tiene identidad de git configurada ({missing}). Configúrala con "
                + "«git config user.name» y «git config user.email» — Atalaya no se inventa un autor.");
        }

        string message = string.IsNullOrWhiteSpace(description)
            ? title!.Trim()
            : title!.Trim() + "\n\n" + description!.Trim();

        // Las rutas, como las escribe git: el índice se consulta y se commitea con barras.
        List<string> targets = paths.Select(p => p.Replace('\\', '/')).ToList();

        string messageFile = Path.Combine(
            Path.GetTempPath(), $"atalaya-commit-{Guid.NewGuid():N}.txt");

        // Lo preparado AQUÍ, y solo eso, es lo que se despreparará si el commit no sale.
        IReadOnlyList<string> staged = Array.Empty<string>();

        try
        {
            // Sin BOM: git se lo tragaría dentro del asunto del commit.
            File.WriteAllText(messageFile, message, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // BUGFIX-F32-3 — LOS FICHEROS DEL ARREGLO QUE GIT NO CONOCE, AL ÍNDICE, Y SOLO
            // ELLOS. `--only` no sabe commitear una ruta que git no conoce todavía, y el arreglo
            // crea ficheros: un test que cubre el defecto es el caso normal, no el raro.
            try
            {
                staged = StageUnknown(cloneRoot!, targets);
            }
            catch (Exception ex)
            {
                return Fail($"No se pudieron preparar los ficheros nuevos del arreglo: {ex.Message}");
            }

            // --cleanup=whitespace y no el `strip` de por defecto: una descripción que empiece una
            // línea por «#» es texto del usuario, no un comentario que git deba comerse.
            string arguments = "commit --only --cleanup=whitespace -F "
                + Quote(messageFile) + " --"
                + string.Concat(targets.Select(p => " " + Quote(p)));

            ProcessOutcome outcome;
            try
            {
                outcome = _runner.Run("git", arguments, cloneRoot!, Timeout, ct);
            }
            catch (Exception ex)
            {
                // El caso normal aquí es que no haya `git` en el PATH. Es un fallo como otro
                // cualquiera: se dice y no se toca nada.
                return Undo(cloneRoot!, staged, $"No se pudo ejecutar git: {ex.Message}");
            }

            if (outcome.TimedOut)
            {
                return Undo(cloneRoot!, staged,
                    $"El commit no volvió en {Timeout.TotalSeconds:0} s y se ha cortado. Tus "
                    + "cambios siguen en el clon, sin commitear." + Tail(outcome.Output));
            }

            if (outcome.ExitCode != 0)
            {
                return Undo(cloneRoot!, staged,
                    "git rechazó el commit. Tus cambios siguen en el clon, exactamente como "
                    + "estaban." + Tail(outcome.Output));
            }

            string sha = GitInfo.HeadSha(cloneRoot);
            return sha is { Length: > 0 } && sha != "unknown"
                ? new FixCommitResult(true, sha, Author: AuthorOf(cloneRoot!))
                // Aquí NO se despara nada: git volvió con 0, así que el commit está hecho y lo
                // que se preparó ya está dentro de su árbol. Deshacerlo sería inventarse un
                // cambio en el índice que git no hizo.
                : Fail("git dijo que el commit salió bien, pero no se pudo leer el nuevo HEAD.");
        }
        catch (Exception ex)
        {
            return Undo(cloneRoot!, staged, $"No se pudo commitear: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(messageFile);
            }
            catch (IOException)
            {
                // Un temporal que no se deja borrar no puede tumbar un commit que ya se hizo.
            }
        }
    }

    /// <summary>
    /// <b>Prepara los ficheros del arreglo que git todavía no conoce, y solo ésos</b>
    /// (BUGFIX-F32-3). Devuelve cuáles ha preparado, que es lo que hay que deshacer si el commit
    /// no sale.
    /// <para>
    /// La condición es una sola y no una lectura de estados: <b>que la ruta no esté en el
    /// índice</b>. Cubre el fichero nuevo y el ignorado por igual, y —lo que importa— <b>nunca
    /// toca una ruta que ya estuviera preparada</b>, sea del arreglo o del usuario: si está en el
    /// índice, git ya la conoce, <c>--only</c> la commitea y aquí no hay nada que hacer. Un
    /// fichero que el arreglo tocara y que ya no esté en el disco tampoco se prepara: no hay qué.
    /// </para>
    /// <para>
    /// Va por LibGit2Sharp y no por el CLI, al revés que el commit, y el criterio es el mismo que
    /// eligió el CLI en D-1033: <b>preparar no dispara ningún hook</b>. Y se prepara ruta a ruta,
    /// nunca con <c>-A</c> ni con <c>.</c> — eso es justo lo que <c>--only</c> existe para evitar—.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> StageUnknown(string cloneRoot, IReadOnlyList<string> paths)
    {
        var staged = new List<string>();
        using var repo = new Repository(cloneRoot);

        foreach (string path in paths)
        {
            if (repo.Index[path] is not null || !File.Exists(Path.Combine(cloneRoot, path)))
            {
                continue;
            }

            Commands.Stage(repo, path);
            staged.Add(path);
        }

        return staged;
    }

    /// <summary>
    /// <b>Deshace exactamente lo que se preparó</b> —el <c>git reset -- ruta</c> de cada una— y
    /// devuelve el fallo (BUGFIX-F32-3). La regla de D-1034 es que un paso 1 que falla no cambia
    /// nada, y el índice es parte del clon: un fichero nuevo que se quedara preparado sería un
    /// cambio que el usuario no pidió y que además se colaría en su siguiente commit.
    /// <para>
    /// Despreparar no toca el árbol de trabajo: el fichero sigue en el disco, byte a byte, sin
    /// seguimiento — que es como estaba antes de pulsar.
    /// </para>
    /// </summary>
    private static FixCommitResult Undo(
        string cloneRoot, IReadOnlyList<string> staged, string reason)
    {
        if (staged.Count == 0)
        {
            return Fail(reason);
        }

        try
        {
            using var repo = new Repository(cloneRoot);
            Commands.Unstage(repo, staged);
        }
        catch (Exception ex)
        {
            // No poder despreparar no cambia el desenlace —el commit no se hizo—, pero el usuario
            // tiene que enterarse de que le queda algo en el índice.
            return Fail(reason
                + $"\n\n[además, no se pudieron despreparar {staged.Count} fichero(s) nuevo(s) "
                + $"del arreglo: {ex.Message}]");
        }

        return Fail(reason);
    }

    /// <summary>
    /// Con quién quedó firmado el commit que se acaba de hacer. Del commit, no del config:
    /// es lo que el usuario va a ver en el historial cuando publique (BUGFIX-F32-2).
    /// </summary>
    private static string? AuthorOf(string cloneRoot)
    {
        try
        {
            using var repo = new Repository(cloneRoot);
            Signature? who = repo.Head.Tip?.Author;
            return who is null ? null : $"{who.Name} <{who.Email}>";
        }
        catch (Exception)
        {
            return null;   // El commit está hecho; no poder leer el autor no lo deshace.
        }
    }

    /// <summary>
    /// Qué falta de la identidad, o vacío si está. Se mira el config —local, global y de sistema,
    /// igual que git— y también las variables de entorno, que es la otra forma legítima de tenerla
    /// puesta: rechazar por config ausente a quien la tiene en el entorno sería un falso negativo.
    /// </summary>
    private static string MissingIdentity(string cloneRoot)
    {
        string? name;
        string? email;
        try
        {
            using var repo = new Repository(cloneRoot);
            name = repo.Config.Get<string>("user.name")?.Value;
            email = repo.Config.Get<string>("user.email")?.Value;
        }
        catch (Exception)
        {
            return string.Empty;   // No poder leerla no es lo mismo que no tenerla: decide git.
        }

        name = Pick(name, "GIT_AUTHOR_NAME", "GIT_COMMITTER_NAME");
        email = Pick(email, "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_EMAIL");

        return (name, email) switch
        {
            (null or "", null or "") => "faltan user.name y user.email",
            (null or "", _) => "falta user.name",
            (_, null or "") => "falta user.email",
            _ => string.Empty,
        };
    }

    private static string? Pick(string? configured, params string[] variables)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (string variable in variables)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>La cola de lo que dijo git, que es donde habla el hook.</summary>
    private static string Tail(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        string[] lines = output!.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        string[] tail = lines.Length <= TailLines ? lines : lines[^TailLines..];
        string prefix = lines.Length > tail.Length
            ? $"\n\n[git, últimas {tail.Length} de {lines.Length} líneas]\n"
            : "\n\n";
        return prefix + string.Join("\n", tail);
    }

    private static FixCommitResult Fail(string reason) => new(false, Error: reason);

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
