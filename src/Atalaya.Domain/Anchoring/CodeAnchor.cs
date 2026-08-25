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

    /// <summary>Hash de un snippet anclado (finales de línea normalizados, espacio final recortado).</summary>
    public static string ComputeSnippetHash(string snippet)
    {
        string normalized = string.Join('\n',
            (snippet ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.TrimEnd()));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    [GeneratedRegex("/{2,}")]
    private static partial Regex MultiSlash();
}
