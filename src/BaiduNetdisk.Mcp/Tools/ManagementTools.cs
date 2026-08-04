using System.ComponentModel;
using BaiduNetdisk.Management;
using BaiduNetdisk.OAuth;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BaiduNetdisk.Mcp.Tools;

[McpServerToolType]
public sealed class ManagementTools(
    HttpClient httpClient,
    BaiduAuthenticatedSession session,
    BaiduMcpOptions options,
    BaiduMcpJson json)
{
    [McpServerTool(Name = "create_directory", ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Creates a directory under BAIDU_APP_ROOT. The default conflict policy fails without renaming or overwriting.")]
    public Task<string> CreateDirectory(
        [Description("Absolute remote directory path under BAIDU_APP_ROOT.")] string path,
        [Description("Conflict policy: fail, rename, or overwrite.")] string onConflict = "fail",
        CancellationToken cancellationToken = default)
        => BaiduMcpToolSupport.ExecuteAsync(
            json,
            async () =>
            {
                var service = CreateService();
                var policy = ParseConflictPolicy(onConflict);
                return await session.ExecuteAsync(
                    (accessToken, token) => service.CreateDirectoryAsync(
                        accessToken,
                        path,
                        policy,
                        token),
                    cancellationToken).ConfigureAwait(false);
            });

    [McpServerTool(Name = "copy_files", ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Copies 1 to 100 files or directories within BAIDU_APP_ROOT and returns one result per item. The default conflict policy fails.")]
    public Task<string> CopyFiles(
        [Description("Transfer requests containing sourcePath, destinationDirectory, and optional newName.")]
        McpTransferRequest[] requests,
        [Description("Conflict policy: fail, rename, or overwrite.")] string onConflict = "fail",
        CancellationToken cancellationToken = default) =>
        TransferFiles(requests, onConflict, move: false, cancellationToken);

    [McpServerTool(Name = "move_files", ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Moves 1 to 100 files or directories within BAIDU_APP_ROOT and returns one result per item. The default conflict policy fails.")]
    public Task<string> MoveFiles(
        [Description("Transfer requests containing sourcePath, destinationDirectory, and optional newName.")]
        McpTransferRequest[] requests,
        [Description("Conflict policy: fail, rename, or overwrite.")] string onConflict = "fail",
        CancellationToken cancellationToken = default) =>
        TransferFiles(requests, onConflict, move: true, cancellationToken);

    [McpServerTool(Name = "rename_file", ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Renames one file or directory under BAIDU_APP_ROOT. A conflicting target is reported as an error and is not overwritten.")]
    public Task<string> RenameFile(
        [Description("Absolute existing path under BAIDU_APP_ROOT.")] string path,
        [Description("New leaf name without directory separators.")] string newName,
        CancellationToken cancellationToken = default)
    {
        return BaiduMcpToolSupport.ExecuteAsync(
            json,
            async () =>
            {
                var service = CreateService();
                return await session.ExecuteAsync(
                    (accessToken, token) => service.RenameAsync(
                        accessToken,
                        [new BaiduFileRenameRequest(path, newName)],
                        token),
                    cancellationToken).ConfigureAwait(false);
            });
    }

    [McpServerTool(Name = "delete_files", ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Deletes 1 to 100 paths under BAIDU_APP_ROOT. The confirm argument must be explicitly true.")]
    public Task<string> DeleteFiles(
        [Description("Absolute paths under BAIDU_APP_ROOT to delete.")] string[] paths,
        [Description("Must be true to authorize this destructive operation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new McpException("删除属于破坏性操作，confirm 必须显式设为 true。");
        }

        return BaiduMcpToolSupport.ExecuteAsync(
            json,
            async () =>
            {
                var service = CreateService();
                return await session.ExecuteAsync(
                    (accessToken, token) => service.DeleteAsync(
                        accessToken,
                        paths,
                        confirmDelete: true,
                        cancellationToken: token),
                    cancellationToken).ConfigureAwait(false);
            });
    }

    private Task<string> TransferFiles(
        McpTransferRequest[] requests,
        string onConflict,
        bool move,
        CancellationToken cancellationToken)
    {
        if (requests is null || requests.Length is < 1 or > 100)
        {
            throw new McpException("requests 必须包含 1～100 项。");
        }

        return BaiduMcpToolSupport.ExecuteAsync(
            json,
            async () =>
            {
                var service = CreateService();
                var policy = ParseConflictPolicy(onConflict);
                var coreRequests = requests.Select(request => new BaiduFileTransferRequest(
                    request.SourcePath,
                    request.DestinationDirectory,
                    request.NewName)).ToArray();
                return await session.ExecuteAsync(
                    (accessToken, token) => move
                        ? service.MoveAsync(accessToken, coreRequests, policy, token)
                        : service.CopyAsync(accessToken, coreRequests, policy, token),
                    cancellationToken).ConfigureAwait(false);
            });
    }

    private BaiduFileManagementService CreateService() =>
        new(httpClient, new BaiduFileManagementOptions { AppRoot = options.RequireAppRoot() });

    private static BaiduFileConflictPolicy ParseConflictPolicy(string value) =>
        value.ToLowerInvariant() switch
        {
            "fail" => BaiduFileConflictPolicy.Fail,
            "rename" => BaiduFileConflictPolicy.Rename,
            "overwrite" => BaiduFileConflictPolicy.Overwrite,
            _ => throw new McpException("onConflict 只支持 fail、rename 或 overwrite。")
        };
}

public sealed record McpTransferRequest
{
    [Description("Absolute source path under BAIDU_APP_ROOT.")]
    public required string SourcePath { get; init; }

    [Description("Absolute destination directory under BAIDU_APP_ROOT.")]
    public required string DestinationDirectory { get; init; }

    [Description("Optional new leaf name for the copied or moved item.")]
    public string? NewName { get; init; }
}
