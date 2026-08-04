using BaiduNetdisk.Mcp.Tools;
using BaiduNetdisk.Mcp;
using BaiduNetdisk.Api;
using BaiduNetdisk.Download;
using BaiduNetdisk.OAuth;
using BaiduNetdisk.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    // stdout is reserved exclusively for MCP JSON-RPC frames.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var mcpOptions = BaiduMcpOptions.FromEnvironment();
builder.Services.AddSingleton(mcpOptions);
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
builder.Services.AddSingleton<BaiduOAuthClient>(services =>
    new BaiduOAuthClient(
        services.GetRequiredService<HttpClient>(),
        services.GetRequiredService<BaiduMcpOptions>().OAuth));
builder.Services.AddSingleton<IBaiduTokenStore>(services =>
{
    var options = services.GetRequiredService<BaiduMcpOptions>();
    return BaiduTokenStoreFactory.Create(options.TokenPath, options.TokenProtection);
});
builder.Services.AddSingleton<BaiduAuthenticatedSession>();
builder.Services.AddSingleton<BaiduNetdiskClient>();
builder.Services.AddSingleton<BaiduDownloadService>();
builder.Services.AddSingleton<BaiduLocalPathPolicy>();
builder.Services.AddSingleton<BaiduMcpJson>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ServerInfoTools>()
    .WithTools<AccountTools>()
    .WithTools<FileQueryTools>()
    .WithTools<TransferTools>()
    .WithTools<ManagementTools>();

await builder.Build().RunAsync();
