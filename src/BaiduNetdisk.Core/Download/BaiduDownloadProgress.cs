namespace BaiduNetdisk.Download;

public sealed record BaiduDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double? Percentage => TotalBytes <= 0
        ? null
        : Math.Min(1, (double)BytesReceived / TotalBytes);
}
