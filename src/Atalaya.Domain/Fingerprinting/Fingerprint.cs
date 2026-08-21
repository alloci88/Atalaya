using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Atalaya.Domain.Fingerprinting;

/// <summary>
/// Semantic fingerprint of a finding (§2). It is the deduplication criterion and
/// the silence key, so — on the main path — it deliberately does NOT depend on the
/// LLM-authored title: it is <c>ruleId</c> + normalized primary path + containing
/// symbol. Only when the agent gives no symbol do we fall back to a normalized
/// title (lowercased, digits and repeated whitespace stripped) to keep two reports
/// of the same defect converging.
/// </summary>
public static partial class Fingerprint
{
    private const string Prefix = "sha256:";

    // Unit separator between fields — cannot appear in paths/rule ids/titles.
    private const char Sep = '';

    /// <summary>Computes the canonical <c>sha256:...</c> fingerprint string.</summary>
    public static string Compute(string ruleId, string primaryPath, string? symbol, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);

        string discriminator = !string.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim()
            : NormalizeTitle(title);

        string material = string.Concat(
            ruleId.Trim(), Sep,
            NormalizePath(primaryPath), Sep,
            discriminator);

        return Hash(material);
    }

    /// <summary>
    /// Normalizes a repo-relative path for stable hashing: forward slashes, no
    /// leading "./", collapsed duplicate slashes, trimmed. Case is preserved
    /// because git is case-sensitive.
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

    /// <summary>Title fallback normalization: lowercase, no digits, single-spaced.</summary>
    public static string NormalizeTitle(string title)
    {
        string t = (title ?? string.Empty).ToLowerInvariant();
        t = Digits().Replace(t, string.Empty);
        t = Whitespace().Replace(t, " ");
        return t.Trim();
    }

    /// <summary>Hash of an anchored snippet (line endings normalized, trailing space trimmed).</summary>
    public static string ComputeSnippetHash(string snippet)
    {
        string normalized = string.Join('\n',
            (snippet ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.TrimEnd()));
        return Hash(normalized);
    }

    private static string Hash(string material)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Prefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    [GeneratedRegex("/{2,}")]
    private static partial Regex MultiSlash();

    [GeneratedRegex("[0-9]")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
