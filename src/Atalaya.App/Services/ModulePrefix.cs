namespace Atalaya.App.Services;

/// <summary>
/// Cómo se rotula un módulo en las bandas del mapa, donde el sitio escasea (F10.1c): <b>sin el
/// nombre de su aplicación</b>. Un módulo de XBLAST que se llama <c>XBLASTCore</c> está repitiendo
/// el nombre de la app en cada banda; lo que lo distingue de sus hermanos es <c>Core</c>.
/// <para>
/// <b>Es una convención de PRESENTACIÓN, no cirugía sobre el dato.</b> Se calcula en tiempo de
/// render, por aplicación, y no se guarda ni se escribe en ninguna parte: el hub sigue teniendo los
/// nombres tal y como salen del clon, y el nombre completo se conserva en el tooltip, en la tabla,
/// en el inventario y en la lámina exportada. Lo único que cambia es la etiqueta de una banda de
/// dos centímetros.
/// </para>
/// <para>
/// <b>Por qué el nombre de la app y no el prefijo común de los módulos.</b> El prefijo común exige
/// que lo compartan TODOS, y basta un módulo fuera para perderlo entero — el caso real de XBLAST,
/// donde veintiún módulos empiezan por «XBLAST» y el vigesimosegundo se llama <c>Documents</c>.
/// Relajarlo a «la mayoría» habría dejado un mapa con nombres omitidos y nombres enteros
/// mezclados sin forma de saber cuáles son cuáles. Con el nombre de la aplicación no hay ninguna
/// ambigüedad estadística: lo que se quita es un hecho —ese módulo lleva el nombre de su app— y se
/// explica en una frase que se entiende a la primera. Quien lee <c>Documents</c> entiende que ese
/// módulo no lleva el nombre de la aplicación, no que le falte algo.
/// </para>
/// </summary>
public static class ModulePrefix
{
    /// <summary>
    /// Por debajo de esto, omitir no ahorra sitio y sí quita contexto. Tres caracteres es donde
    /// empieza a compensar — y una aplicación llamada «Ax» no tiene un nombre que estorbe.
    /// </summary>
    public const int MinLength = 3;

    /// <summary>
    /// Lo que le tiene que quedar al módulo después de quitarle el nombre de la app. Un módulo que
    /// se quedara en una letra —o en nada— no sería un nombre: ese se muestra entero.
    /// </summary>
    public const int MinRemainder = 2;

    /// <summary>
    /// Cómo se rotula cada módulo: su nombre corto, o <c>null</c> si se muestra entero. La clave es
    /// el nombre del módulo tal cual está en el inventario.
    /// </summary>
    /// <param name="appName">El nombre de la aplicación (<c>XBLAST</c>).</param>
    /// <param name="appSlug">Y su slug (<c>xblast</c>): se prueban los dos, sin distinguir mayúsculas.</param>
    public static IReadOnlyDictionary<string, string?> DisplayNames(
        string? appName, string? appSlug, IEnumerable<string> moduleNames)
    {
        var modules = moduleNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var display = modules.ToDictionary(m => m, _ => (string?)null, StringComparer.Ordinal);
        if (Token(appName, appSlug, modules) is not { Length: > 0 } token)
        {
            return display;
        }

        foreach (string module in modules)
        {
            display[module] = Shorten(module, token);
        }

        Deduplicate(display);
        return display;
    }

    /// <summary>El nombre sin el token, o <c>null</c> si no procede acortarlo.</summary>
    public static string? Shorten(string module, string token)
    {
        if (!module.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = module[token.Length..];

        // Ni un nombre que se queda en nada, ni un corte a mitad de palabra: «XBLASTern» no es un
        // módulo llamado «ern», es un nombre que da la casualidad de que empieza igual.
        return rest.Length >= MinRemainder && !char.IsLower(rest[0]) ? rest : null;
    }

    /// <summary>
    /// Qué se quita: el nombre de la aplicación o su slug, el que más módulos encabece. Casi
    /// siempre son la misma palabra; se prueban los dos porque no tienen por qué serlo.
    /// </summary>
    private static string? Token(string? appName, string? appSlug, IReadOnlyList<string> modules)
        => new[] { appName, appSlug }
            .Where(t => t is { Length: >= MinLength })
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (Token: t, Hits: modules.Count(m => Shorten(m, t) is not null)))
            .Where(p => p.Hits > 0)
            .OrderByDescending(p => p.Hits)
            .ThenByDescending(p => p.Token.Length)
            .Select(p => p.Token)
            .FirstOrDefault();

    /// <summary>
    /// <b>Dos módulos no pueden acabar rotulados igual.</b> Con una app «XBLAST» que tuviera
    /// <c>XBLASTCore</c> y <c>Core</c>, acortar el primero dejaría dos bandas «Core» y el mapa
    /// tendría dos módulos indistinguibles — que es peor que un nombre largo. Los que chocan
    /// vuelven los DOS a su nombre entero; los demás se quedan acortados.
    /// <para>
    /// Se repite hasta que nada cambia: un nombre que vuelve a ser largo podría, en teoría, chocar
    /// con el corto de un tercero. Termina siempre, porque cada vuelta solo convierte cortos en
    /// largos y nunca al revés.
    /// </para>
    /// </summary>
    private static void Deduplicate(Dictionary<string, string?> display)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            var clashes = display
                .GroupBy(p => p.Value ?? p.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Where(p => p.Value is not null).Select(p => p.Key))
                .ToList();

            foreach (string module in clashes)
            {
                display[module] = null;
                changed = true;
            }
        }
    }
}
