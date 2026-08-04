using BaiduNetdisk.Api;
using BaiduNetdisk.Diagnostics;
using BaiduNetdisk.OAuth;
using ModelContextProtocol;

namespace BaiduNetdisk.Mcp;

internal static class BaiduMcpToolSupport
{
    public static async Task<string> ExecuteAsync<T>(
        BaiduMcpJson json,
        Func<Task<T>> operation)
    {
        try
        {
            return json.Serialize(await operation().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (BaiduReauthorizationRequiredException exception)
        {
            throw new McpException(exception.Message, exception);
        }
        catch (BaiduNetdiskApiException exception)
        {
            throw new McpException(
                $"百度网盘 API 操作失败，errno={exception.ErrorCode}，request_id={exception.RequestId ?? "未返回"}。",
                exception);
        }
        catch (BaiduOAuthException exception)
        {
            throw new McpException(
                $"百度 OAuth 操作失败：{SensitiveDataRedactor.Redact(exception.Error)}。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new McpException("连接百度服务失败，请稍后重试。", exception);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            throw new McpException(SensitiveDataRedactor.Redact(exception.Message), exception);
        }
    }

    public static void ValidateRequestedCount(int count, BaiduMcpOptions options, string name)
    {
        if (count is < 1 || count > options.MaximumItems)
        {
            throw new McpException($"{name} 必须在 1～{options.MaximumItems} 之间。");
        }
    }
}
