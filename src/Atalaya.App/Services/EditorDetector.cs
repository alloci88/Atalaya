using Microsoft.Win32;

namespace Atalaya.App.Services;

/// <summary>
/// Lo que hay que preguntarle a la máquina para saber si un editor está instalado, detrás de una
/// costura.
/// <para>
/// Existe por lo mismo que <see cref="IFileOpener"/> (D-260, D-287, D-304): con
/// <c>File.Exists</c> y el registro de Windows sueltos dentro del detector, «el desplegable enseña
/// solo lo instalado» solo se podría comprobar instalando ocho editores en la máquina de tests.
/// </para>
/// </summary>
public interface IEditorProbe
{
    bool FileExists(string path);

    /// <summary>Las subcarpetas de <paramref name="parent"/>; vacío si no existe.</summary>
    IEnumerable<string> Directories(string parent);

    /// <summary>Las carpetas del PATH.</summary>
    IEnumerable<string> PathDirectories();

    /// <summary>
    /// La ruta que el registro de Windows asocia a un ejecutable (<c>App Paths</c>), o
    /// <c>null</c>. Es la vía fiable para lo que se instala sin tocar el PATH — que en Windows es
    /// casi todo.
    /// </summary>
    string? AppPath(string executable);

    /// <summary>Expande <c>%ProgramFiles%</c> y compañía.</summary>
    string Expand(string path);
}

/// <inheritdoc cref="IEditorProbe"/>
public sealed class SystemEditorProbe : IEditorProbe
{
    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IEnumerable<string> Directories(string parent)
    {
        try
        {
            return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).ToList() : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public IEnumerable<string> PathDirectories()
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string? AppPath(string executable)
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? entry = root.OpenSubKey(key + executable);
                if (entry?.GetValue(null) is string path && path.Length > 0)
                {
                    return path.Trim('"');
                }
            }
            catch (Exception)
            {
                // Un registro que no se deja leer no es un editor instalado: se sigue buscando.
            }
        }

        return null;
    }

    public string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}

/// <summary>Un editor de la tabla y dónde está, si está.</summary>
/// <param name="Detected">
/// False para el que el ajuste nombra pero ya no se encuentra: sigue en la lista para no cambiarle
/// el ajuste al usuario por la espalda, y se enseña marcado.
/// </param>
public sealed record DetectedEditor(EditorDefinition Editor, string? ExecutablePath, bool Detected)
{
    /// <summary>Lo que lee el desplegable: el nombre, y si no está, dicho.</summary>
    public string Label => Detected ? Editor.Name : Editor.Name + " (no encontrado)";
}

/// <summary>Una entrada del desplegable de Ajustes: lo que se guarda y lo que se lee.</summary>
public sealed record EditorOption(string Id, string Label);

/// <summary>
/// Qué editores del registro están de verdad en esta máquina.
/// <para>
/// <b>Por qué importa que el desplegable no ofrezca lo que no hay.</b> Elegir un editor que no está
/// instalado es elegir un fallo, y hasta esta tanda ese fallo era mudo: <c>devenv</c> no resolvía,
/// se caía al manejador del sistema y el fichero se abría en otra cosa como si nada hubiera pasado.
/// </para>
/// <para>
/// Se busca en tres sitios y en este orden: el registro de Windows (<c>App Paths</c>, que es lo que
/// rellena casi todo instalador), el PATH, y las carpetas de instalación conocidas de cada ficha.
/// </para>
/// </summary>
public sealed class EditorDetector
{
    private readonly IEditorProbe _probe;
    private IReadOnlyList<DetectedEditor>? _cache;

    public EditorDetector(IEditorProbe? probe = null) => _probe = probe ?? new SystemEditorProbe();

    /// <summary>
    /// Los editores que ofrecer, en el orden del registro. Se cachea: la detección toca disco y
    /// registro, y el desplegable se construye cada vez que se abre Ajustes.
    /// </summary>
    public IReadOnlyList<DetectedEditor> Detect() => _cache ??= Scan();

