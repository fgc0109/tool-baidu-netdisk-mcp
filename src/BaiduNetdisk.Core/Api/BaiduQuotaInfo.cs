using System.Text.Json.Serialization;
using BaiduNetdisk.Serialization;

namespace BaiduNetdisk.Api;

public sealed record BaiduQuotaInfo
{
    [JsonPropertyName("total")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("used")]
    public long UsedBytes { get; init; }

    [JsonPropertyName("request_id")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? RequestId { get; init; }

    [JsonIgnore]
    public long RemainingBytes => Math.Max(0, TotalBytes - UsedBytes);

    [JsonIgnore]
    public double UsedRatio => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;
}
