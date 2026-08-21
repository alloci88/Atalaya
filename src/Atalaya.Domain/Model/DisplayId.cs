namespace Atalaya.Domain.Model;

/// <summary>
/// Helpers for the human-readable presentation alias (§2): a per-pillar prefix plus a
/// zero-padded per-app counter (e.g. BUG-0042). The alias is NOT identity — the ULID is.
/// </summary>
public static class DisplayId
{
    /// <summary>The alias prefix for a pillar: OPT / MEJ / BUG.</summary>
    public static string Prefix(Pillar pillar) => pillar switch
    {
        Pillar.Optimizacion => "OPT",
        Pillar.Mejoras => "MEJ",
        Pillar.Errores => "BUG",
        _ => throw new ArgumentOutOfRangeException(nameof(pillar)),
    };

    /// <summary>Formats "PREFIX-0042" from a pillar and a 1-based sequence number.</summary>
    public static string Format(Pillar pillar, int sequence) => $"{Prefix(pillar)}-{sequence:D4}";

    /// <summary>
    /// Advances the per-pillar counter in <paramref name="counters"/> and returns the next alias.
    /// Mutates the dictionary. Call only after a successful push (§2).
    /// </summary>
    public static string Next(IDictionary<string, int> counters, Pillar pillar)
    {
        string prefix = Prefix(pillar);
        int next = (counters.TryGetValue(prefix, out int last) ? last : 0) + 1;
        counters[prefix] = next;
        return Format(pillar, next);
    }
}
