namespace BaiduNetdisk.Download;

public sealed class BaiduDownloadIntegrityException : IOException
{
    public BaiduDownloadIntegrityException(string message)
        : base(message)
    {
    }
}
