using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduNetdisk.Api;
using BaiduNetdisk.Serialization;

namespace BaiduNetdisk.Management;

public sealed class BaiduFileManagementService
{
    private const int MaximumBatchSize = 100;
    private static readonly Uri FileEndpoint = new("https://pan.baidu.com/rest/2.0/xpan/file");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _appRoot;

    public BaiduFileManagementService(HttpClient httpClient, BaiduFileManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _appRoot = options.GetValidatedAppRoot();
    }

    public async Task<BaiduDirectoryResult> CreateDirectoryAsync(
        string accessToken,
        string path,
        BaiduFileConflictPolicy conflictPolicy = BaiduFileConflictPolicy.Fail,
        CancellationToken cancellationToken = default)
    {
        var validatedPath = ValidateManagedPath(path, nameof(path));
        ValidateConflictPolicy(conflictPolicy);
        var form = new Dictionary<string, string>
        {
            ["path"] = validatedPath,
            ["size"] = "0",
            ["isdir"] = "1",
            ["block_list"] = "[]"
        };
        if (conflictPolicy != BaiduFileConflictPolicy.Fail)
        {
            form["rtype"] = Format((int)conflictPolicy);
        }

        var response = await PostFormAsync<CreateDirectoryResponse>(
            new Dictionary<string, string>
            {
                ["method"] = "create",
                ["access_token"] = RequireAccessToken(accessToken)
            },
            form,
            cancellationToken).ConfigureAwait(false);
        return new BaiduDirectoryResult(
            response.FileSystemId,
            response.Path ?? validatedPath,
            response.Name,
            FromUnixTime(response.CreatedAt),
            FromUnixTime(response.ModifiedAt));
    }

    public Task<BaiduBatchOperationResult> CopyAsync(
        string accessToken,
        IReadOnlyCollection<BaiduFileTransferRequest> requests,
        BaiduFileConflictPolicy conflictPolicy = BaiduFileConflictPolicy.Fail,
        CancellationToken cancellationToken = default) =>
        TransferAsync("copy", accessToken, requests, conflictPolicy, cancellationToken);

    public Task<BaiduBatchOperationResult> MoveAsync(
        string accessToken,
        IReadOnlyCollection<BaiduFileTransferRequest> requests,
        BaiduFileConflictPolicy conflictPolicy = BaiduFileConflictPolicy.Fail,
        CancellationToken cancellationToken = default) =>
        TransferAsync("move", accessToken, requests, conflictPolicy, cancellationToken);

