using System.ComponentModel;
using BaiduNetdisk.Api;
using BaiduNetdisk.OAuth;
using ModelContextProtocol.Server;

namespace BaiduNetdisk.Mcp.Tools;

[McpServerToolType]
public sealed class AccountTools(
    BaiduAuthenticatedSession session,
    BaiduNetdiskClient netdisk,
    BaiduMcpJson json)
{
    [McpServerTool(Name = "get_account", ReadOnly = true, Idempotent = true),
     Description("Returns the authorized Baidu Netdisk account profile without exposing OAuth tokens.")]
    public Task<string> GetAccount(CancellationToken cancellationToken) =>
        BaiduMcpToolSupport.ExecuteAsync(
            json,
            () => session.ExecuteAsync(
                (accessToken, token) => netdisk.GetUserInfoAsync(accessToken, token),
                cancellationToken));

    [McpServerTool(Name = "get_quota", ReadOnly = true, Idempotent = true),
     Description("Returns total, used, and remaining Baidu Netdisk storage in bytes.")]
    public Task<string> GetQuota(CancellationToken cancellationToken) =>
        BaiduMcpToolSupport.ExecuteAsync(
            json,
            () => session.ExecuteAsync(
                (accessToken, token) => netdisk.GetQuotaAsync(accessToken, token),
                cancellationToken));
}
