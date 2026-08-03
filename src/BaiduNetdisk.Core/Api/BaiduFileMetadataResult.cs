using System.Text.Json.Serialization;
using BaiduNetdisk.Serialization;

namespace BaiduNetdisk.Api;

public sealed record BaiduFileMetadataResult
{
    [JsonPropertyName("list")]
    public List<BaiduFileMetadata> Items { get; init; } = [];

    [JsonPropertyName("request_id")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? RequestId { get; init; }
}
