using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atalaya.Storage.Json;

/// <summary>
/// Serializes an enum to/from an explicit wire string map (D-003). The exact wire values
/// (e.g. <c>critica</c>, <c>falso-positivo</c>, <c>severityChanged</c>) live in Storage,
/// so <c>Atalaya.Domain</c> stays free of serialization concerns.
/// </summary>
public sealed class EnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<TEnum, string> _toWire = new();
    private readonly Dictionary<string, TEnum> _fromWire = new(StringComparer.OrdinalIgnoreCase);

    public EnumJsonConverter(params (TEnum Value, string Wire)[] map)
    {
        foreach ((TEnum value, string wire) in map)
        {
            _toWire[value] = wire;
            _fromWire[wire] = value;
        }

        // Fail fast if a new enum member was added but not mapped.
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            if (!_toWire.ContainsKey(value))
            {
                throw new InvalidOperationException(
                    $"Enum {typeof(TEnum).Name}.{value} has no wire mapping.");
            }
        }
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? s = reader.GetString();
        if (s is not null && _fromWire.TryGetValue(s, out TEnum value))
        {
            return value;
        }

        throw new JsonException($"'{s}' is not a valid {typeof(TEnum).Name} value.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(_toWire[value]);
    }
}