    public Task<BaiduBatchOperationResult> RenameAsync(
        string accessToken,
        IReadOnlyCollection<BaiduFileRenameRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ValidateBatch(requests, nameof(requests));
        var entries = requests.Select(request =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return new FileManagerEntry
            {
                Path = ValidateManagedPath(request.Path, nameof(requests)),
                NewName = ValidateLeafName(request.NewName, nameof(requests))
            };
        }).ToArray();
        return FileManagerAsync("rename", accessToken, entries, cancellationToken);
    }

    public Task<BaiduBatchOperationResult> DeleteAsync(
        string accessToken,
        IReadOnlyCollection<string> paths,
        bool confirmDelete,
        CancellationToken cancellationToken = default)
    {
        if (!confirmDelete)
        {
            throw new InvalidOperationException("删除属于破坏性操作，必须显式确认。");
        }

        ValidateBatch(paths, nameof(paths));
        var entries = paths.Select(path => new FileManagerEntry
        {
            Path = ValidateManagedPath(path, nameof(paths))
        }).ToArray();
        return FileManagerAsync("delete", accessToken, entries, cancellationToken);
    }

    private Task<BaiduBatchOperationResult> TransferAsync(
        string operation,
        string accessToken,
        IReadOnlyCollection<BaiduFileTransferRequest> requests,
        BaiduFileConflictPolicy conflictPolicy,
        CancellationToken cancellationToken)
    {
        ValidateBatch(requests, nameof(requests));
        ValidateConflictPolicy(conflictPolicy);
        var entries = requests.Select(request =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var source = ValidateManagedPath(request.SourcePath, nameof(requests));
            var destination = ValidateManagedDirectory(request.DestinationDirectory, nameof(requests));
            var name = request.NewName is null
                ? null
                : ValidateLeafName(request.NewName, nameof(requests));
            ValidateDestinationPath(destination, name ?? GetLeafName(source), nameof(requests));
            return new FileManagerEntry
            {
                Path = source,
                Destination = destination,
                NewName = name,
                OnDuplicate = FormatConflictPolicy(conflictPolicy)
            };
        }).ToArray();
        return FileManagerAsync(operation, accessToken, entries, cancellationToken);
    }

    private async Task<BaiduBatchOperationResult> FileManagerAsync(
        string operation,
        string accessToken,
        IReadOnlyList<FileManagerEntry> entries,
        CancellationToken cancellationToken)
    {
        var response = await PostFormAsync<FileManagerResponse>(
            new Dictionary<string, string>
            {
                ["method"] = "filemanager",
                ["access_token"] = RequireAccessToken(accessToken),
                ["opera"] = operation
            },
            new Dictionary<string, string>
            {
                ["async"] = "0",
                ["filelist"] = JsonSerializer.Serialize(entries, JsonOptions)
            },
            cancellationToken).ConfigureAwait(false);

        var results = new List<BaiduFileOperationResult>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var info = index < response.Info.Count ? response.Info[index] : null;
            results.Add(new BaiduFileOperationResult(
                entry.Path,
                GetDestinationPath(operation, entry, info?.Path),
                info?.ErrorCode ?? -1,
                info is null ? "百度响应缺少该操作的逐项结果。" : info.ErrorMessage));
        }

        return new BaiduBatchOperationResult(operation, results, response.RequestId);
    }

    private async Task<T> PostFormAsync<T>(
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(BuildUri(FileEndpoint, query), content, cancellationToken)
            .ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ApiEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiEnvelope>(payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw InvalidResponse(response, exception);
        }

        if (!response.IsSuccessStatusCode || envelope is null || envelope.ErrorCode != 0)
        {
            throw new BaiduNetdiskApiException(
                envelope?.ErrorCode ?? -(int)response.StatusCode,
                envelope?.ErrorMessage ?? "百度网盘文件管理 API 返回了 HTTP 错误。",
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
            throw InvalidResponse(response, exception, envelope.RequestId);
        }
    }

    private string ValidateManagedPath(string path, string parameterName)
    {
        var validated = ValidateAbsolutePath(path, parameterName, allowRoot: false);
        if (!validated.StartsWith($"{_appRoot}/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"写操作路径必须位于 {_appRoot}/ 目录内。", parameterName);
        }

        return validated;
    }

    private string ValidateManagedDirectory(string path, string parameterName)
    {
        var validated = ValidateAbsolutePath(path, parameterName, allowRoot: true).TrimEnd('/');
        if (!string.Equals(validated, _appRoot, StringComparison.Ordinal) &&
            !validated.StartsWith($"{_appRoot}/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"目标目录必须位于 {_appRoot} 内。", parameterName);
        }

        return validated;
    }

    private string ValidateDestinationPath(string directory, string name, string parameterName)
    {
        var path = $"{directory}/{name}";
        return ValidateManagedPath(path, parameterName);
    }

    private static string ValidateAbsolutePath(string path, string parameterName, bool allowRoot)
    {
        var normalized = allowRoot ? path?.TrimEnd('/') : path;
        if (string.IsNullOrWhiteSpace(normalized) ||
            !normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains('\\') ||
            normalized.EndsWith("/", StringComparison.Ordinal) ||
            normalized.Split('/').Skip(1).Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("网盘路径必须是规范的绝对路径。", parameterName);
        }

        return normalized;
    }

    private static string ValidateLeafName(string name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.Contains('/') ||
            name.Contains('\\'))
        {
            throw new ArgumentException("新名称必须是不含路径分隔符的文件名。", parameterName);
        }

        return name;
    }

    private static void ValidateBatch<T>(IReadOnlyCollection<T>? items, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(items, parameterName);
        if (items.Count is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(parameterName, "一次必须操作 1～100 项。");
        }
    }

    private static void ValidateConflictPolicy(BaiduFileConflictPolicy conflictPolicy)
    {
        if (conflictPolicy is not BaiduFileConflictPolicy.Fail and
            not BaiduFileConflictPolicy.Rename and
            not BaiduFileConflictPolicy.Overwrite)
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy), "不支持的同名策略。");
        }
    }

    private static string FormatConflictPolicy(BaiduFileConflictPolicy conflictPolicy) =>
        conflictPolicy switch
        {
            BaiduFileConflictPolicy.Fail => "fail",
            BaiduFileConflictPolicy.Rename => "newcopy",
            BaiduFileConflictPolicy.Overwrite => "overwrite",
            _ => throw new ArgumentOutOfRangeException(nameof(conflictPolicy))
        };

    private static string GetLeafName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string? GetDestinationPath(
        string operation,
        FileManagerEntry entry,
        string? responsePath) =>
        responsePath ?? operation switch
        {
            "copy" or "move" => $"{entry.Destination}/{entry.NewName ?? GetLeafName(entry.Path)}",
            "rename" => $"{entry.Path[..(entry.Path.LastIndexOf('/') + 1)]}{entry.NewName}",
            _ => null
        };

    private static string RequireAccessToken(string accessToken) =>
        string.IsNullOrWhiteSpace(accessToken)
            ? throw new ArgumentException("Access Token 不能为空。", nameof(accessToken))
            : accessToken;

    private static DateTimeOffset? FromUnixTime(long value) =>
        value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : null;

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    private static BaiduNetdiskApiException InvalidResponse(
        HttpResponseMessage response,
        JsonException exception,
        string? requestId = null) =>
        new(
            -1,
            "百度网盘文件管理 API 返回的数据无法解析。",
            requestId,
            (int)response.StatusCode,
            exception);

    private sealed record ApiEnvelope
    {
        [JsonPropertyName("errno")]
        public int ErrorCode { get; init; }

        [JsonPropertyName("errmsg")]
        public string? ErrorMessage { get; init; }

        [JsonPropertyName("request_id")]
        [JsonConverter(typeof(FlexibleStringJsonConverter))]
        public string? RequestId { get; init; }
    }

    private sealed record CreateDirectoryResponse
    {
        [JsonPropertyName("fs_id")]
        public long FileSystemId { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("server_filename")]
        public string? Name { get; init; }

        [JsonPropertyName("ctime")]
        public long CreatedAt { get; init; }

        [JsonPropertyName("mtime")]
        public long ModifiedAt { get; init; }
    }

    private sealed record FileManagerEntry
    {
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("dest")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Destination { get; init; }

        [JsonPropertyName("newname")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NewName { get; init; }

        [JsonPropertyName("ondup")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OnDuplicate { get; init; }
    }

    private sealed record FileManagerResponse
    {
        [JsonPropertyName("info")]
        public List<FileManagerItemResponse> Info { get; init; } = [];

        [JsonPropertyName("request_id")]
        [JsonConverter(typeof(FlexibleStringJsonConverter))]
        public string? RequestId { get; init; }
    }

    private sealed record FileManagerItemResponse
    {
        [JsonPropertyName("errno")]
        public int ErrorCode { get; init; }

        [JsonPropertyName("errmsg")]
        public string? ErrorMessage { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }
    }
}
