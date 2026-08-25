using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Atalaya.Domain.Anchoring;

/// <summary>
/// Anclaje de código: normalización de rutas y hash de snippet.
/// <para>
/// F4: lo que queda del antiguo <c>Fingerprint</c>. La identidad semántica de un hallazgo ya NO
/// se computa (es su ULID, y la decide el auditor vía <c>report_verdicts</c>); estas dos
/// operaciones sobreviven porque NO tienen nada que ver con identidad: normalizar una ruta es
/// necesario para comparar ubicaciones entre sistemas de ficheros, y el hash de snippet es el
/// ancla que permite re-localizar una línea que se ha movido (§5.4 verify).
/// </para>
/// </summary>
public static partial class CodeAnchor
{
    /// <summary>
    /// Normaliza una ruta relativa al repo: barras hacia delante, sin "./" inicial, sin barras
    /// duplicadas, recortada. La caja se preserva porque git distingue mayúsculas.
    /// </summary>
    public static string NormalizePath(string path)
    {
        string p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        p = MultiSlash().Replace(p, "/");
        return p.Trim('/');
    }

    /// <summary>
    /// Hash de un snippet anclado: finales de línea normalizados y cada línea recortada por
    /// <b>los dos lados</b>, sin las líneas en blanco de los extremos.
    /// <para>
    /// <b>Por qué por los dos lados</b> (D-217). Las dos puntas que usan este hash no ven el mismo
    /// texto: al ingerir se hashea el snippet que manda el LLM, que llega <b>sin la sangría</b> del
    /// fichero, y al mostrar se hashea la línea <b>cruda</b>, con sus ocho o doce espacios delante.
    /// Recortando solo por la derecha —como se hacía— ninguna línea de dentro de una clase podía
    /// casar jamás, y la ficha avisaba de que «el código ha cambiado» en 53 de 56 ubicaciones sobre
    /// un repositorio intacto. Los hashes ya guardados siguen valiendo: se calcularon sobre un texto
    /// que ya venía sin sangría.
    /// </para>
    /// <para>
    /// El precio es que dos líneas idénticas con sangrías distintas colisionan. Quien busca por
    /// hash lo paga quedándose con la candidata más cercana a la línea guardada, no con la primera.
    /// </para>
    /// </summary>
    public static string ComputeSnippetHash(string snippet)
    {
        string[] lines = (snippet ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray();

        int first = 0;
        int last = lines.Length - 1;
        while (first <= last && lines[first].Length == 0)
        {
            first++;
        }

        while (last >= first && lines[last].Length == 0)
        {
            last--;
        }

        string normalized = first > last ? string.Empty : string.Join('\n', lines[first..(last + 1)]);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    [GeneratedRegex("/{2,}")]
    private static partial Regex MultiSlash();
}
