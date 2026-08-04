using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace BaiduNetdisk.Mcp.Tools;

[McpServerToolType]
public sealed class ServerInfoTools(BaiduMcpOptions options)
{
    [McpServerTool(Name = "server_info", ReadOnly = true, Idempotent = true),
     Description("Returns non-sensitive status for the local Baidu Netdisk MCP server.")]
    public string GetServerInfo()
    {
        var status = new
        {
            name = "baidu-netdisk-mcp",
            transport = "stdio",
            appRootConfigured = !string.IsNullOrWhiteSpace(options.AppRoot),
            localRootCount = options.LocalRoots.Count,
            maximumItems = options.MaximumItems,
            maximumResponseCharacters = options.MaximumResponseCharacters
        };
        return JsonSerializer.Serialize(status);
    }
}
