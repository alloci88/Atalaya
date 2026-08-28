namespace Atalaya.Inventory;

/// <summary>Un fichero del clon que el catálogo reconoce como posible directiva (F7).</summary>
/// <param name="Path">Ruta relativa a la raíz del clon, con barras hacia delante.</param>
/// <param name="Kind">La familia del patrón que lo encontró.</param>
/// <param name="Why">Por qué el catálogo lo propone. Se enseña en el panel.</param>
/// <param name="Bytes">Tamaño en disco, para poder estimar lo que costaría incluirlo.</param>
public sealed record DirectiveCandidate(string Path, string Kind, string Why, long Bytes);

/// <summary>
/// Busca en un clon local los ficheros que el catálogo reconoce como directivas del proyecto (F7).
/// <para>
/// <b>Camina el árbol por su cuenta y no reutiliza el recorrido del inventario.</b> Aquél poda
/// <c>specs</c>, <c>tests</c>, <c>docs</c> y <c>fixtures</c> porque no son código que auditar —y
/// además solo se queda con los ficheros fuente del stack—, así que preguntarle por un ADR o por
/// una skill habría devuelto siempre una lista vacía. Son dos preguntas distintas sobre el mismo
/// árbol y cada una necesita su recorrido.
/// </para>
/// <para>
/// No lee el contenido de nada: devuelve rutas y tamaños. El contenido se lee cuando hace falta
/// —al componer un prompt, o al pedir la vista previa en el panel— y siempre del clon, que es
/// donde vive la versión vigente.
/// </para>
/// </summary>
public sealed class DirectiveScanner
{
    /// <summary>
    /// Tope de candidatos. Una colección de skills grande puede tener centenares de ficheros y un
    /// panel con dos mil filas no es una lista, es un vertedero. Se corta y se dice: el panel
    /// avisa de que hay más, y lo que falte se añade a mano.
    /// </summary>
    public const int MaxCandidates = 400;

    public DirectiveScanOutput Scan(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new DirectiveScanOutput(Array.Empty<DirectiveCandidate>(), false);
        }

        root = Path.GetFullPath(root);
        var pruned = new HashSet<string>(DirectiveCatalog.PrunedDirectories, StringComparer.OrdinalIgnoreCase);
        var found = new List<DirectiveCandidate>();
        bool truncated = false;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && !truncated)
        {
            string dir = pending.Pop();

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                // Una carpeta sin permisos no rompe el escaneo: se salta, como en el inventario.
                continue;
            }

            foreach (string sub in subdirs)
            {
                if (!pruned.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                DirectivePattern? pattern = DirectiveCatalog.Match(rel);
                if (pattern is null)
                {
                    continue;
                }

                long bytes;
                try
                {
                    bytes = new FileInfo(file).Length;
                }
                catch
                {
                    continue;
                }

                if (bytes > DirectiveCatalog.MaxCandidateBytes)
                {
                    continue;
                }

                found.Add(new DirectiveCandidate(rel, pattern.Kind, pattern.Why, bytes));
                if (found.Count >= MaxCandidates)
                {
                    truncated = true;
                    break;
                }
            }
        }

        // Orden estable por ruta: dos máquinas que escanean el mismo commit tienen que proponer la
        // misma lista en el mismo orden, o el panel parecería cambiar solo.
        found.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new DirectiveScanOutput(found, truncated);
    }
}

/// <summary>
/// Lo que encontró un escaneo de directivas. El <paramref name="Truncated"/> viaja con la lista
/// porque una lista recortada que no lo dice se lee como una lista completa.
/// </summary>
public sealed record DirectiveScanOutput(IReadOnlyList<DirectiveCandidate> Candidates, bool Truncated);
