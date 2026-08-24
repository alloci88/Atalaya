using System.Text;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Model;

namespace Atalaya.Domain.Ingestion;

/// <summary>
/// F3.1 Bloque 1 — matching de 2ª pasada.
/// <para>
/// Los hallazgos importados del v4 llevan fingerprint por título; los payloads nuevos usan
/// <c>ruleId</c>. Cuando la búsqueda por fingerprint no encuentra activo, esta clase intenta
/// emparejar el payload con un hallazgo existente de la MISMA unidad por
/// <b>ruta normalizada + solape de título</b>. Si el score supera <see cref="Threshold"/>,
/// el llamador debe tratarlo como reconfirmación y migrar el fingerprint antiguo a
/// <see cref="Finding.PreviousFingerprints"/> (nunca borrar el hallazgo).
/// </para>
/// <para>
/// Piloto 2026-08-24 sobre <c>CommonStatics.cs</c>: 4 de 5 "nuevos" eran el mismo problema
/// que un "resuelto" de la sesión anterior. La regla se calibró contra esa evidencia real.
/// </para>
/// </summary>
public static class SecondPassMatcher
{
    /// <summary>Umbral mínimo de similitud de título (Jaccard sobre tokens normalizados)
    /// para aceptar una reconfirmación por 2ª pasada. Calibrado contra el piloto (los 4 pares
    /// legítimos superaban 0,55; el falso positivo del <c>criterio.seguridad</c> queda por debajo).</summary>
    public const double Threshold = 0.5;

    /// <summary>
    /// Busca el mejor candidato entre <paramref name="candidatesInApp"/> para
    /// <paramref name="submitted"/>. Devuelve <c>null</c> si nada supera el umbral.
    /// Los candidatos deben ser TODOS los hallazgos de la app (la clase filtra internamente
    /// por ruta y estado); pasar sólo activos convertiría el matching en ciego a resueltos,
    /// que es justo el caso que queremos capturar.
    /// </summary>
    public static SecondPassMatch? TryMatch(SubmittedFinding submitted, IReadOnlyList<Finding> candidatesInApp)
    {
        string normPath = Fingerprint.NormalizePath(submitted.PrimaryPath);
        HashSet<string> newTokens = Tokenize(submitted.Title);
        if (newTokens.Count == 0)
        {
            return null;
        }

        SecondPassMatch? best = null;
        foreach (Finding f in candidatesInApp)
        {
            if (f.Locations.Count == 0)
            {
                continue;
            }

            // Ruta: alguna location debe coincidir con la del payload (misma unidad).
            bool samePath = false;
            foreach (Location loc in f.Locations)
            {
                if (Fingerprint.NormalizePath(loc.Path) == normPath)
                {
                    samePath = true;
                    break;
                }
            }

            if (!samePath)
            {
                continue;
            }

            HashSet<string> oldTokens = Tokenize(f.Title);
            double score = Jaccard(newTokens, oldTokens);
            if (score < Threshold)
            {
                continue;
            }

            if (best is null || score > best.Score)
            {
                best = new SecondPassMatch(f, score);
            }
        }

        return best;
    }

    /// <summary>Tokeniza un título: minúsculas, sin acentos, sin puntuación, palabras &gt;=3 chars
    /// y sin stopwords castellanas comunes. Preserva identificadores de código
    /// (<c>StringToByteArray</c> → {stringtobytearray}) porque justo esos son la señal fuerte.</summary>
    internal static HashSet<string> Tokenize(string? title)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(title))
        {
            return result;
        }

        string norm = RemoveDiacritics(title.ToLowerInvariant());
        var current = new StringBuilder();
        foreach (char c in norm)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                AddToken(result, current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            AddToken(result, current.ToString());
        }

        return result;
    }

    private static void AddToken(HashSet<string> set, string token)
    {
        if (token.Length < 3)
        {
            return;
        }

        if (Stopwords.Contains(token))
        {
            return;
        }

        set.Add(token);
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        int intersection = 0;
        foreach (string s in a)
        {
            if (b.Contains(s))
            {
                intersection++;
            }
        }

        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string RemoveDiacritics(string text)
    {
        string form = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(form.Length);
        foreach (char c in form)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "que", "para", "por", "con", "sin", "los", "las", "una", "uno", "del", "des",
        "the", "and", "for", "with", "not", "can", "may", "esto", "esta", "este",
        "cuando", "donde", "como", "pero", "sobre",
    };
}

/// <summary>Resultado positivo del matching de 2ª pasada.</summary>
/// <param name="Finding">Hallazgo existente a reconfirmar/migrar.</param>
/// <param name="Score">Similitud de título 0..1 (Jaccard sobre tokens).</param>
public sealed record SecondPassMatch(Finding Finding, double Score);
