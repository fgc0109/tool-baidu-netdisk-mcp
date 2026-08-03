namespace BaiduNetdisk.Management;

public sealed record BaiduFileTransferRequest(
    string SourcePath,
    string DestinationDirectory,
    string? NewName = null);
