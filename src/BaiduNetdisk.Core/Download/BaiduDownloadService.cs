using System.Buffers;
using System.Security.Cryptography;
using BaiduNetdisk.Api;

namespace BaiduNetdisk.Download;

public sealed class BaiduDownloadService
{
    private const int BufferSize = 128 * 1024;
    private const string DownloadUserAgent = "pan.baidu.com";

    private readonly HttpClient _httpClient;
    private readonly BaiduNetdiskClient _netdiskClient;

    public BaiduDownloadService(HttpClient httpClient, BaiduNetdiskClient netdiskClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(netdiskClient);
        _httpClient = httpClient;
        _netdiskClient = netdiskClient;
    }

    public async Task<BaiduDownloadResult> DownloadByFileSystemIdAsync(
        string accessToken,
        long fileSystemId,
        string destinationPath,
        bool overwrite = false,
        IProgress<BaiduDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access Token 不能为空。", nameof(accessToken));
        }

        if (fileSystemId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSystemId), "fs_id 必须为正整数。");
        }

        var fullDestinationPath = ValidateDestination(destinationPath, overwrite);
        var metadataResult = await _netdiskClient.GetFileMetadataAsync(
            accessToken,
            new[] { fileSystemId },
            includeDownloadLink: true,
            cancellationToken).ConfigureAwait(false);
        var metadata = metadataResult.Items.FirstOrDefault(item => item.FileSystemId == fileSystemId)
            ?? throw new FileNotFoundException($"百度网盘没有返回 fs_id={fileSystemId} 的文件元数据。");

        if (metadata.IsDirectory)
        {
            throw new InvalidOperationException("目录不能直接下载，请指定文件的 fs_id。");
        }

        if (string.IsNullOrWhiteSpace(metadata.DownloadLink))
        {
            throw new BaiduNetdiskApiException(-1, "文件元数据中没有下载地址。", metadataResult.RequestId);
        }

        var downloadUri = BuildAuthenticatedDownloadUri(metadata.DownloadLink, accessToken);
        var temporaryPath = $"{fullDestinationPath}.{Guid.NewGuid():N}.partial";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.UserAgent.ParseAdd(DownloadUserAgent);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long bytesWritten = 0;
            try
            {
                progress?.Report(new BaiduDownloadProgress(0, metadata.SizeBytes));
                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                        .ConfigureAwait(false);
                    hash.AppendData(buffer, 0, bytesRead);
                    bytesWritten += bytesRead;
                    progress?.Report(new BaiduDownloadProgress(bytesWritten, metadata.SizeBytes));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var actualMd5 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            ValidateIntegrity(metadata, bytesWritten, actualMd5);
            cancellationToken.ThrowIfCancellationRequested();

            destination.Close();
            File.Move(temporaryPath, fullDestinationPath, overwrite);
            return new BaiduDownloadResult(fullDestinationPath, bytesWritten, actualMd5);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string ValidateDestination(string destinationPath, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("本地目标路径不能为空。", nameof(destinationPath));
        }

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("本地目标路径无效。", nameof(destinationPath));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"目标目录不存在：{directory}");
        }

        if (Directory.Exists(fullPath))
        {
            throw new IOException($"目标路径是目录：{fullPath}");
        }

        if (!overwrite && File.Exists(fullPath))
        {
            throw new IOException($"目标文件已存在：{fullPath}。如需替换，请显式启用覆盖。");
        }

        return fullPath;
    }

    private static Uri BuildAuthenticatedDownloadUri(string downloadLink, string accessToken)
    {
        if (!Uri.TryCreate(downloadLink, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsTrustedDownloadHost(uri.Host))
        {
            throw new BaiduNetdiskApiException(-1, "百度返回了不受信任的下载地址，已拒绝发送 Access Token。");
        }

        if (HasQueryParameter(uri.Query, "access_token"))
        {
            return uri;
        }

        var builder = new UriBuilder(uri);
        var existingQuery = builder.Query.TrimStart('?');
        var tokenParameter = $"access_token={Uri.EscapeDataString(accessToken)}";
        builder.Query = string.IsNullOrEmpty(existingQuery)
            ? tokenParameter
            : $"{existingQuery}&{tokenParameter}";
        return builder.Uri;
    }

    private static bool IsTrustedDownloadHost(string host) =>
        string.Equals(host, "baidu.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "baidupcs.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".baidupcs.com", StringComparison.OrdinalIgnoreCase);

    private static bool HasQueryParameter(string query, string name) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Split('=', 2)[0])
            .Any(key => string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal));

    private static void ValidateIntegrity(BaiduFileMetadata metadata, long bytesWritten, string actualMd5)
    {
        if (bytesWritten != metadata.SizeBytes)
        {
            throw new BaiduDownloadIntegrityException(
                $"下载大小校验失败：预期 {metadata.SizeBytes} bytes，实际 {bytesWritten} bytes。");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Md5) &&
            metadata.Md5.Length == 32 &&
            !string.Equals(metadata.Md5, actualMd5, StringComparison.OrdinalIgnoreCase))
        {
            throw new BaiduDownloadIntegrityException(
                $"下载 MD5 校验失败：预期 {metadata.Md5.ToLowerInvariant()}，实际 {actualMd5}。");
        }
    }
}
