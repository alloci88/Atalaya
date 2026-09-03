using System.Text.RegularExpressions;

namespace Atalaya.Domain.Model;

/// <summary>
/// <b>Qué miembros dice el auditor que ha revisado</b>, sacado de sus resúmenes de pasada (F23 §4).
/// <para>
/// El informe repetía la misma lista una vez por pasada —siete veces la misma frase en el caso de
/// referencia—. La lista importa; repetirla no. Aquí se unen las de todas las pasadas de una unidad
/// para poder escribirla <b>una vez</b>.
/// </para>
/// <para>
/// <b>Se puede leer porque hay contrato.</b> El prompt le pide al auditor que el resumen empiece
/// exactamente por <c>«Revisados: A, B, C.»</c>, así que esto no adivina un formato: lee el que se
/// pidió. Lo que no encaje se ignora en silencio y la unidad se queda sin línea de revisados, que
/// es mejor que enseñar un trozo de prosa cortado por la mitad.
/// </para>
/// <para>
/// <b>Y se queda con lo que parece un miembro</b>: los identificadores empiezan por mayúscula y los
/// adornos del modelo no —«constantes UsuarioServicio/ClaveServicio», «campo estático Http» y
/// «Http (campo estático)» son las tres formas que produjo en una sola sesión, y de las tres sale
/// lo mismo—. Un miembro que de verdad empiece por minúscula se pierde; a cambio, no se cuela
/// media frase en español como si fuera código.
/// </para>
/// </summary>
public static class ReviewedMembers
{
    private const string Marker = "Revisados:";

    /// <summary>Identificadores: empiezan por mayúscula y siguen en letras, dígitos o guion bajo.</summary>
    private static readonly Regex Identifier = new(
        @"\b\p{Lu}[\p{L}\p{Nd}_]{1,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// La unión de lo revisado en todos los resúmenes, <b>en el orden en que apareció</b> y sin
    /// repetir. El orden es el del auditor: reordenarlo alfabéticamente perdería la pista de por
    /// dónde empezó a mirar.
    /// </summary>
    public static IReadOnlyList<string> From(IEnumerable<string?> summaries)
    {
        var seen = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? summary in summaries)
        {
            foreach (string member in InOne(summary))
            {
                if (known.Add(member))
                {
                    seen.Add(member);
                }
            }
        }

        return seen;
    }

    /// <summary>Lo revisado según UN resumen. Vacío si no sigue el formato acordado.</summary>
    public static IReadOnlyList<string> InOne(string? summary)
    {
        if (summary is null)
        {
            return Array.Empty<string>();
        }

        int start = summary.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return Array.Empty<string>();
        }

        string rest = summary[(start + Marker.Length)..];

        // La lista termina en el primer punto que cierra frase. Se busca un punto SEGUIDO DE
        // ESPACIO para no cortar en «Http.DefaultRequestHeaders» ni en un «etc.» a media lista.
        int end = rest.IndexOf(". ", StringComparison.Ordinal);
        if (end < 0 && rest.EndsWith('.'))
        {
            end = rest.Length - 1;
        }

        string list = end >= 0 ? rest[..end] : rest;

        var members = new List<string>();
        foreach (Match m in Identifier.Matches(list))
        {
            members.Add(m.Value);
        }

        return members;
    }
}
