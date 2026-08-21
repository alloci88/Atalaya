using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atalaya.Domain.Ids;

namespace Atalaya.Storage.Json;

/// <summary>Serializes <see cref="Ulid"/> as its canonical 26-char text form.</summary>
public sealed class UlidJsonConverter : JsonConverter<Ulid>
{
    public override Ulid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? s = reader.GetString();
        if (Ulid.TryParse(s, out Ulid value))
        {
            return value;
        }

        throw new JsonException($"'{s}' is not a valid ULID.");
    }

    public override void Write(Utf8JsonWriter writer, Ulid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>
/// Serializes <see cref="DateTimeOffset"/> as UTC ISO-8601 with a trailing <c>Z</c>
/// (all Atalaya timestamps are UTC), matching the schema examples in §2.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? s = reader.GetString();
        return DateTimeOffset.Parse(s!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
