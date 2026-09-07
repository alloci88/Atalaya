using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Deja las ubicaciones de un hallazgo apuntando a donde está el código <b>de verdad</b>, y lo
/// guarda (F5.6 §2, D-226).
/// <para>
/// <b>Por qué persistir y no solo pintar.</b> Con el hash arreglado (D-219) la ficha ya encontraba
/// la línea buena, pero lo anunciaba —«se anotó en la 167 y su código está en la 172»— en 24 de los
/// 25 hallazgos del hub. Un aviso que sale siempre no avisa de nada: es exactamente el banner que
/// el usuario reportó, con otro texto. Y no había nada que anunciar, porque el código no se había
/// movido: la línea nació torcida. Corrigiéndola en disco, la próxima lectura dice «anclado» y
/// calla, y el aviso vuelve a significar lo que tiene que significar.
/// </para>
/// <para>
/// <b>Solo se corrige lo que se puede demostrar.</b> Dos correcciones, y ninguna más: (1) el hash
/// aparece en otra línea del fichero —es el mismo texto, letra por letra, así que esa es la línea—;
/// y (2) la línea anclada no es código ejecutable —un comentario de documentación, un atributo, una
/// llave— y se baja a la primera del miembro que sí lo es. Cuando el hash <b>no aparece</b> no se
/// toca nada: eso sí es código cambiado, y ahí el aviso y el «Verificar ahora» son la respuesta
/// correcta. Reanclar por símbolo en disco silenciaría para siempre el único caso en el que hace
/// falta una persona.
/// </para>
/// </summary>
public sealed class AnchorRepair
{
    private readonly HubContext _hub;

    public AnchorRepair(HubContext hub) => _hub = hub;

    /// <summary>
    /// Corrige y persiste las ubicaciones de <paramref name="finding"/> contra el clon. Devuelve
    /// cuántas movió; <c>0</c> —el caso normal a partir de la segunda vez— no escribe nada.
    /// </summary>
    public int Repair(string slug, Finding finding, string? clonePath)
    {
        if (string.IsNullOrWhiteSpace(clonePath))
        {
            return 0;
        }

        int repaired = 0;
        var cache = new Dictionary<string, string[]?>(StringComparer.Ordinal);

        foreach (Location loc in finding.Locations)
        {
            string[]? lines = Read(cache, clonePath, loc.Path);
            if (lines is null || lines.Length == 0)
            {
                continue;
            }

            (int corrected, bool rehash) = Correct(lines, loc);
            if (corrected == loc.Line || corrected < 1 || corrected > lines.Length)
            {
                continue;
            }

            loc.Line = corrected;

            // BUGFIX-ANCLA — EL HASH SOLO SE REESCRIBE SI EL ANCLA SE ENCONTRÓ. Cuando no está
            // en el fichero, el texto auditado se perdió y calcular un hash nuevo sobre la
            // línea a la que se baja fabricaría un ancla a código que nadie auditó: la próxima
            // lectura diría «anclado» y el aviso desaparecería para siempre, que es justo lo
            // que D-226 prohíbe. Se mueve el número —para que deje de ser una llave— y se deja
            // el ancla: el par pasa a decir «el texto auditado era éste, y donde vivía es
            // ésta», que es la verdad. Un test de R13 §0(b) cazó la primera versión de esto.
            if (rehash)
            {
                loc.SnippetHash = CodeAnchor.ComputeSnippetHash(lines[corrected - 1]);
            }

            repaired++;
        }

        if (repaired > 0)
        {
            _hub.Store.WriteFinding(slug, finding);
        }

        return repaired;
    }

    /// <summary>
    /// La línea a la que debería apuntar esta ubicación —o la que ya tiene—, y si el ancla se
    /// puede recalcular sobre ella.
    /// </summary>
    private static (int Line, bool Rehash) Correct(string[] lines, Location loc)
    {
        bool inRange = loc.Line >= 1 && loc.Line <= lines.Length;

        int anchored;
        if (string.IsNullOrEmpty(loc.SnippetHash))
        {
            // Sin ancla no hay nada que demostrar; como mucho, sacarla de un comentario.
            anchored = inRange ? loc.Line : 0;
        }
        else if (inRange && CodeAnchor.ComputeSnippetHash(lines[loc.Line - 1]) == loc.SnippetHash)
        {
            anchored = loc.Line;
        }
        else
        {
            anchored = LocationAnchor.FindByHash(lines, loc.SnippetHash, loc.Line);
        }

        // BUGFIX-ANCLA — LOS DOS CASOS DE D-226 SON INDEPENDIENTES, y hasta aquí el (2) colgaba
        // del (1): cuando el hash no aparecía se devolvía la línea sin más, así que un hallazgo
        // anclado a una llave se quedaba anclado a una llave para siempre. Medido: 69 de 398
        // ubicaciones del hub de xblast. Bajar una llave a la primera línea ejecutable de su
        // miembro **no** es re-anclar por símbolo —eso es lo que D-226 prohíbe, y sigue
        // prohibido—: es el mismo hecho demostrable del caso (2), y se puede demostrar sin el
        // hash. Lo que sigue intacto es que un hash ausente **no mueve la ubicación de miembro**:
        // se queda dentro del que ya tenía, y el aviso y el «Verificar ahora» siguen saliendo.
        bool found = anchored >= 1;
        int start = found ? anchored : loc.Line;
        return (SymbolAnchor.FirstCodeLine(lines, loc.Path, start), found);
    }

    private static string[]? Read(Dictionary<string, string[]?> cache, string clonePath, string path)
    {
        if (cache.TryGetValue(path, out string[]? cached))
        {
            return cached;
        }

        string[]? lines = null;
        try
        {
            string abs = Path.Combine(clonePath, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(abs))
            {
                lines = File.ReadAllLines(abs);
            }
        }
        catch (Exception)
        {
            // Un fichero ilegible no impide abrir la ficha: se deja la ubicación como está.
        }

        cache[path] = lines;
        return lines;
    }
}
