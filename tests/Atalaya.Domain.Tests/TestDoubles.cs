using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Tests;

/// <summary>A clock whose "now" is set explicitly by the test.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }
}

/// <summary>
/// Deterministic ULID factory: emits strictly increasing ids from a fixed timestamp and
/// a monotonic counter, so tests get stable, sortable ids without touching the RNG.
/// </summary>
public sealed class TestUlidFactory : IUlidFactory
{
    private readonly DateTimeOffset _timestamp;
    private long _counter;

    public TestUlidFactory(DateTimeOffset? timestamp = null)
        => _timestamp = timestamp ?? DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    public Ulid NewUlid()
    {
        long n = ++_counter;
        Span<byte> random = stackalloc byte[10];
        for (int i = 0; i < 8; i++)
        {
            random[9 - i] = (byte)(n >> (8 * i));
        }

        return Ulid.Create(_timestamp, random);
    }
}
