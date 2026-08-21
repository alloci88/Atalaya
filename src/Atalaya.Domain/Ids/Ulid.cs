using System.Diagnostics.CodeAnalysis;

namespace Atalaya.Domain.Ids;

/// <summary>
/// A 128-bit ULID (Universally Unique Lexicographically Sortable Identifier).
/// 48-bit millisecond timestamp + 80 bits of randomness, rendered as 26
/// Crockford base32 characters. Sorts lexicographically by creation time.
/// Implemented in-house so <c>Atalaya.Domain</c> stays dependency-free (D-002).
/// </summary>
public readonly struct Ulid : IEquatable<Ulid>, IComparable<Ulid>
{
    // Crockford base32 alphabet (excludes I, L, O, U to avoid ambiguity).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int TextLength = 26;

    // The 128-bit value split into two big-endian halves.
    // _hi holds bytes 0..7 (top 48 bits = timestamp, next 16 bits = randomness).
    // _lo holds bytes 8..15 (the remaining 64 bits of randomness).
    private readonly ulong _hi;
    private readonly ulong _lo;

    private Ulid(ulong hi, ulong lo)
    {
        _hi = hi;
        _lo = lo;
    }

    /// <summary>The all-zero ULID (sorts first). Used as a sentinel.</summary>
    public static Ulid Empty => new(0, 0);

    /// <summary>Milliseconds since the Unix epoch encoded in the top 48 bits.</summary>
    public long TimestampMs => (long)(_hi >> 16);

    /// <summary>The creation instant recovered from the timestamp component.</summary>
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);

    /// <summary>
    /// Builds a ULID from an explicit timestamp and exactly 10 random bytes.
    /// Deterministic — the whole point is that tests can pin both inputs.
    /// </summary>
    public static Ulid Create(DateTimeOffset timestamp, ReadOnlySpan<byte> randomness)
    {
        if (randomness.Length != 10)
        {
            throw new ArgumentException("ULID randomness must be exactly 10 bytes.", nameof(randomness));
        }

        long ms = timestamp.ToUnixTimeMilliseconds();
        if (ms < 0 || ms > 0xFFFF_FFFF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamp does not fit in 48 bits.");
        }

        // hi = [48-bit time][16 bits of randomness]
        ulong hi = ((ulong)ms << 16) | ((ulong)randomness[0] << 8) | randomness[1];
        // lo = the last 8 random bytes, big-endian.
        ulong lo = 0;
        for (int i = 2; i < 10; i++)
        {
            lo = (lo << 8) | randomness[i];
        }

        return new Ulid(hi, lo);
    }

    /// <summary>Writes the 16-byte big-endian representation into <paramref name="dest"/>.</summary>
    public void WriteBytes(Span<byte> dest)
    {
        if (dest.Length < 16)
        {
            throw new ArgumentException("Destination must be at least 16 bytes.", nameof(dest));
        }

        for (int i = 0; i < 8; i++)
        {
            dest[i] = (byte)(_hi >> (56 - 8 * i));
            dest[8 + i] = (byte)(_lo >> (56 - 8 * i));
        }
    }

    /// <summary>The canonical 26-character Crockford base32 text form.</summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[16];
        WriteBytes(bytes);

        Span<char> chars = stackalloc char[TextLength];
        // 26 chars * 5 bits = 130 bits; the value is 128 bits, so pad 2 leading zero bits.
        const int pad = TextLength * 5 - 128;
        for (int c = 0; c < TextLength; c++)
        {
            int group = 0;
            for (int b = 0; b < 5; b++)
            {
                int globalBit = c * 5 + b - pad;
                int bit = 0;
                if (globalBit >= 0)
                {
                    bit = (bytes[globalBit / 8] >> (7 - globalBit % 8)) & 1;
                }

                group = (group << 1) | bit;
            }

            chars[c] = Alphabet[group];
        }

        return new string(chars);
    }

    /// <summary>Parses a 26-character canonical ULID; throws on malformed input.</summary>
    public static Ulid Parse(string text)
    {
        if (!TryParse(text, out Ulid result))
        {
            throw new FormatException($"'{text}' is not a valid ULID.");
        }

        return result;
    }

    /// <summary>Case-insensitive, tolerant of Crockford I/L/O aliases. Never throws.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out Ulid result)
    {
        result = default;
        if (text is null || text.Length != TextLength)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        const int pad = TextLength * 5 - 128;

        for (int c = 0; c < TextLength; c++)
        {
            int v = DecodeChar(text[c]);
            if (v < 0)
            {
                return false;
            }

            for (int b = 0; b < 5; b++)
            {
                int bit = (v >> (4 - b)) & 1;
                int globalBit = c * 5 + b - pad;
                if (globalBit < 0)
                {
                    // These are the padding bits; the canonical form requires them zero.
                    if (bit != 0)
                    {
                        return false;
                    }

                    continue;
                }

                if (bit != 0)
                {
                    bytes[globalBit / 8] |= (byte)(1 << (7 - globalBit % 8));
                }
            }
        }

        ulong hi = 0, lo = 0;
        for (int i = 0; i < 8; i++)
        {
            hi = (hi << 8) | bytes[i];
            lo = (lo << 8) | bytes[8 + i];
        }

        result = new Ulid(hi, lo);
        return true;
    }

    private static int DecodeChar(char c)
    {
        return char.ToUpperInvariant(c) switch
        {
            >= '0' and <= '9' => c - '0',
            'A' => 10, 'B' => 11, 'C' => 12, 'D' => 13, 'E' => 14, 'F' => 15,
            'G' => 16, 'H' => 17, 'J' => 18, 'K' => 19, 'M' => 20, 'N' => 21,
            'P' => 22, 'Q' => 23, 'R' => 24, 'S' => 25, 'T' => 26, 'V' => 27,
            'W' => 28, 'X' => 29, 'Y' => 30, 'Z' => 31,
            // Crockford aliases for human-entered text.
            'I' or 'L' => 1,
            'O' => 0,
            _ => -1,
        };
    }

    public int CompareTo(Ulid other)
    {
        int hi = _hi.CompareTo(other._hi);
        return hi != 0 ? hi : _lo.CompareTo(other._lo);
    }

    public bool Equals(Ulid other) => _hi == other._hi && _lo == other._lo;

    public override bool Equals(object? obj) => obj is Ulid other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_hi, _lo);

    public static bool operator ==(Ulid left, Ulid right) => left.Equals(right);

    public static bool operator !=(Ulid left, Ulid right) => !left.Equals(right);

    public static bool operator <(Ulid left, Ulid right) => left.CompareTo(right) < 0;

    public static bool operator >(Ulid left, Ulid right) => left.CompareTo(right) > 0;

    public static bool operator <=(Ulid left, Ulid right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Ulid left, Ulid right) => left.CompareTo(right) >= 0;
}
