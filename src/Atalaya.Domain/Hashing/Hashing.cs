using System.Security.Cryptography;
using System.Text;
using Atalaya.Domain.Anchoring;

namespace Atalaya.Domain.Hashing;

/// <summary>General-purpose SHA-256 helpers used for content hashes and unit identity.</summary>
public static class HashUtil
{
    /// <summary>Lowercase hex SHA-256 of a UTF-8 string, prefixed <c>sha256:</c>.</summary>
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>Lowercase hex SHA-256 of raw bytes, prefixed <c>sha256:</c>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Unit identity hash (§2): SHA-256 of the normalized unit path. Stable across
    /// cycles and edits (it is NOT the content hash), so a claim survives edits.
    /// This is the name used for <c>claims/{unitHash}.json</c>.
    /// </summary>
    public static string UnitHash(string unitPath) => Sha256Hex(CodeAnchor.NormalizePath(unitPath));

    /// <summary>
    /// Huella del CONTENIDO de un fichero con los finales de línea normalizados a LF (F9 §2).
    /// <para>
    /// Es la huella con la que se reconoce el commit que recogió un arreglo de la aplicación, y por
    /// eso tiene que valer igual a los dos lados de git: el fichero del árbol de trabajo puede tener
    /// CRLF por <c>core.autocrlf</c> mientras el blob guardado tiene LF, y son el mismo contenido.
    /// Sin normalizar, cada máquina con autocrlf distinto dejaría de reconocer sus propios arreglos.
    /// </para>
    /// <para>
    /// Deliberadamente NO es <see cref="Model.InventoryUnit.ContentHash"/>, que es de bytes crudos
    /// porque mide el fichero de disco y ahí un CRLF sí es una diferencia real.
    /// </para>
    /// </summary>
    public static string NormalizedContentHash(ReadOnlySpan<byte> bytes)
    {
        // Se copia solo lo que sobrevive: el CR de un par CR-LF se cae, y un CR suelto —un fichero
        // de Mac clásico— se respeta, porque ahí no hay ninguna traducción de git que deshacer.
        var normalized = new byte[bytes.Length];
        int n = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                continue;
            }

            normalized[n++] = bytes[i];
        }

        return Sha256Hex(normalized.AsSpan(0, n));
    }
}
