using System.Text.Json.Serialization;
using BaiduNetdisk.Serialization;

namespace BaiduNetdisk.Api;

public sealed record BaiduFilePage
{
    [JsonPropertyName("list")]
    public List<BaiduFileEntry> Items { get; init; } = [];

    [JsonPropertyName("request_id")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? RequestId { get; init; }
}
