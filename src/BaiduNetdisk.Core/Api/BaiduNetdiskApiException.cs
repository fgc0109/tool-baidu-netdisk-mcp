namespace BaiduNetdisk.Api;

public sealed class BaiduNetdiskApiException : Exception
{
    public BaiduNetdiskApiException(
        int errorCode,
        string? errorMessage,
        string? requestId = null,
        int? statusCode = null,
        Exception? innerException = null)
        : base(BuildMessage(errorCode, errorMessage, requestId), innerException)
    {
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        RequestId = requestId;
        StatusCode = statusCode;
    }

    public int ErrorCode { get; }

    public string? ErrorMessage { get; }

    public string? RequestId { get; }

    public int? StatusCode { get; }

    private static string BuildMessage(int errorCode, string? errorMessage, string? requestId)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? $"百度网盘 API 错误 {errorCode}"
            : $"百度网盘 API 错误 {errorCode}: {errorMessage}";
        return string.IsNullOrWhiteSpace(requestId) ? message : $"{message} (request_id: {requestId})";
    }
}
