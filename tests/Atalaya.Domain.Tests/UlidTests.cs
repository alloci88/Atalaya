using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using FluentAssertions;
using Xunit;

namespace Atalaya.Domain.Tests;

public class UlidTests
{
    private static byte[] Random(byte fill)
    {
        var b = new byte[10];
        Array.Fill(b, fill);
        return b;
    }

    [Fact]
    public void ToString_produces_26_crockford_chars()
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var ulid = Ulid.Create(ts, Random(0xAB));

        string text = ulid.ToString();

        text.Should().HaveLength(26);
        text.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{26}$");
    }

    [Fact]
    public void Parse_roundtrips_ToString()
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_712_345_678_901);
        var original = Ulid.Create(ts, Random(0x5C));

        var parsed = Ulid.Parse(original.ToString());

        parsed.Should().Be(original);
        parsed.TimestampMs.Should().Be(1_712_345_678_901);
    }

    [Fact]
    public void Timestamp_component_is_recoverable()
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var ulid = Ulid.Create(ts, Random(0x00));

        ulid.TimestampMs.Should().Be(1_700_000_000_000);
        ulid.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void Ordering_follows_timestamp_then_randomness()
    {
        var earlier = Ulid.Create(DateTimeOffset.FromUnixTimeMilliseconds(1000), Random(0xFF));
        var later = Ulid.Create(DateTimeOffset.FromUnixTimeMilliseconds(2000), Random(0x00));

        (earlier < later).Should().BeTrue();
        string.CompareOrdinal(earlier.ToString(), later.ToString()).Should().BeNegative();
    }

    [Fact]
    public void Factory_is_monotonic_within_the_same_millisecond()
    {
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeMilliseconds(42));
        var factory = new UlidFactory(clock);

        var ids = Enumerable.Range(0, 500).Select(_ => factory.NewUlid()).ToList();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Create_rejects_wrong_randomness_length()
    {
        var act = () => Ulid.Create(DateTimeOffset.UnixEpoch, new byte[9]);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("TOO-SHORT")]
    [InlineData("00000000000000000000000000000")]
    public void TryParse_rejects_malformed(string text)
    {
        Ulid.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_accepts_crockford_aliases()
    {
        // I/L map to 1 and O maps to 0; canonical form uses digits, so lowercase + aliases still parse.
        var original = Ulid.Create(DateTimeOffset.FromUnixTimeMilliseconds(1000), Random(0x11));
        string canonical = original.ToString();

        Ulid.TryParse(canonical.ToLowerInvariant(), out var lower).Should().BeTrue();
        lower.Should().Be(original);
    }
}
