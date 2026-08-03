using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduNetdisk.Api;
using BaiduNetdisk.Serialization;

namespace BaiduNetdisk.Upload;

public sealed class BaiduUploadService
{
    private static readonly Uri FileEndpoint = new("https://pan.baidu.com/rest/2.0/xpan/file");
    private static readonly Uri LocateUploadEndpoint = new("https://d.pcs.baidu.com/rest/2.0/pcs/file");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly BaiduUploadOptions _options;
    private readonly string _appRoot;

    public BaiduUploadService(HttpClient httpClient, BaiduUploadOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options;
        _appRoot = options.GetValidatedAppRoot();
    }

    public async Task<BaiduUploadResult> UploadFileAsync(
        string accessToken,
        string localPath,
        string remotePath,
        BaiduUploadConflictPolicy conflictPolicy = BaiduUploadConflictPolicy.Rename,
        IProgress<BaiduUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireAccessToken(accessToken);
        var fullLocalPath = ValidateLocalFile(localPath);
        var validatedRemotePath = ValidateRemotePath(remotePath);
        ValidateConflictPolicy(conflictPolicy);

        await using var file = new FileStream(
            fullLocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var analysis = await AnalyzeFileAsync(file, cancellationToken).ConfigureAwait(false);
        progress?.Report(new BaiduUploadProgress(0, analysis.SizeBytes, 0, analysis.BlockMd5.Count));

        var precreate = await PrecreateAsync(
            accessToken,
            validatedRemotePath,
            analysis,
            conflictPolicy,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(precreate.UploadId))
        {
            throw new BaiduNetdiskApiException(-1, "预创建响应缺少 uploadid。", precreate.RequestId);
        }

        var requestedParts = ValidateRequestedParts(precreate.BlockList, analysis.BlockMd5.Count);
        long bytesCompleted = analysis.SizeBytes - requestedParts.Sum(index => GetPartLength(index, analysis.SizeBytes));
        var partsCompleted = analysis.BlockMd5.Count - requestedParts.Count;
        progress?.Report(new BaiduUploadProgress(
            bytesCompleted,
            analysis.SizeBytes,
            partsCompleted,
            analysis.BlockMd5.Count));

        if (requestedParts.Count > 0)
        {
            var uploadServer = await LocateUploadServerAsync(
                accessToken,
                validatedRemotePath,
                precreate.UploadId,
                cancellationToken).ConfigureAwait(false);
            foreach (var partIndex in requestedParts)
            {
                var partLength = GetPartLength(partIndex, analysis.SizeBytes);
                await UploadPartWithRetryAsync(
                    uploadServer,
                    accessToken,
                    validatedRemotePath,
                    precreate.UploadId,
                    partIndex,
                    partLength,
                    file,
                    cancellationToken).ConfigureAwait(false);
                bytesCompleted += partLength;
                partsCompleted++;
                progress?.Report(new BaiduUploadProgress(
                    bytesCompleted,
                    analysis.SizeBytes,
                    partsCompleted,
                    analysis.BlockMd5.Count));
            }
        }

        var created = await CreateFileAsync(
            accessToken,
            validatedRemotePath,
            precreate.UploadId,
            analysis,
            conflictPolicy,
            cancellationToken).ConfigureAwait(false);
        return new BaiduUploadResult(
            created.FileSystemId,
            created.Path ?? validatedRemotePath,
            created.SizeBytes,
            created.Md5,
            requestedParts.Count == 0);
    }

    private async Task<FileAnalysis> AnalyzeFileAsync(FileStream file, CancellationToken cancellationToken)
    {
        file.Position = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(_options.ChunkSize);
        using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sliceHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var blockMd5 = new List<string>();
        long totalBytes = 0;
        var sliceBytesRemaining = 256 * 1024;

        try
        {
            while (true)
            {
                var count = 0;
                while (count < _options.ChunkSize)
                {
                    var bytesRead = await file.ReadAsync(
                        buffer.AsMemory(count, _options.ChunkSize - count),
                        cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    fullHash.AppendData(buffer, count, bytesRead);
                    if (sliceBytesRemaining > 0)
                    {
                        var sliceCount = Math.Min(sliceBytesRemaining, bytesRead);
                        sliceHash.AppendData(buffer, count, sliceCount);
                        sliceBytesRemaining -= sliceCount;
                    }

                    count += bytesRead;
                    totalBytes += bytesRead;
                }

                if (count == 0)
                {
                    break;
                }

                blockMd5.Add(ToMd5(MD5.HashData(buffer.AsSpan(0, count))));
                if (count < _options.ChunkSize)
                {
                    break;
                }
            }

            if (blockMd5.Count == 0)
            {
                blockMd5.Add(ToMd5(MD5.HashData(ReadOnlySpan<byte>.Empty)));
            }

            return new FileAnalysis(
                totalBytes,
                blockMd5,
                ToMd5(fullHash.GetHashAndReset()),
                ToMd5(sliceHash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            file.Position = 0;
        }
    }

    private Task<PrecreateResponse> PrecreateAsync(
        string accessToken,
        string remotePath,
        FileAnalysis analysis,
        BaiduUploadConflictPolicy conflictPolicy,
        CancellationToken cancellationToken) =>
        PostFormAsync<PrecreateResponse>(
            FileEndpoint,
            new Dictionary<string, string>
            {
                ["method"] = "precreate",
                ["access_token"] = accessToken,
                ["openapi"] = "xpansdk"
            },
            new Dictionary<string, string>
            {
                ["path"] = remotePath,
                ["size"] = Format(analysis.SizeBytes),
                ["isdir"] = "0",
                ["autoinit"] = "1",
                ["rtype"] = Format((int)conflictPolicy),
                ["block_list"] = JsonSerializer.Serialize(analysis.BlockMd5),
                ["content-md5"] = analysis.ContentMd5,
                ["slice-md5"] = analysis.SliceMd5
            },
            cancellationToken);

    private async Task<Uri> LocateUploadServerAsync(
        string accessToken,
        string remotePath,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<LocateUploadResponse>(
            LocateUploadEndpoint,
            new Dictionary<string, string>
            {
                ["method"] = "locateupload",
                ["access_token"] = accessToken,
                ["appid"] = "250528",
                ["upload_version"] = "2.0",
                ["path"] = remotePath,
                ["uploadid"] = uploadId
            },
            cancellationToken).ConfigureAwait(false);
        var candidates = response.Servers
            .Select(server => server.Server)
            .Append(response.Host)
            .Where(server => !string.IsNullOrWhiteSpace(server));

        foreach (var candidate in candidates)
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                IsTrustedUploadHost(uri.Host))
            {
                return new Uri(uri, "/rest/2.0/pcs/superfile2");
            }
        }

        throw new BaiduNetdiskApiException(-1, "百度没有返回可信的 HTTPS 上传域名。", response.RequestId);
    }

    private async Task UploadPartWithRetryAsync(
        Uri uploadEndpoint,
        string accessToken,
        string remotePath,
        string uploadId,
        int partIndex,
        long partLength,
        FileStream file,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                file.Position = (long)partIndex * _options.ChunkSize;
                using var limitedStream = new LimitedReadStream(file, partLength);
                using var fileContent = new StreamContent(limitedStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                fileContent.Headers.ContentLength = partLength;
                using var multipart = new MultipartFormDataContent();
                multipart.Add(fileContent, "file", $"part-{partIndex}");
                var uri = BuildUri(
                    uploadEndpoint,
                    new Dictionary<string, string>
                    {
                        ["method"] = "upload",
                        ["access_token"] = accessToken,
                        ["openapi"] = "xpansdk",
                        ["type"] = "tmpfile",
                        ["path"] = remotePath,
                        ["uploadid"] = uploadId,
                        ["partseq"] = Format(partIndex)
                    });
                using var response = await _httpClient.PostAsync(uri, multipart, cancellationToken)
                    .ConfigureAwait(false);
                _ = await ReadJsonResponseAsync<UploadPartResponse>(response, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                attempt < _options.MaxChunkAttempts && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private Task<CreateFileResponse> CreateFileAsync(
        string accessToken,
        string remotePath,
        string uploadId,
        FileAnalysis analysis,
        BaiduUploadConflictPolicy conflictPolicy,
        CancellationToken cancellationToken) =>
        PostFormAsync<CreateFileResponse>(
            FileEndpoint,
            new Dictionary<string, string>
            {
                ["method"] = "create",
                ["access_token"] = accessToken,
                ["openapi"] = "xpansdk"
            },
            new Dictionary<string, string>
            {
                ["path"] = remotePath,
                ["size"] = Format(analysis.SizeBytes),
                ["isdir"] = "0",
                ["uploadid"] = uploadId,
                ["rtype"] = Format((int)conflictPolicy),
                ["block_list"] = JsonSerializer.Serialize(analysis.BlockMd5)
            },
            cancellationToken);

    private async Task<T> PostFormAsync<T>(
        Uri endpoint,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(BuildUri(endpoint, query), content, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetJsonAsync<T>(
        Uri endpoint,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(BuildUri(endpoint, query), cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ApiEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiEnvelope>(payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new BaiduNetdiskApiException(
                -1,
                "百度上传 API 返回的数据不是有效 JSON。",
                statusCode: (int)response.StatusCode,
                innerException: exception);
        }

        var errorCode = envelope?.ErrorCode ?? envelope?.Errno ?? 0;
        if (!response.IsSuccessStatusCode || errorCode != 0)
        {
            throw new BaiduNetdiskApiException(
                errorCode == 0 ? -(int)response.StatusCode : errorCode,
                envelope?.ErrorMessage ?? envelope?.ErrMsg ?? "百度上传 API 返回了 HTTP 错误。",
                envelope?.RequestId,
                (int)response.StatusCode);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new JsonException("响应数据为空。");
        }
        catch (JsonException exception)
        {
            throw new BaiduNetdiskApiException(
                -1,
                "百度上传 API 返回的字段格式无法解析。",
                envelope?.RequestId,
                (int)response.StatusCode,
                exception);
        }
    }

    private string ValidateRemotePath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) ||
            !remotePath.StartsWith($"{_appRoot}/", StringComparison.Ordinal) ||
            remotePath.EndsWith("/", StringComparison.Ordinal) ||
            remotePath.Contains('\\') ||
            remotePath.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException($"远程文件必须位于 {_appRoot}/ 目录内。", nameof(remotePath));
        }

        return remotePath;
    }

    private static string ValidateLocalFile(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            throw new ArgumentException("本地文件路径不能为空。", nameof(localPath));
        }

        var fullPath = Path.GetFullPath(localPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("本地上传文件不存在。", fullPath);
        }

        return fullPath;
    }

    private static void RequireAccessToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access Token 不能为空。", nameof(accessToken));
        }
    }

    private static void ValidateConflictPolicy(BaiduUploadConflictPolicy conflictPolicy)
    {
        if (conflictPolicy is < BaiduUploadConflictPolicy.Rename or > BaiduUploadConflictPolicy.Overwrite)
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy), "不支持的重名策略。");
        }
    }

    private List<int> ValidateRequestedParts(IReadOnlyCollection<int>? requestedParts, int totalParts)
    {
        var parts = (requestedParts ?? Array.Empty<int>()).Distinct().Order().ToList();
        if (parts.Any(index => index < 0 || index >= totalParts))
        {
            throw new BaiduNetdiskApiException(-1, "预创建响应包含无效的分片序号。");
        }

        return parts;
    }

    private long GetPartLength(int partIndex, long totalSize)
    {
        if (totalSize == 0)
        {
            return 0;
        }

        var offset = (long)partIndex * _options.ChunkSize;
        return Math.Min(_options.ChunkSize, totalSize - offset);
    }

    private static bool IsTrustedUploadHost(string host) =>
        string.Equals(host, "baidu.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "baidupcs.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".baidupcs.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException => true,
        BaiduNetdiskApiException apiException when apiException.StatusCode is 408 or 429 => true,
        BaiduNetdiskApiException apiException when apiException.StatusCode >= 500 => true,
        _ => false
    };

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string ToMd5(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private sealed record FileAnalysis(
        long SizeBytes,
        IReadOnlyList<string> BlockMd5,
        string ContentMd5,
        string SliceMd5);

    private sealed record ApiEnvelope
    {
        [JsonPropertyName("errno")]
        public int? Errno { get; init; }

        [JsonPropertyName("error_code")]
        public int? ErrorCode { get; init; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; init; }

        [JsonPropertyName("error_msg")]
        public string? ErrorMessage { get; init; }

        [JsonPropertyName("request_id")]
        [JsonConverter(typeof(FlexibleStringJsonConverter))]
        public string? RequestId { get; init; }
    }

    private sealed record PrecreateResponse
    {
        [JsonPropertyName("uploadid")]
        public string? UploadId { get; init; }

        [JsonPropertyName("block_list")]
        public List<int>? BlockList { get; init; }

        [JsonPropertyName("request_id")]
        [JsonConverter(typeof(FlexibleStringJsonConverter))]
        public string? RequestId { get; init; }
    }

    private sealed record UploadServer
    {
        [JsonPropertyName("server")]
        public string? Server { get; init; }
    }

    private sealed record LocateUploadResponse
    {
        [JsonPropertyName("servers")]
        public List<UploadServer> Servers { get; init; } = [];

        [JsonPropertyName("host")]
        public string? Host { get; init; }

        [JsonPropertyName("request_id")]
        [JsonConverter(typeof(FlexibleStringJsonConverter))]
        public string? RequestId { get; init; }
    }

    private sealed record UploadPartResponse
    {
        [JsonPropertyName("md5")]
        public string? Md5 { get; init; }
    }

    private sealed record CreateFileResponse
    {
        [JsonPropertyName("fs_id")]
        public long FileSystemId { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("size")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("md5")]
        public string? Md5 { get; init; }
    }

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;

        public LimitedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var requested = (int)Math.Min(count, _remaining);
            var read = requested == 0 ? 0 : _inner.Read(buffer, offset, requested);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var requested = (int)Math.Min(buffer.Length, _remaining);
            var read = requested == 0
                ? 0
                : await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The shared source file remains open for subsequent parts and retries.
            base.Dispose(disposing);
        }
    }
}
