namespace BaiduNetdisk.OAuth;

public sealed class BaiduReauthorizationRequiredException : Exception
{
    public BaiduReauthorizationRequiredException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
