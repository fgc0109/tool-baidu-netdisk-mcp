using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduNetdisk.Serialization;

namespace BaiduNetdisk.Api;

public sealed class BaiduNetdiskClient
{
    private static readonly Uri UserInfoEndpoint = new("https://pan.baidu.com/rest/2.0/xpan/nas");
    private static readonly Uri QuotaEndpoint = new("https://pan.baidu.com/api/quota");
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
