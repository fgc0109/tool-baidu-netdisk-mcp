using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaiduNetdisk.Serialization;

internal sealed class FlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.GetRawText();
        }

        throw new JsonException($"不能把 {reader.TokenType} 转换成字符串。");
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
