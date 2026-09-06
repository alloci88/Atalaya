using System.Text;
using System.Text.RegularExpressions;

namespace Atalaya.Agents;

/// <summary>
/// Los argumentos de UNA llamada a herramienta, contados <b>según el modelo los escribe</b>
/// (F30 §2).
/// <para>
/// <b>Vive en el vocabulario común y no en cada casa</b> porque la regla de contar es la misma para
/// las dos y no tiene nada de específico: lo que cambia es el evento por el que llegan los trozos
/// —<c>input_json_delta</c> en Claude Code, <c>AssistantToolCallDeltaEvent.InputDelta</c> en
/// Copilot—, no qué se cuenta. Dos copias acabarían contando distinto, y entonces la misma sesión
/// diría «3 hallazgos» con una casa y «4» con la otra.
/// </para>
/// <para>
/// <b>No se parsea el JSON.</b> Lo que llega está a medias por definición —un array que todavía se
/// está escribiendo no es JSON válido—, así que deserializarlo fallaría en cada trozo. Se cuenta lo
/// único que se puede contar con certeza: los valores de una clave que ya han CERRADO su comilla.
/// Un elemento a medio escribir no cuenta hasta que su título está entero, que es exactamente lo
/// que se quiere enseñar — un título cortado por la mitad no informa de nada.
/// </para>
/// </summary>
public sealed class ToolCallInput
{
    /// <summary>
    /// Qué clave marca «un elemento más», por herramienta. Es el campo que identifica al elemento
    /// para quien lo lee: el título de un hallazgo, el veredicto de una reconciliación, la ruta de
    /// una ubicación.
    /// </summary>
    private static readonly Dictionary<string, string> Marker = new(StringComparer.Ordinal)
    {
        ["submit_findings"] = "title",
        ["submit_finding"] = "title",
        ["report_verdicts"] = "verdict",
        ["add_locations"] = "path",
    };

    private readonly StringBuilder _json = new();
    private readonly Regex? _key;

    public ToolCallInput(string tool)
    {
        Tool = tool;
        _key = Marker.TryGetValue(tool, out string? key)
            ? new Regex("\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")
            : null;
    }

    /// <summary>El nombre corto de la herramienta, ya sin el prefijo del servidor.</summary>
    public string Tool { get; }

    /// <summary>Elementos cuya clave ya ha cerrado.</summary>
    public int Items { get; private set; }

    /// <summary>El último que se completó, para poder enseñarlo.</summary>
    public string? Last { get; private set; }

    /// <summary>
    /// El nombre de la herramienta sin el prefijo del servidor MCP: de
    /// <c>mcp__atalaya__submit_findings</c> a <c>submit_findings</c>. Lo que se enseña es la
    /// herramienta, no por dónde llegó.
    /// </summary>
    public static string Short(string name)
    {
        int at = name.LastIndexOf("__", StringComparison.Ordinal);
        return at < 0 ? name : name[(at + 2)..];
    }

    /// <summary>
    /// Añade un trozo. Devuelve <c>true</c> <b>solo</b> si con él se ha completado un elemento
    /// nuevo: así el hilo se repinta cuando hay algo que decir y no con cada puñado de caracteres —
    /// que a 64 tokens por segundo serían decenas de repintados por segundo sin información nueva.
    /// </summary>
    public bool Append(string chunk)
    {
        if (chunk.Length == 0 || _key is null)
        {
            return false;
        }

        _json.Append(chunk);
        MatchCollection found = _key.Matches(_json.ToString());
        if (found.Count <= Items)
        {
            return false;
        }

        Items = found.Count;
        Last = found[^1].Groups[1].Value;
        return true;
    }
}