    /// <summary>Vuelve a mirar. Lo usa «Probar» para que instalar un editor y probar sea un gesto.</summary>
    public IReadOnlyList<DetectedEditor> Refresh()
    {
        _cache = null;
        return Detect();
    }

    /// <summary>
    /// Lo que se ofrece en Ajustes: lo detectado, más el manejador del sistema —que está siempre—,
    /// más el editor que el ajuste nombre aunque ya no esté (marcado «no encontrado»).
    /// </summary>
    public IReadOnlyList<DetectedEditor> Offer(string? configured)
    {
        var offered = Detect().Where(d => d.Detected).ToList();
        if (configured is { Length: > 0 } && !offered.Any(d => d.Editor.Id.Equals(configured, StringComparison.OrdinalIgnoreCase))
            && EditorRegistry.Find(configured) is { } missing)
        {
            offered.Insert(0, new DetectedEditor(missing, null, Detected: false));
        }

        return offered;
    }

    /// <summary>Dónde está el ejecutable de este editor, o <c>null</c> si no está.</summary>
    public string? Locate(EditorDefinition editor)
        => Detect().FirstOrDefault(d => d.Editor.Id == editor.Id)?.ExecutablePath;

    private IReadOnlyList<DetectedEditor> Scan()
    {
        var found = new List<DetectedEditor>();
        foreach (EditorDefinition editor in EditorRegistry.All)
        {
            if (editor.Kind != EditorKind.Program)
            {
                // El manejador del sistema no se instala: está siempre.
                found.Add(new DetectedEditor(editor, null, Detected: true));
                continue;
            }

            string? path = LocateOnDisk(editor);
            found.Add(new DetectedEditor(editor, path, path is not null));
        }

        return found;
    }

    private string? LocateOnDisk(EditorDefinition editor)
    {
        foreach (string exe in editor.Executables)
        {
            if (_probe.AppPath(exe) is { Length: > 0 } registered && _probe.FileExists(registered))
            {
                return registered;
            }
        }

        foreach (string dir in _probe.PathDirectories())
        {
            foreach (string exe in editor.Executables)
            {
                string candidate = Path.Combine(dir, exe);
                if (_probe.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (string folder in editor.Folders)
        {
            if (Resolve(_probe.Expand(folder)) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// La primera ruta real que casa con la plantilla. El <c>*</c> se expande por segmentos —una
    /// carpeta por versión, una por edición— porque es exactamente así como se instalan Visual
    /// Studio y los JetBrains, y adivinar el año o la edición sería adivinar.
    /// </summary>
    private string? Resolve(string template)
    {
        string[] parts = template.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var heads = new List<string> { parts[0].Length == 0 ? Path.DirectorySeparatorChar.ToString() : parts[0] };

        for (int i = 1; i < parts.Length && heads.Count > 0; i++)
        {
            string part = parts[i];
            var next = new List<string>();

            foreach (string head in heads)
            {
                if (!part.Contains('*'))
                {
                    // Los tramos intermedios se encadenan sin comprobar: lo que no exista se cae
                    // solo en el filtro final, y así no hay dos comprobaciones que mantener.
                    next.Add(Path.Combine(head, part));
                    continue;
                }

                foreach (string child in _probe.Directories(head))
                {
                    if (Matches(Path.GetFileName(child), part))
                    {
                        next.Add(child);
                    }
                }
            }

            heads = next;
        }

        return heads.FirstOrDefault(_probe.FileExists);
    }

    /// <summary>Un <c>*</c> por segmento: prefijo, sufijo, o todo.</summary>
    private static bool Matches(string name, string pattern)
    {
        int star = pattern.IndexOf('*');
        string prefix = pattern[..star];
        string suffix = pattern[(star + 1)..];
        return name.Length >= prefix.Length + suffix.Length
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
