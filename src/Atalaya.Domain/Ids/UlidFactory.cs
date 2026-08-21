using System.Security.Cryptography;
using Atalaya.Domain.Abstractions;

namespace Atalaya.Domain.Ids;

/// <summary>Creates new ULIDs. Injectable so tests can pin time and randomness.</summary>
public interface IUlidFactory
{
    Ulid NewUlid();
}

/// <summary>
/// Default factory: uses the injected <see cref="IClock"/> for the timestamp and a
/// cryptographic RNG for the randomness. Guarantees strict monotonicity within a
/// single millisecond by incrementing the previous randomness (ULID spec, §5.3),
/// so ids created in a tight loop still sort in creation order.
/// </summary>
public sealed class UlidFactory : IUlidFactory
{
    private readonly IClock _clock;
    private readonly RandomNumberGenerator _rng;
    private readonly object _gate = new();
    private long _lastMs = -1;
    private readonly byte[] _lastRandom = new byte[10];

    public UlidFactory(IClock clock, RandomNumberGenerator? rng = null)
    {
        _clock = clock;
        _rng = rng ?? RandomNumberGenerator.Create();
    }

    public Ulid NewUlid()
    {
        lock (_gate)
        {
            long ms = _clock.UtcNow.ToUnixTimeMilliseconds();
            Span<byte> random = stackalloc byte[10];

            if (ms == _lastMs)
            {
                // Same millisecond: increment the last randomness to preserve monotonicity.
                IncrementBigEndian(_lastRandom);
                _lastRandom.CopyTo(random);
            }
            else
            {
                _rng.GetBytes(random);
                _lastMs = ms;
                random.CopyTo(_lastRandom);
            }

            return Ulid.Create(DateTimeOffset.FromUnixTimeMilliseconds(ms), random);
        }
    }

    private static void IncrementBigEndian(byte[] value)
    {
        for (int i = value.Length - 1; i >= 0; i--)
        {
            if (++value[i] != 0)
            {
                return;
            }
            // carry into the next byte on overflow
        }
        // Overflow of all 80 bits within one ms is astronomically unlikely; wrap silently.
    }
}
