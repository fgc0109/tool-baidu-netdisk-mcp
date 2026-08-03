namespace BaiduNetdisk.Download;

public sealed record BaiduDownloadResult(
    string DestinationPath,
    long BytesWritten,
    string Md5);
