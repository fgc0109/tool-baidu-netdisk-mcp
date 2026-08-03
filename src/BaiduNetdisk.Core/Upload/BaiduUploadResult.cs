namespace BaiduNetdisk.Upload;

public sealed record BaiduUploadResult(
    long FileSystemId,
    string RemotePath,
    long SizeBytes,
    string? Md5,
    bool UsedRapidUpload);
