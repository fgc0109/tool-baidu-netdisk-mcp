using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var failures = 0;
var isolatedRoot = Path.Combine(Path.GetTempPath(), $"baidu-mcp-integration-{Guid.NewGuid():N}");
Directory.CreateDirectory(isolatedRoot);
try
{
    await using var client = await CreateClientAsync(isolatedRoot, "2025-11-25");
    Assert(client.NegotiatedProtocolVersion == "2025-11-25",
        "没有完成指定版本的 initialize 握手。");

    var tools = await client.ListToolsAsync();
    string[] expectedTools =
    [
        "server_info",
        "get_account",
        "get_quota",
        "list_files",
        "search_files",
        "get_file_metadata",
        "download_file",
        "upload_file",
        "create_directory",
        "copy_files",
        "move_files",
        "rename_file",
        "delete_files"
    ];
    Assert(expectedTools.All(name => tools.Any(tool => tool.Name == name)),
        "tools/list 缺少一个或多个百度网盘工具。");
    Assert(tools.All(tool => tool.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Object),
        "一个或多个工具缺少输入 JSON Schema。");
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

    var accountError = await client.CallToolAsync(
        "get_account",
        new Dictionary<string, object?>(),
        cancellationToken: CancellationToken.None);
    Assert(accountError.IsError is true, "缺少 Token 时 get_account 没有返回工具错误。");
    Assert(GetText(accountError).Contains("login", StringComparison.OrdinalIgnoreCase),
        "缺少 Token 的错误没有提供重新登录指引。");

    var limitError = await client.CallToolAsync(
        "list_files",
        new Dictionary<string, object?> { ["limit"] = 101 },
        cancellationToken: CancellationToken.None);
    Assert(limitError.IsError is true && GetText(limitError).Contains("1～100", StringComparison.Ordinal),
        "list_files 没有执行返回数量限制。");

    var unsafePath = Path.GetFullPath(Path.Combine(isolatedRoot, "..", "outside.bin"));
    var pathError = await client.CallToolAsync(
        "download_file",
        new Dictionary<string, object?>
        {
            ["fileSystemId"] = 1L,
            ["localPath"] = unsafePath
        },
        cancellationToken: CancellationToken.None);
    var pathErrorText = GetText(pathError);
    Assert(pathError.IsError is true && pathErrorText.Contains("允许范围", StringComparison.Ordinal),
        $"download_file 没有拒绝本地根目录外路径：{pathErrorText}");

    var deleteError = await client.CallToolAsync(
        "delete_files",
        new Dictionary<string, object?>
        {
            ["paths"] = new[] { "/apps/integration/a.txt" },
            ["confirm"] = false
        },
        cancellationToken: CancellationToken.None);
    Assert(deleteError.IsError is true && GetText(deleteError).Contains("confirm", StringComparison.Ordinal),
        "delete_files 没有要求显式确认。");

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

    await using var modernClient = await CreateClientAsync(isolatedRoot, protocolVersion: null);
    Assert(modernClient.NegotiatedProtocolVersion == "2026-07-28",
        "没有完成 2026-07-28 server/discover 协议协商。");
    Assert((await modernClient.ListToolsAsync()).Count == expectedTools.Length,
        "现代协议下工具列表不完整。");

    Console.WriteLine("PASS MCP 双协议协商、工具发现、调用、限制和错误返回");
}
catch (Exception exception)
{
    failures++;
    Console.Error.WriteLine($"FAIL MCP 集成测试: {exception}");
}
finally
{
    Directory.Delete(isolatedRoot, recursive: true);
}

return failures == 0 ? 0 : 1;

static async Task<McpClient> CreateClientAsync(string isolatedRoot, string? protocolVersion)
{
    var repositoryRoot = FindRepositoryRoot();
    var projectPath = Path.Combine(
        repositoryRoot,
        "src",
        "BaiduNetdisk.Mcp",
        "BaiduNetdisk.Mcp.csproj");
    var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
    environment["BAIDU_TOKEN_FILE"] = Path.Combine(isolatedRoot, "missing-token.json");
    environment["BAIDU_LOCAL_ROOTS"] = isolatedRoot;
    environment["BAIDU_APP_ROOT"] = "/apps/integration";
    environment["BAIDU_MCP_MAX_ITEMS"] = "100";
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "BaiduNetdiskMcpIntegrationTest",
        Command = "dotnet",
        Arguments = ["run", "--project", projectPath, "-c", "Release", "--no-build"],
        WorkingDirectory = repositoryRoot,
        InheritEnvironmentVariables = false,
        EnvironmentVariables = environment
    });
    return await McpClient.CreateAsync(
        transport,
        new McpClientOptions
        {
            ProtocolVersion = protocolVersion,
            InitializationTimeout = TimeSpan.FromSeconds(15)
        });
}

static string GetText(CallToolResult result) =>
    string.Join("\n", result.Content.OfType<TextContentBlock>().Select(content => content.Text));

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
