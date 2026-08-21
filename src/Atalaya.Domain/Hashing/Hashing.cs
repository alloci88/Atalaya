using System.Security.Cryptography;
using System.Text;
using Atalaya.Domain.Fingerprinting;

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
    public static string UnitHash(string unitPath) => Sha256Hex(Fingerprint.NormalizePath(unitPath));
}
