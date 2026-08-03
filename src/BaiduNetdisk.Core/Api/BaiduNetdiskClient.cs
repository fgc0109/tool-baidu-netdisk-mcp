using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduNetdisk.Serialization;

namespace BaiduNetdisk.Api;

public sealed class BaiduNetdiskClient
{
    private static readonly Uri UserInfoEndpoint = new("https://pan.baidu.com/rest/2.0/xpan/nas");
    private static readonly Uri QuotaEndpoint = new("https://pan.baidu.com/api/quota");
    private static readonly Uri FileEndpoint = new("https://pan.baidu.com/rest/2.0/xpan/file");
    private static readonly Uri MultimediaEndpoint = new("https://pan.baidu.com/rest/2.0/xpan/multimedia");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public BaiduNetdiskClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public Task<BaiduUserInfo> GetUserInfoAsync(
        string accessToken,
        CancellationToken cancellationToken = default) =>
        GetAsync<BaiduUserInfo>(
            UserInfoEndpoint,
            new Dictionary<string, string>
            {
                ["method"] = "uinfo",
                ["access_token"] = RequireAccessToken(accessToken)
            },
            cancellationToken);

    public Task<BaiduQuotaInfo> GetQuotaAsync(
        string accessToken,
        CancellationToken cancellationToken = default) =>
        GetAsync<BaiduQuotaInfo>(
            QuotaEndpoint,
            new Dictionary<string, string>
            {
                ["access_token"] = RequireAccessToken(accessToken),
                ["checkfree"] = "1",
                ["checkexpire"] = "1"
            },
            cancellationToken);

    public Task<BaiduFilePage> ListFilesAsync(
        string accessToken,
        string directoryPath = "/",
        int start = 0,
        int limit = 100,
        BaiduFileOrder order = BaiduFileOrder.Name,
        bool descending = false,
        bool foldersOnly = false,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(start, limit);
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "list",
            ["access_token"] = RequireAccessToken(accessToken),
            ["dir"] = RequireAbsolutePath(directoryPath),
            ["start"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["order"] = FormatOrder(order),
            ["desc"] = descending ? "1" : "0"
        };

        if (foldersOnly)
        {
            parameters["folder"] = "1";
        }

        return GetAsync<BaiduFilePage>(FileEndpoint, parameters, cancellationToken);
    }

    public Task<BaiduFilePage> SearchFilesAsync(
        string accessToken,
        string keyword,
        string directoryPath = "/",
        int page = 1,
        int pageSize = 100,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            throw new ArgumentException("搜索关键词不能为空。", nameof(keyword));
        }

        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "页码必须大于或等于 1。");
        }

        ValidateLimit(pageSize, nameof(pageSize));
        return GetAsync<BaiduFilePage>(
            FileEndpoint,
            new Dictionary<string, string>
            {
                ["method"] = "search",
                ["access_token"] = RequireAccessToken(accessToken),
                ["dir"] = RequireAbsolutePath(directoryPath),
                ["key"] = keyword,
                ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["num"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["recursion"] = recursive ? "1" : "0"
            },
            cancellationToken);
    }

    public Task<BaiduFileMetadataResult> GetFileMetadataAsync(
        string accessToken,
        IReadOnlyCollection<long> fileSystemIds,
        bool includeDownloadLink = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystemIds);
        if (fileSystemIds.Count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fileSystemIds),
                "一次必须查询 1～100 个 fs_id。");
        }

        if (fileSystemIds.Any(id => id <= 0))
        {
            throw new ArgumentException("fs_id 必须为正整数。", nameof(fileSystemIds));
        }

        return GetAsync<BaiduFileMetadataResult>(
            MultimediaEndpoint,
            new Dictionary<string, string>
            {
                ["method"] = "filemetas",
                ["access_token"] = RequireAccessToken(accessToken),
                ["fsids"] = JsonSerializer.Serialize(fileSystemIds),
                ["dlink"] = includeDownloadLink ? "1" : "0",
                ["thumb"] = "0",
                ["extra"] = "0"
            },
            cancellationToken);
    }

    private async Task<T> GetAsync<T>(
        Uri endpoint,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildUri(endpoint, parameters);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new BaiduNetdiskApiException(
                errorCode: -1,
                errorMessage: "百度网盘 API 返回的数据不是有效 JSON。",
                statusCode: (int)response.StatusCode,
                innerException: exception);
        }

        using (document)
        {
            ApiEnvelope? envelope;
            try
            {
                envelope = document.RootElement.Deserialize<ApiEnvelope>(JsonOptions);
            }
            catch (JsonException exception)
            {
                throw InvalidResponse(response, exception);
            }

            if (!response.IsSuccessStatusCode || envelope is null || envelope.ErrorCode != 0)
            {
                throw new BaiduNetdiskApiException(
                    envelope?.ErrorCode ?? -(int)response.StatusCode,
                    envelope?.ErrorMessage ?? "百度网盘 API 返回了 HTTP 错误。",
                    envelope?.RequestId,
                    (int)response.StatusCode);
            }

            T? result;
            try
            {
                result = document.RootElement.Deserialize<T>(JsonOptions);
            }
            catch (JsonException exception)
            {
                throw InvalidResponse(response, exception, envelope.RequestId);
            }

            return result ?? throw new BaiduNetdiskApiException(
                errorCode: -1,
                errorMessage: "百度网盘 API 返回的数据为空。",
                requestId: envelope.RequestId,
                statusCode: (int)response.StatusCode);
        }
    }

    private static BaiduNetdiskApiException InvalidResponse(
        HttpResponseMessage response,
        JsonException exception,
        string? requestId = null) =>
        new(
            errorCode: -1,
            errorMessage: "百度网盘 API 返回的字段格式无法解析。",
            requestId: requestId,
            statusCode: (int)response.StatusCode,
            innerException: exception);

    private static string RequireAccessToken(string accessToken) =>
        string.IsNullOrWhiteSpace(accessToken)
            ? throw new ArgumentException("Access Token 不能为空。", nameof(accessToken))
            : accessToken;

    private static string RequireAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("网盘路径必须是以 / 开头的绝对路径。", nameof(path));
        }

        return path;
    }

    private static void ValidatePage(int start, int limit)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "起始偏移量不能小于 0。");
        }

        ValidateLimit(limit, nameof(limit));
    }

    private static void ValidateLimit(int limit, string parameterName)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(parameterName, "每页数量必须在 1～1000 之间。");
        }
    }

    private static string FormatOrder(BaiduFileOrder order) => order switch
    {
        BaiduFileOrder.Name => "name",
        BaiduFileOrder.Time => "time",
        BaiduFileOrder.Size => "size",
        _ => throw new ArgumentOutOfRangeException(nameof(order), order, "不支持的排序方式。")
    };

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

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
}
