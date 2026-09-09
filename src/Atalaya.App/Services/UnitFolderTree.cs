namespace Atalaya.App.Services;

/// <summary>
/// Una carpeta del inventario, ya plegada: lo que va a ser una fila entre el proyecto y sus
/// unidades (F37 §1).
/// </summary>
/// <param name="Name">
/// Lo que se lee en la fila. Tras plegar una cadena puede llevar varios tramos —«Class/Objects3D»—
/// porque esa cadena ES una sola fila.
/// </param>
/// <param name="RelativePath">
/// La carpeta, en canónico y relativa al proyecto. Es la identidad estable de la fila: el nombre
/// puede cambiar al plegarse o desplegarse la cadena, la ruta no.
/// </param>
/// <param name="Folders">Las subcarpetas, carpetas primero y en orden canónico (§1.8).</param>
/// <param name="Units">Las unidades que cuelgan DIRECTAMENTE de esta carpeta, en canónico.</param>
public sealed record UnitFolder(
    string Name,
    string RelativePath,
    IReadOnlyList<UnitFolder> Folders,
    IReadOnlyList<string> Units);

/// <summary>
/// <b>El árbol de carpetas de un proyecto, construido a partir de las rutas canónicas de sus
/// unidades y de nada más</b> (F37 §0).
/// <para>
/// Es una función pura a propósito. El escaneo NO cambia: la carpeta de una unidad ya está dentro
/// de su ruta desde el primer ciclo (D-549: las rutas se miran en canónico, con «/» y relativas a
/// la raíz del clon), así que agrupar es un asunto de la vista y no del inventario que se publica
/// en el hub. Enseñar carpetas no puede escribir un fichero distinto.
/// </para>
/// <para>
/// <b>Las dos reglas</b>, en este orden:
/// </para>
/// <list type="number">
/// <item>Solo existe la carpeta que contiene alguna unidad —directa o más abajo—. Se construye
/// desde las unidades, así que una carpeta vacía no llega ni a nacer.</item>
/// <item>Una carpeta sin unidades propias y con UNA sola subcarpeta se pliega con ella en una
/// fila: «Class/Objects3D». Con unidades propias, o con dos subcarpetas, no se pliega — ahí la
/// carpeta ya separa algo.</item>
/// </list>
/// </summary>
public static class UnitFolderTree
{
    /// <summary>
    /// <b>La carpeta del proyecto</b>, que es de la que se cuelgan las demás («relativa al
    /// proyecto», §1.1).
    /// <para>
    /// El inventario guarda la ruta relativa a la raíz del clon y el nombre del módulo, no el
    /// directorio del manifiesto: no hay un campo que diga «el proyecto vive aquí». Se deduce de
    /// lo que sí hay, y en dos pasos:
    /// </para>
    /// <list type="number">
    /// <item>el prefijo de directorio COMÚN a todas sus unidades — nada por encima de él
    /// distingue a una unidad de otra, así que no puede ser una carpeta del proyecto;</item>
    /// <item>y si en ese prefijo aparece el nombre del módulo, se corta ahí. Es lo que salva el
    /// caso de un proyecto cuyas unidades viven TODAS en una subcarpeta: sin este corte,
    /// <c>XBLASTMatLab/Class</c> entero se leería como «el proyecto» y la carpeta <c>Class</c>
    /// desaparecería de la vista.</item>
    /// </list>
    /// <para>
    /// Sin el nombre del módulo en la ruta —un <c>.csproj</c> que no se llama como su carpeta— se
    /// queda en el prefijo común, que es la respuesta honesta con lo que se sabe (N-2): agrupa
    /// bien, y como mucho deja de nombrar un tramo que ninguna unidad distingue.
    /// </para>
    /// </summary>
    public static string ProjectRoot(string module, IEnumerable<string> paths)
    {
        List<string>? common = null;
        foreach (string path in paths)
        {
            string[] dir = Segments(path);
            if (common is null)
            {
                common = dir.ToList();
                continue;
            }

            int shared = 0;
            while (shared < common.Count && shared < dir.Length
                   && string.Equals(common[shared], dir[shared], StringComparison.Ordinal))
            {
                shared++;
            }

            common.RemoveRange(shared, common.Count - shared);
        }

        if (common is null || common.Count == 0)
        {
            return string.Empty;
        }

        int cut = common.LastIndexOf(module);
        if (cut >= 0)
        {
            common.RemoveRange(cut + 1, common.Count - cut - 1);
        }

        return string.Join('/', common);
    }

