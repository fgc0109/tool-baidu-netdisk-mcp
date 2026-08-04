using System.ComponentModel;
using BaiduNetdisk.Download;
using BaiduNetdisk.OAuth;
using BaiduNetdisk.Upload;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BaiduNetdisk.Mcp.Tools;

[McpServerToolType]
public sealed class TransferTools(
    HttpClient httpClient,
    BaiduAuthenticatedSession session,
    BaiduDownloadService downloader,
    BaiduLocalPathPolicy localPaths,
    BaiduMcpOptions options,
    BaiduMcpJson json)
{
    [McpServerTool(Name = "download_file", ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Downloads one Baidu Netdisk file to an absolute path inside BAIDU_LOCAL_ROOTS. Existing files are preserved unless overwrite is explicitly true.")]
    public Task<string> DownloadFile(
        [Description("Positive Baidu Netdisk fs_id of the file to download.")] long fileSystemId,
        [Description("Absolute local destination path inside BAIDU_LOCAL_ROOTS.")] string localPath,
        [Description("Explicitly allow replacing an existing local file.")] bool overwrite = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reporter = progress is null
            ? null
            : new Progress<BaiduDownloadProgress>(value => progress.Report(new ProgressNotificationValue
            {
                Progress = value.BytesReceived,
                Total = value.TotalBytes,
                Message = value.Percentage is null ? "正在下载" : $"下载 {value.Percentage:P1}"
            }));
        return BaiduMcpToolSupport.ExecuteAsync(
            json,
            async () =>
            {
                var destination = localPaths.ValidateDownloadDestination(localPath);
                return await session.ExecuteAsync(
                    (accessToken, token) => downloader.DownloadByFileSystemIdAsync(
                        accessToken,
                        fileSystemId,
                        destination,
                        overwrite,
                        reporter,
                        token),
                    cancellationToken).ConfigureAwait(false);
            });
    }

    [McpServerTool(Name = "upload_file", ReadOnly = false, Destructive = true, Idempotent = false),
     Description("Uploads one local file from BAIDU_LOCAL_ROOTS to BAIDU_APP_ROOT using streaming multipart upload. The default conflict policy renames instead of overwriting.")]
    public Task<string> UploadFile(
        [Description("Absolute local source path inside BAIDU_LOCAL_ROOTS.")] string localPath,
        [Description("Absolute remote file path under BAIDU_APP_ROOT.")] string remotePath,
        [Description("Conflict policy: rename, rename-if-different, or overwrite.")] string onConflict = "rename",
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reporter = progress is null
            ? null
            : new Progress<BaiduUploadProgress>(value => progress.Report(new ProgressNotificationValue
            {
                Progress = value.BytesCompleted,
                Total = value.TotalBytes,
                Message = $"上传分片 {value.PartsCompleted}/{value.TotalParts}"
            }));
        return BaiduMcpToolSupport.ExecuteAsync(
            json,
            async () =>
            {
                var source = localPaths.ValidateUploadSource(localPath);
                var uploader = new BaiduUploadService(
                    httpClient,
                    new BaiduUploadOptions { AppRoot = options.RequireAppRoot() });
                var policy = ParseUploadConflictPolicy(onConflict);
                return await session.ExecuteAsync(
                    (accessToken, token) => uploader.UploadFileAsync(
                        accessToken,
                        source,
                        remotePath,
                        policy,
                        reporter,
                        token),
                    cancellationToken).ConfigureAwait(false);
            });
    }

    private static BaiduUploadConflictPolicy ParseUploadConflictPolicy(string value) =>
        value.ToLowerInvariant() switch
        {
            "rename" => BaiduUploadConflictPolicy.Rename,
            "rename-if-different" => BaiduUploadConflictPolicy.RenameIfDifferent,
            "overwrite" => BaiduUploadConflictPolicy.Overwrite,
            _ => throw new McpException(
                "onConflict 只支持 rename、rename-if-different 或 overwrite。")
        };
}
