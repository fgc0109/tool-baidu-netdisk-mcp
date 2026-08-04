using System.ComponentModel;
using BaiduNetdisk.Api;
using BaiduNetdisk.OAuth;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BaiduNetdisk.Mcp.Tools;

[McpServerToolType]
public sealed class FileQueryTools(
    BaiduAuthenticatedSession session,
    BaiduNetdiskClient netdisk,
    BaiduMcpOptions options,
    BaiduMcpJson json)
{
    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true),
     Description("Lists one Baidu Netdisk directory with bounded pagination. This tool does not modify files.")]
    public Task<string> ListFiles(
        [Description("Absolute Netdisk directory path, for example / or /资料.")] string directory = "/",
        [Description("Zero-based result offset.")] int start = 0,
        [Description("Number of items to return; limited by BAIDU_MCP_MAX_ITEMS.")] int limit = 50,
        [Description("Sort order: name, time, or size.")] string order = "name",
        [Description("Whether to sort in descending order.")] bool descending = false,
        [Description("Whether to return directories only.")] bool foldersOnly = false,
        CancellationToken cancellationToken = default)
    {
        BaiduMcpToolSupport.ValidateRequestedCount(limit, options, nameof(limit));
        var parsedOrder = ParseOrder(order);
        return BaiduMcpToolSupport.ExecuteAsync(
            json,
            async () =>
            {
                var page = await session.ExecuteAsync(
                    (accessToken, token) => netdisk.ListFilesAsync(
                        accessToken,
                        directory,
                        start,
                        limit,
                        parsedOrder,
                        descending,
                        foldersOnly,
                        token),
                    cancellationToken).ConfigureAwait(false);
                return new
                {
                    items = page.Items,
                    returned = page.Items.Count,
                    nextStart = start + page.Items.Count
                };
            });
    }

    [McpServerTool(Name = "search_files", ReadOnly = true, Idempotent = true),
     Description("Searches Baidu Netdisk file names with bounded pagination. This tool does not modify files.")]
    public Task<string> SearchFiles(
        [Description("File-name keyword to search for.")] string keyword,
        [Description("Absolute directory in which to search.")] string directory = "/",
        [Description("One-based page number.")] int page = 1,
        [Description("Number of items to return; limited by BAIDU_MCP_MAX_ITEMS.")] int pageSize = 50,
        [Description("Whether to recursively search child directories.")] bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        BaiduMcpToolSupport.ValidateRequestedCount(pageSize, options, nameof(pageSize));
        return BaiduMcpToolSupport.ExecuteAsync(
            json,
            async () =>
            {
                var result = await session.ExecuteAsync(
                    (accessToken, token) => netdisk.SearchFilesAsync(
                        accessToken,
                        keyword,
                        directory,
                        page,
                        pageSize,
                        recursive,
                        token),
                    cancellationToken).ConfigureAwait(false);
                return new
                {
                    items = result.Items,
                    returned = result.Items.Count,
                    nextPage = page + 1
                };
            });
    }

    [McpServerTool(Name = "get_file_metadata", ReadOnly = true, Idempotent = true),
     Description("Returns metadata for 1 to 100 Baidu Netdisk fs_id values without download links.")]
    public Task<string> GetFileMetadata(
        [Description("Array of positive Baidu Netdisk fs_id values.")] long[] fileSystemIds,
        CancellationToken cancellationToken = default)
    {
        if (fileSystemIds is null || fileSystemIds.Length is < 1 or > 100)
        {
            throw new McpException("fileSystemIds 必须包含 1～100 个 fs_id。");
        }

        return BaiduMcpToolSupport.ExecuteAsync(
            json,
            () => session.ExecuteAsync(
                (accessToken, token) => netdisk.GetFileMetadataAsync(
                    accessToken,
                    fileSystemIds,
                    includeDownloadLink: false,
                    cancellationToken: token),
                cancellationToken));
    }

    private static BaiduFileOrder ParseOrder(string order) => order.ToLowerInvariant() switch
    {
        "name" => BaiduFileOrder.Name,
        "time" => BaiduFileOrder.Time,
        "size" => BaiduFileOrder.Size,
        _ => throw new McpException("order 只支持 name、time 或 size。")
    };
}
