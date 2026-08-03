namespace BaiduNetdisk.Management;

public sealed record BaiduFileOperationResult(
    string SourcePath,
    string? DestinationPath,
    int ErrorCode,
    string? ErrorMessage)
{
    public bool Success => ErrorCode == 0;
}
