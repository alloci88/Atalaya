namespace Atalaya.App.Services;

/// <summary>
/// El prefijo que todos los módulos de una aplicación comparten, para poder omitirlo donde el
/// sitio escasea (F10.1b).
/// <para>
/// <b>Es una convención de PRESENTACIÓN, no cirugía sobre el dato.</b> Se calcula en tiempo de
/// render, por aplicación, y no se guarda ni se escribe en ninguna parte: el hub sigue teniendo
/// los nombres tal y como salen del clon, y el nombre completo se conserva en el tooltip, en la
/// tabla, en el inventario y en la lámina exportada. Lo único que cambia es la etiqueta de una
/// banda de dos centímetros.
/// </para>
/// <para>
/// <b>Por qué solo si lo comparten TODOS.</b> Con un prefijo que solo compartiera una parte, el
/// mapa mezclaría nombres recortados con nombres enteros y nadie podría saber cuáles son cuáles.
/// La declaración de la cabecera («Módulos de XBLAST*») solo se puede escribir una vez si vale
/// para todas las bandas.
/// </para>
/// </summary>
public static class ModulePrefix
{
    /// <summary>
    /// Por debajo de esto, omitir no ahorra sitio y sí quita contexto. Tres caracteres es donde
    /// empieza a compensar.
    /// </summary>
    public const int MinLength = 3;

    /// <summary>
    /// Lo que le tiene que quedar a CADA módulo después de quitarle el prefijo. Un módulo que se
    /// quedara en una letra —o en nada— no sería un nombre, así que en ese caso no se omite en
    /// ninguno: o vale para todos, o no vale.
    /// </summary>
    public const int MinRemainder = 2;

    /// <summary>
    /// El prefijo común omitible, o cadena vacía si no lo hay. Ordinal: los nombres de módulo son
    /// nombres de carpeta y se comparan como tales.
    /// </summary>
    public static string Common(IEnumerable<string> names)
    {
        var distinct = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Con un solo módulo, «el prefijo común» es su nombre entero: omitirlo dejaría la banda en
        // blanco. Hace falta al menos una pareja para que la palabra «común» signifique algo.
        if (distinct.Count < 2)
        {
            return string.Empty;
        }

        string prefix = distinct[0];
        foreach (string name in distinct)
        {
            while (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                prefix = prefix[..^1];
            }
        }

        prefix = ToWordBoundary(prefix, distinct);

        bool usable = prefix.Length >= MinLength
                      && distinct.All(n => n.Length - prefix.Length >= MinRemainder);

        return usable ? prefix : string.Empty;
    }

    /// <summary>El nombre sin su prefijo. Si no empieza por él —o no hay— se devuelve intacto.</summary>
    public static string Elide(string name, string prefix)
        => prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..]
            : name;

    /// <summary>
    /// Recorta el prefijo hasta que lo que queda de cada nombre empieza donde empieza una palabra.
    /// <para>
    /// El prefijo común de <c>XBLASTCore</c> y <c>XBLASTCommon</c> —a secas— es <c>XBLASTCo</c>, y
    /// omitirlo dejaría «re» y «mmon», que no son nombres de nada. Retrocediendo hasta que ningún
    /// resto empiece por minúscula queda <c>XBLAST</c>, que sí es la palabra que sobra.
    /// </para>
    /// </summary>
    private static string ToWordBoundary(string prefix, IReadOnlyList<string> names)
    {
        while (prefix.Length > 0 && names.Any(n => StartsMidWord(n, prefix.Length)))
        {
            prefix = prefix[..^1];
        }

        return prefix;
    }

    private static bool StartsMidWord(string name, int at)
        => at < name.Length && char.IsLower(name[at]);
}
