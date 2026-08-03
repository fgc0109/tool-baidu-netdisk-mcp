namespace BaiduNetdisk.OAuth;

public sealed class BaiduOAuthException : Exception
{
    public BaiduOAuthException(
        string error,
        string? description,
        int? statusCode = null,
        Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}", innerException)
    {
        Error = error;
        Description = description;
        StatusCode = statusCode;
    }

    public string Error { get; }

    public string? Description { get; }

    public int? StatusCode { get; }
}
