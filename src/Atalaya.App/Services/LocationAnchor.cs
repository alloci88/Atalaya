using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Dónde está de verdad el código de una ubicación guardada (F5.6, defectos 1 y 2).
/// <para>
/// <b>El ancla buena es el hash, no el número de línea.</b> Las líneas las emite el LLM al
/// reportar y son aproximadas: sobre las 56 ubicaciones reales que motivaron esta tanda el desfase
/// iba de +1 a +25 líneas (D-220), y el caso que abrió el parte apuntaba a un comentario de
/// documentación. El <c>snippetHash</c>, en cambio, identifica el texto exacto que el auditor
/// miró, así que buscarlo en el fichero devuelve la línea correcta sin adivinar nada.
/// </para>
/// <para>
/// Desde que <see cref="CodeAnchor.ComputeSnippetHash"/> recorta por los dos lados (D-219) dos
/// líneas idénticas con sangrías distintas colisionan. Por eso, entre varias candidatas, se elige
/// <b>la más cercana a la línea guardada</b>: si el hallazgo decía «línea 167» y el mismo texto
/// aparece en la 172 y en la 340, la que quería decir es la 172.
/// </para>
/// </summary>
public static class LocationAnchor
{
    /// <summary>
    /// La línea (1-based) cuyo contenido casa con <paramref name="snippetHash"/>, la más cercana a
    /// <paramref name="near"/>; <c>0</c> si el hash no aparece en el fichero.
    /// </summary>
    public static int FindByHash(IReadOnlyList<string> lines, string? snippetHash, int near)
    {
        if (string.IsNullOrEmpty(snippetHash) || lines.Count == 0)
        {
            return 0;
        }

        int best = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < lines.Count; i++)
        {
            if (CodeAnchor.ComputeSnippetHash(lines[i]) != snippetHash)
            {
                continue;
            }

            int distance = Math.Abs(i + 1 - near);
            if (distance < bestDistance)
            {
                best = i + 1;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// La línea que hay que <b>persistir</b> para una ubicación recién reportada (D-226): si el
    /// snippet aparece en el clon, la línea real; si no —sin clon, fichero ausente, snippet que ya
    /// no está— la que dijo el auditor, tal cual. Nunca inventa: solo corrige lo que puede
    /// comprobar.
    /// </summary>
    /// <summary>
    /// La ubicación bajada a la primera línea <b>ejecutable</b> del miembro que la contiene, con
    /// su hash recalculado sobre esa línea (BUGFIX-ANCLA).
    /// <para>
    /// Es el caso (2) de D-226 aplicado <b>donde nace el dato</b>. Una llave de cierre no es el
    /// hallazgo: es el final del método de al lado, y un hash calculado sobre <c>}</c> no vuelve
    /// a casar con nada. Si la línea ya era código, se devuelve la ubicación tal cual —esto es
    /// idempotente— y si no hay clon, fichero o miembro alrededor, también.
    /// </para>
    /// </summary>
    public static Location OnFirstCodeLine(
        string? clonePath, string path, int line, string? snippetHash)
    {
        if (string.IsNullOrWhiteSpace(clonePath))
        {
            return new Location(path, line, snippetHash);
        }

        try
        {
            string abs = Path.Combine(clonePath, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                return new Location(path, line, snippetHash);
            }

            string[] lines = File.ReadAllLines(abs);
            int code = SymbolAnchor.FirstCodeLine(lines, path, line);
            return code == line || code < 1 || code > lines.Length
                ? new Location(path, line, snippetHash)
                : new Location(path, code, CodeAnchor.ComputeSnippetHash(lines[code - 1]));
        }
        catch (Exception)
        {
            // Bajar la línea es una mejora, no un requisito: si el disco falla se guarda lo que
            // había, igual que hace ResolveOnDisk.
            return new Location(path, line, snippetHash);
        }
    }

    public static int ResolveOnDisk(string? clonePath, string path, int line, string? snippetHash)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || string.IsNullOrEmpty(snippetHash))
        {
            return line;
        }

        try
        {
            string abs = Path.Combine(clonePath, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                return line;
            }

            int found = FindByHash(File.ReadAllLines(abs), snippetHash, line);
            return found > 0 ? found : line;
        }
        catch (Exception)
        {
            // Re-anclar es una mejora, no un requisito: si el disco falla se guarda lo reportado.
            return line;
        }
    }
}
