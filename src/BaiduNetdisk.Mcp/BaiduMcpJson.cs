using System.Text.Json;

namespace BaiduNetdisk.Mcp;

public sealed class BaiduMcpJson(BaiduMcpOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (json.Length <= options.MaximumResponseCharacters)
        {
            return json;
        }

        return JsonSerializer.Serialize(
            new
            {
                truncated = true,
                message = "结果超过 MCP 文本长度限制，请缩小分页数量或查询范围。",
                originalCharacterCount = json.Length,
                maximumCharacterCount = options.MaximumResponseCharacters
            },
            JsonOptions);
    }
}
