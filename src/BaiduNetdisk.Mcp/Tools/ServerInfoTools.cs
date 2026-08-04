using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace BaiduNetdisk.Mcp.Tools;

[McpServerToolType]
public sealed class ServerInfoTools
{
    [McpServerTool(Name = "server_info", ReadOnly = true, Idempotent = true),
     Description("Returns non-sensitive status for the local Baidu Netdisk MCP server.")]
    public static string GetServerInfo()
    {
        var status = new
        {
            name = "baidu-netdisk-mcp",
            transport = "stdio",
            appRootConfigured = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("BAIDU_APP_ROOT")),
            tokenFileConfigured = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("BAIDU_TOKEN_FILE"))
        };
        return JsonSerializer.Serialize(status);
    }
}
