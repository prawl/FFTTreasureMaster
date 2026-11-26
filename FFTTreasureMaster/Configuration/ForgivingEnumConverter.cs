using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFTTreasureMaster.Configuration;

/// <summary>
/// Reads an enum by member name only and never throws, so one bad value cannot fail the whole
/// config file. Enum.TryParse is avoided because it accepts numbers and comma lists.
/// </summary>
public class ForgivingEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private readonly T _fallback;

    public ForgivingEnumConverter(T fallback) => _fallback = fallback;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Skip();
            return _fallback;
        }
        if (reader.TokenType != JsonTokenType.String) return _fallback;

        var text = reader.GetString();
        foreach (var name in Enum.GetNames<T>())
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<T>(name);
        return _fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : _fallback.ToString());
}

public sealed class TileStyleConverter : ForgivingEnumConverter<TileStyle>
{
    public TileStyleConverter() : base(Tuning.TileStyleDefault) { }
}

public sealed class StyleCycleKeyConverter : ForgivingEnumConverter<StyleCycleKey>
{
    public StyleCycleKeyConverter() : base(Tuning.StyleCycleKeyDefault) { }
}
