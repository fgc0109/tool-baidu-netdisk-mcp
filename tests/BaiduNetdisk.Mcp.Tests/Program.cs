using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var failures = 0;
try
{
    await using var client = await CreateClientAsync();
    Assert(client.NegotiatedProtocolVersion == "2025-11-25",
        "没有完成指定版本的 initialize 握手。");

    var tools = await client.ListToolsAsync();
    var serverInfo = tools.SingleOrDefault(tool => tool.Name == "server_info")
        ?? throw new InvalidOperationException("tools/list 缺少 server_info。");
    Assert(serverInfo.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Object,
        "server_info 缺少输入 JSON Schema。");

    var result = await client.CallToolAsync(
        "server_info",
        new Dictionary<string, object?>(),
        cancellationToken: CancellationToken.None);
    Assert(result.IsError is not true, "server_info 返回工具错误。");
    var text = result.Content.OfType<TextContentBlock>().SingleOrDefault()?.Text;
    Assert(text?.Contains("baidu-netdisk-mcp", StringComparison.Ordinal) == true,
        "server_info 返回内容不正确。");

    try
    {
        await client.CallToolAsync(
            "missing_tool",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);
        throw new InvalidOperationException("未知工具没有返回错误。");
    }
    catch (McpException)
    {
        // Expected JSON-RPC/tool discovery failure.
    }

    Console.WriteLine("PASS MCP initialize、工具发现、调用和错误返回");
}
catch (Exception exception)
{
    failures++;
    Console.Error.WriteLine($"FAIL MCP 集成测试: {exception}");
}

return failures == 0 ? 0 : 1;

static async Task<McpClient> CreateClientAsync()
{
    var repositoryRoot = FindRepositoryRoot();
    var projectPath = Path.Combine(
        repositoryRoot,
        "src",
        "BaiduNetdisk.Mcp",
        "BaiduNetdisk.Mcp.csproj");
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "BaiduNetdiskMcpIntegrationTest",
        Command = "dotnet",
        Arguments = ["run", "--project", projectPath, "-c", "Release", "--no-build"],
        WorkingDirectory = repositoryRoot
    });
    return await McpClient.CreateAsync(
        transport,
        new McpClientOptions
        {
            ProtocolVersion = "2025-11-25",
            InitializationTimeout = TimeSpan.FromSeconds(15)
        });
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "BaiduNetdiskTool.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("无法定位仓库根目录。");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
