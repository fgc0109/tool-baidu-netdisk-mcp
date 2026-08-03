namespace BaiduNetdisk.Upload;

public sealed record BaiduUploadProgress(
    long BytesCompleted,
    long TotalBytes,
    int PartsCompleted,
    int TotalParts)
{
    public double? Percentage => TotalBytes <= 0
        ? null
        : Math.Min(1, (double)BytesCompleted / TotalBytes);
}
