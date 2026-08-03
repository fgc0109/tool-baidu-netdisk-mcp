using System.Text.Json.Serialization;

namespace BaiduNetdisk.Api;

public sealed record BaiduFileEntry
{
    [JsonPropertyName("fs_id")]
    public long FileSystemId { get; init; }

    [JsonPropertyName("server_filename")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("isdir")]
    public int DirectoryFlag { get; init; }

    [JsonPropertyName("size")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("category")]
    public int Category { get; init; }

    [JsonPropertyName("server_ctime")]
    public long ServerCreatedTime { get; init; }

    [JsonPropertyName("server_mtime")]
    public long ServerModifiedTime { get; init; }

    [JsonPropertyName("local_ctime")]
    public long LocalCreatedTime { get; init; }

    [JsonPropertyName("local_mtime")]
    public long LocalModifiedTime { get; init; }

    [JsonIgnore]
    public bool IsDirectory => DirectoryFlag == 1;

    [JsonIgnore]
    public DateTimeOffset? ServerModifiedAt => ToDateTimeOffset(ServerModifiedTime);

    private static DateTimeOffset? ToDateTimeOffset(long unixTime) =>
        unixTime <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(unixTime);
}