    /// <summary>
    /// El árbol del proyecto. La raíz devuelta ES el proyecto: no tiene nombre y no se pliega
    /// nunca con su única hija — la fila del proyecto ya existe, y absorber la carpeta de dentro
    /// se llevaría por delante el único sitio donde se lee cómo se llama.
    /// </summary>
    public static UnitFolder Build(string module, IEnumerable<string> paths)
    {
        var all = paths as IReadOnlyList<string> ?? paths.ToList();
        return BuildUnder(ProjectRoot(module, all), all);
    }

    /// <summary>
    /// El árbol de las rutas dadas, colgando de una carpeta de proyecto YA decidida.
    /// <para>
    /// <b>Por qué la carpeta del proyecto se pasa desde fuera.</b> Se calcula sobre TODAS las
    /// unidades del proyecto, no sobre las que se están enseñando: con una búsqueda o un filtro de
    /// deriva puesto, el prefijo común de lo que queda puede bajar varios niveles, y el proyecto
    /// se «movería» al filtrar — un `Core/Forms` que aparece y desaparece según lo que se busque.
    /// El proyecto está donde está; lo que el filtro cambia es qué se enseña de él.
    /// </para>
    /// </summary>
    public static UnitFolder BuildUnder(string root, IEnumerable<string> paths)
    {
        var all = paths as IReadOnlyList<string> ?? paths.ToList();
        int skip = root.Length == 0 ? 0 : root.Split('/').Length;

        var top = new Branch(string.Empty, string.Empty);
        foreach (string path in all)
        {
            string[] segments = path.Split('/');
            Branch current = top;
            for (int i = skip; i < segments.Length - 1; i++)
            {
                current = current.Child(segments[i]);
            }

            current.Units.Add(path);
        }

        return Freeze(top, foldable: false);
    }

    private static UnitFolder Freeze(Branch branch, bool foldable)
    {
        var folders = branch.Children.Values.Select(k => Freeze(k, foldable: true)).ToList();
        var units = branch.Units.OrderBy(u => u, StringComparer.Ordinal).ToList();

        // La cadena de una sola subcarpeta. Las hijas ya vienen plegadas, así que «a» sobre «b/c»
        // sale «a/b/c» de una pasada y sin volver a recorrer nada.
        if (foldable && units.Count == 0 && folders.Count == 1)
        {
            UnitFolder only = folders[0];
            return only with { Name = $"{branch.Name}/{only.Name}" };
        }

        return new UnitFolder(branch.Name, branch.RelativePath, folders, units);
    }

    /// <summary>Los tramos del DIRECTORIO de una ruta canónica; el fichero no cuenta.</summary>
    private static string[] Segments(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? Array.Empty<string>() : path[..slash].Split('/');
    }

    /// <summary>El nodo mientras se construye. Ordenado en canónico desde que nace (§1.8).</summary>
    private sealed class Branch(string name, string relativePath)
    {
        public string Name { get; } = name;

        public string RelativePath { get; } = relativePath;

        public SortedDictionary<string, Branch> Children { get; } = new(StringComparer.Ordinal);

        public List<string> Units { get; } = new();

        public Branch Child(string segment)
        {
            if (!Children.TryGetValue(segment, out Branch? child))
            {
                child = new Branch(
                    segment,
                    RelativePath.Length == 0 ? segment : $"{RelativePath}/{segment}");
                Children.Add(segment, child);
            }

            return child;
        }
    }
}
