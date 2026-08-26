using System.Text;

namespace Atalaya.Domain.Model;

/// <summary>
/// Propone el ejemplar de un patrón a partir del hallazgo que lo origina (F5.12): el título, sin
/// los nombres propios del caso concreto.
/// <para>
/// <b>Qué es y qué no es.</b> Esto NO decide si dos hallazgos son del mismo tipo —eso es del
/// auditor, y no hay ni habrá matching programático de similitud (anti-objetivo de F5.12)—. Es un
/// BORRADOR editable: el diálogo lo enseña en una caja de texto y el usuario lo pule antes de
/// confirmar. Por eso basta una heurística honesta y no hace falta un modelo: lo peor que puede
/// pasar es que el usuario reescriba la frase, que es exactamente lo que se espera que haga.
/// </para>
/// <para>
/// Lo que quita son los identificadores: <c>ReadCSV</c>, <c>Program.cs</c>, <c>Foo()</c>, el
/// símbolo del hallazgo — lo que ata la frase a un sitio y no al tipo de problema. «catch vacío en
/// ReadCSV oculta errores de parseo» sale como «catch vacío oculta errores de parseo». Si el
/// recorte se lo come todo, se devuelve el título tal cual: una caja vacía es peor punto de
/// partida que una frase demasiado concreta.
/// </para>
/// </summary>
public static class ExemplarDraft
{
    /// <summary>Palabras que solo estaban ahí para introducir el identificador que se ha quitado.</summary>
    private static readonly HashSet<string> Connectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "de", "del", "al", "a", "para", "por", "sobre", "dentro", "con", "la", "el", "los", "las", "un", "una",
    };

    /// <summary>Longitud mínima para creerse el recorte; por debajo se devuelve el título entero.</summary>
    private const int MinUseful = 8;

    /// <summary>
    /// El borrador. <paramref name="symbol"/> es el símbolo del hallazgo, si lo tiene: se quita
    /// aunque no parezca un identificador, porque por definición nombra el caso concreto.
    /// </summary>
    public static string Propose(string? title, string? symbol = null)
    {
        string source = (title ?? string.Empty).Trim();
        if (source.Length == 0)
        {
            return string.Empty;
        }

        string cleaned = StripSpans(source);
        var kept = new List<string>();
        foreach (string token in cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (LooksLikeIdentifier(token, symbol))
            {
                // El conector que lo introducía se va con él: «catch vacío en ReadCSV» no puede
                // quedarse en «catch vacío en».
                if (kept.Count > 0 && Connectors.Contains(Trim(kept[^1])))
                {
                    kept.RemoveAt(kept.Count - 1);
                }

                continue;
            }

            kept.Add(token);
        }

        while (kept.Count > 0 && Connectors.Contains(Trim(kept[^1])))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        string result = string.Join(' ', kept).Trim().Trim(',', ';', ':', '.', '-', '–', '—');
        return result.Length >= MinUseful ? result : source;
    }

    /// <summary>Fuera lo que va entre paréntesis, corchetes o comillas invertidas: es cita literal.</summary>
    private static string StripSpans(string text)
    {
        var sb = new StringBuilder(text.Length);
        int depth = 0;
        bool inCode = false;
        foreach (char c in text)
        {
            if (c == '`')
            {
                inCode = !inCode;
                sb.Append(' ');
                continue;
            }

            if (c is '(' or '[')
            {
                depth++;
                sb.Append(' ');
                continue;
            }

            if (c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
                sb.Append(' ');
                continue;
            }

            sb.Append(depth > 0 || inCode ? ' ' : c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Un token «nombra un sitio concreto» si es el símbolo del hallazgo, si lleva un punto
    /// pegado (ruta, fichero, miembro cualificado), si tiene guion bajo, o si tiene una mayúscula
    /// que no es la inicial (<c>ReadCSV</c>, <c>miVariable</c>). Una palabra normal en español no
    /// cumple ninguna.
    /// </summary>
    private static bool LooksLikeIdentifier(string token, string? symbol)
    {
        string t = Trim(token);
        if (t.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(symbol) && string.Equals(t, symbol!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (t.Contains('.') || t.Contains('_') || t.Contains('/') || t.Contains('\\'))
        {
            return true;
        }

        for (int i = 1; i < t.Length; i++)
        {
            if (char.IsUpper(t[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>El token sin la puntuación que lo rodea: «ReadCSV,» sigue siendo un identificador.</summary>
    private static string Trim(string token) => token.Trim(',', ';', ':', '.', '«', '»', '"', '\'', '¿', '?', '¡', '!');
}
