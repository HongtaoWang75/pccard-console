using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCard.Core.Json;

/// <summary>
/// 把 JSON 里的数字或字符串都读成 string。
/// 用途：devices.json 里 did 是数字（1234567890），而所有 API 调用要字符串。
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"无法把 {reader.TokenType} 读成字符串"),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
