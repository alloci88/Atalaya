namespace Atalaya.Domain.Abstractions;

/// <summary>
/// Abstraction over "now" so the domain (ULID generation, timestamps, TTLs) is
/// deterministically testable. All domain timestamps are UTC.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real wall clock. Injected in production via DI.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
