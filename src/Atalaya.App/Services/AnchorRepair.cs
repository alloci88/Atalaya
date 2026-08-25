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

            int corrected = Correct(lines, loc);
            if (corrected == loc.Line || corrected < 1 || corrected > lines.Length)
            {
                continue;
            }

            loc.Line = corrected;
            loc.SnippetHash = CodeAnchor.ComputeSnippetHash(lines[corrected - 1]);
            repaired++;
        }

        if (repaired > 0)
        {
            _hub.Store.WriteFinding(slug, finding);
        }

        return repaired;
    }

    /// <summary>La línea a la que debería apuntar esta ubicación, o la que ya tiene.</summary>
    private static int Correct(string[] lines, Location loc)
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

        // El hash no está en el fichero: el código cambió de verdad. No se toca — es el único caso
        // en el que el aviso de la ficha tiene que salir.
        return anchored < 1 ? loc.Line : SymbolAnchor.FirstCodeLine(lines, loc.Path, anchored);
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
