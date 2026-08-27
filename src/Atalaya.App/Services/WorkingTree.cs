using LibGit2Sharp;

namespace Atalaya.App.Services;

/// <summary>
/// Cómo está el árbol de trabajo de un clon (F6.9 §1).
/// </summary>
/// <param name="Clean">No hay nada sin commitear.</param>
/// <param name="Changed">Las rutas que estorban, ya recortadas para poder enseñarlas.</param>
/// <param name="Total">Cuántas hay en total, aunque solo se listen unas pocas.</param>
/// <param name="Problem">
/// No se pudo mirar (la carpeta no es un repo, no se puede abrir). Es distinto de «sucio»: no
/// saber no es lo mismo que saber que hay cambios, y por eso no se dicen con la misma frase.
/// </param>
public sealed record WorkingTreeState(
    bool Clean, IReadOnlyList<string> Changed, int Total, string? Problem = null)
{
    /// <summary>Cuántas rutas se nombran como mucho antes de resumir con un «y N más».</summary>
    public const int MaxListed = 8;

    /// <summary>Lo que se le dice al usuario cuando el arreglo no puede arrancar por esto.</summary>
    public string Message
    {
        get
        {
            if (Problem is { Length: > 0 })
            {
                return Problem;
            }

            if (Clean)
            {
                return "El árbol de trabajo está limpio.";
            }

            string list = string.Join(", ", Changed);
            string more = Total > Changed.Count ? $" …y {Total - Changed.Count} más" : string.Empty;
            return $"Tienes cambios locales sin commitear ({Total}): {list}{more}. "
                + "Commitea o descarta antes de lanzar el arreglo — es lo que hace posible "
                + "deshacerlo después de un solo clic.";
        }
    }
}

/// <summary>
/// Mira si el clon está limpio antes de dejar que un agente escriba en él.
/// <para>
/// <b>Sin excepciones</b> (F6.9 §1). El arreglo asistido no crea rama: lo único que separa «el
/// agente tocó tres ficheros» de «no sé qué hay aquí mío y qué suyo» es que al empezar no hubiera
/// nada más. Con el árbol sucio, «Descartar todo» pisaría trabajo del usuario o lo dejaría
/// mezclado con el del agente, y las dos cosas son peores que no arrancar.
/// </para>
/// <para>
/// Los ficheros IGNORADOS por git no cuentan: <c>bin/</c>, <c>obj/</c> y los artefactos de
/// compilación están en el árbol de cualquiera y no son trabajo de nadie. Los no seguidos que NO
/// están ignorados sí cuentan — un fichero nuevo a medio escribir es exactamente el trabajo que
/// no se puede pisar.
/// </para>
/// </summary>
public static class WorkingTree
{
    public static WorkingTreeState Inspect(string? clonePath)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || !Directory.Exists(clonePath))
        {
            return new WorkingTreeState(false, Array.Empty<string>(), 0,
                "No hay un clon local de esta aplicación en esta máquina.");
        }

        if (!Repository.IsValid(clonePath))
        {
            return new WorkingTreeState(false, Array.Empty<string>(), 0,
                $"{clonePath} ya no es un repositorio git, así que no se puede comprobar si tienes "
                + "cambios sin commitear.");
        }

        try
        {
            using var repo = new Repository(clonePath);
            RepositoryStatus status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeIgnored = false,
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
            });

            List<string> dirty = status
                .Where(e => e.State is not (FileStatus.Unaltered or FileStatus.Ignored))
                .Select(e => e.FilePath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return dirty.Count == 0
                ? new WorkingTreeState(true, Array.Empty<string>(), 0)
                : new WorkingTreeState(
                    false, dirty.Take(WorkingTreeState.MaxListed).ToList(), dirty.Count);
        }
        catch (Exception ex)
        {
            return new WorkingTreeState(false, Array.Empty<string>(), 0,
                $"No se pudo leer el estado del clon: {ex.Message}");
        }
    }
}
