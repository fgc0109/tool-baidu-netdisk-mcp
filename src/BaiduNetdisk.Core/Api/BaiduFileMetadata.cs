using System.Text.Json.Serialization;

namespace BaiduNetdisk.Api;

public sealed record BaiduFileMetadata
{
    [JsonPropertyName("fs_id")]
    public long FileSystemId { get; init; }

    [JsonPropertyName("filename")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("isdir")]
    public int DirectoryFlag { get; init; }

    [JsonPropertyName("size")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("category")]
    public int Category { get; init; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; init; }

    [JsonPropertyName("dlink")]
    public string? DownloadLink { get; init; }

    [JsonPropertyName("server_ctime")]
    public long ServerCreatedTime { get; init; }

    [JsonPropertyName("server_mtime")]
    public long ServerModifiedTime { get; init; }

    [JsonIgnore]
    public bool IsDirectory => DirectoryFlag == 1;

    [JsonIgnore]
    public DateTimeOffset? ServerModifiedAt =>
        ServerModifiedTime <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(ServerModifiedTime);
}
