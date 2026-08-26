using Atalaya.Domain;

namespace Atalaya.App.Services;

/// <summary>
/// Un color de serie con su paso para cada tema (F5.9 §2).
/// <para>
/// No es un «flip» automático del mismo color: son DOS pasos elegidos a mano de la misma familia,
/// uno que contrasta sobre el fondo claro y otro sobre el oscuro. Invertir la luminosidad de un
/// color pensado para fondo claro da, sobre fondo oscuro, un tono lavado que no se distingue del
/// vecino — que es justo lo que la regla quiere evitar.
/// </para>
/// </summary>
public sealed record SeriesColor(string Name, string Light, string Dark)
{
    public string For(bool dark) => dark ? Dark : Light;
}

/// <summary>
/// Los colores de SEVERIDAD, en un solo sitio (F5.9 §2).
/// <para>
/// Son colores de ESTADO y quédan reservados: significan crítica/alta/media/baja en toda la
/// aplicación —chips del portafolio, badges de hallazgos, informes— y por eso ninguna serie de
/// una gráfica puede usarlos. Vivían empotrados en <c>SeverityToBrushConverter</c>; salen aquí
/// para que la reserva sea comprobable en vez de ser una intención escrita en un comentario.
/// </para>
/// </summary>
public static class SeverityPalette
{
    public const string Critica = "#D13A3A";
    public const string Alta = "#E07A2B";
    public const string Media = "#D2B036";
    public const string Baja = "#6C93C0";

    public static string Hex(Severity severity) => severity switch
    {
        Severity.Critica => Critica,
        Severity.Alta => Alta,
        Severity.Media => Media,
        _ => Baja,
    };

    /// <summary>Los cuatro, para poder afirmar que ninguna serie los usa.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Critica, Alta, Media, Baja };
}

/// <summary>
/// Color por IDENTIDAD de aplicación (F5.9 §2): xblast es del mismo color en la gráfica de coste,
/// en su rosco de cobertura y en la lista de sesiones, hoy y dentro de tres meses.
/// <para>
/// <b>Por qué por hash y no por posición.</b> Repartir la paleta por el orden de la lista de apps
/// haría que dar de alta una aplicación nueva —algo que va a pasar con cada app de la empresa—
/// repintase a todas las que van detrás. El índice sale de un hash ESTABLE del slug
/// (FNV-1a; nunca <c>string.GetHashCode</c>, que es aleatorio por proceso y daría colores
/// distintos en cada arranque). Las colisiones se resuelven en orden ordinal de slug sobre el
/// portafolio COMPLETO, así que filtrar la vista no reparte nada: un filtro que oculta apps deja
/// a las demás exactamente del color que tenían.
/// </para>
/// <para>
/// <b>Y por qué solo seis.</b> Más allá de seis, los tonos que quedan libres —descontados los
/// cuatro de severidad y los tres de estado (verde/ámbar/rojo)— ya no se distinguen entre sí en
/// una línea de 1,6 px. La séptima aplicación y siguientes se agrupan en «Otras», que va en gris
/// y a trazos: nunca se puede confundir con una app concreta.
/// </para>
/// </summary>
public static class SeriesPalette
{
    /// <summary>Cuántas aplicaciones tienen color propio. El resto se agrupa en «Otras».</summary>
    public const int MaxNamedSeries = 6;

    public static IReadOnlyList<SeriesColor> Steps { get; } = new SeriesColor[]
    {
        new("violeta", "#6E56CF", "#A491F7"),
        new("turquesa", "#0F8A80", "#35CBBB"),
        new("magenta", "#B83280", "#EE79BC"),
        new("añil", "#2E4CC8", "#7C94FF"),
        new("oliva", "#5C8A16", "#9CCB4A"),
        new("pizarra", "#4E6472", "#93AEC0"),
    };

    /// <summary>
    /// El gris de «Otras». Va SIEMPRE a trazos en las gráficas: un agregado de varias apps no
    /// puede leerse como una app más.
    /// </summary>
    public static SeriesColor Others { get; } = new("otras", "#767676", "#9E9E9E");

    /// <summary>Los segmentos neutros de un rosco: lo pendiente y lo excluido por tamaño.</summary>
    public static SeriesColor Pending { get; } = new("pendiente", "#C9CDD2", "#3E4650");

    /// <inheritdoc cref="Pending"/>
    public static SeriesColor Large { get; } = new("grande", "#9AA0A6", "#5C6672");

    /// <summary>
    /// Reparte la paleta sobre el portafolio completo. Determinista: mismas apps → mismos colores,
    /// en esta sesión y en la siguiente.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Assign(IEnumerable<string> slugs)
    {
        var ordered = slugs
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var taken = new bool[Steps.Count];
        var assigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Primera vuelta: cada slug a SU sitio, el que dice su hash. Es el que conserva para
        // siempre mientras nadie más lo ocupe antes.
        foreach (string slug in ordered)
        {
            int home = Home(slug);
            if (!taken[home])
            {
                taken[home] = true;
                assigned[slug] = home;
            }
        }

        // Segunda: los que colisionaron, al primer hueco libre a partir de su sitio. El orden
        // ordinal hace que el reparto no dependa de en qué orden los devolviera el disco.
        foreach (string slug in ordered.Where(s => !assigned.ContainsKey(s)))
        {
            int home = Home(slug);
            int free = -1;
            for (int k = 0; k < Steps.Count; k++)
            {
                int candidate = (home + k) % Steps.Count;
                if (!taken[candidate])
                {
                    free = candidate;
                    break;
                }
            }

            // Con más de seis apps la paleta se agota: se repite color, y la leyenda —que siempre
            // nombra— es la que desempata. Preferible a inventar un séptimo tono ilegible.
            taken[free < 0 ? home : free] = true;
            assigned[slug] = free < 0 ? home : free;
        }

        return assigned;
    }

    /// <summary>El color de una app según un reparto ya hecho.</summary>
    public static SeriesColor For(string slug, IReadOnlyDictionary<string, int> assignment)
        => Steps[assignment.TryGetValue(slug, out int i) ? i : Home(slug)];

    /// <summary>El sitio natural de un slug en la paleta.</summary>
    private static int Home(string slug) => (int)(Fnv1a(slug) % (uint)Steps.Count);

    /// <summary>
    /// FNV-1a de 32 bits sobre el slug en minúsculas. Estable entre procesos y entre versiones de
    /// .NET, que es toda la razón de no usar <c>string.GetHashCode</c>.
    /// </summary>
    internal static uint Fnv1a(string value)
    {
        uint hash = 2166136261;
        foreach (char c in value.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash;
    }
}
