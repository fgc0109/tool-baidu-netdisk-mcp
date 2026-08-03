namespace BaiduNetdisk.Management;

public sealed record BaiduDirectoryResult(
    long FileSystemId,
    string Path,
    string? Name,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt);
