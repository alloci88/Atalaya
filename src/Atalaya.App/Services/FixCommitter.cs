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
    /// abortar. Un commit fallido no descarta nada ni deja el repositorio a medias.
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

        string messageFile = Path.Combine(
            Path.GetTempPath(), $"atalaya-commit-{Guid.NewGuid():N}.txt");

        try
        {
            // Sin BOM: git se lo tragaría dentro del asunto del commit.
            File.WriteAllText(messageFile, message, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // --cleanup=whitespace y no el `strip` de por defecto: una descripción que empiece una
            // línea por «#» es texto del usuario, no un comentario que git deba comerse.
            string arguments = "commit --only --cleanup=whitespace -F "
                + Quote(messageFile) + " --"
                + string.Concat(paths.Select(p => " " + Quote(p.Replace('\\', '/'))));

            ProcessOutcome outcome;
            try
            {
                outcome = _runner.Run("git", arguments, cloneRoot!, Timeout, ct);
            }
            catch (Exception ex)
            {
                // El caso normal aquí es que no haya `git` en el PATH. Es un fallo como otro
                // cualquiera: se dice y no se toca nada.
                return Fail($"No se pudo ejecutar git: {ex.Message}");
            }

            if (outcome.TimedOut)
            {
                return Fail(
                    $"El commit no volvió en {Timeout.TotalSeconds:0} s y se ha cortado. Tus "
                    + "cambios siguen en el clon, sin commitear." + Tail(outcome.Output));
            }

            if (outcome.ExitCode != 0)
            {
                return Fail(
                    "git rechazó el commit. Tus cambios siguen en el clon, exactamente como "
                    + "estaban." + Tail(outcome.Output));
            }

            string sha = GitInfo.HeadSha(cloneRoot);
            return sha is { Length: > 0 } && sha != "unknown"
                ? new FixCommitResult(true, sha, Author: AuthorOf(cloneRoot!))
                : Fail("git dijo que el commit salió bien, pero no se pudo leer el nuevo HEAD.");
        }
        catch (Exception ex)
        {
            return Fail($"No se pudo commitear: {ex.Message}");
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
