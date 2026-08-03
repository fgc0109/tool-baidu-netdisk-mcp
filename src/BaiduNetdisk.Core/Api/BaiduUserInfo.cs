using System.Text.Json.Serialization;

namespace BaiduNetdisk.Api;

public sealed record BaiduUserInfo
{
    [JsonPropertyName("baidu_name")]
    public string? BaiduName { get; init; }

    [JsonPropertyName("netdisk_name")]
    public string? NetdiskName { get; init; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; init; }

    [JsonPropertyName("uk")]
    public long UserId { get; init; }

    [JsonPropertyName("vip_type")]
    public int VipType { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
}
