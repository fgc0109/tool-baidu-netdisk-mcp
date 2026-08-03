using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaiduNetdisk.OAuth;

public sealed class BaiduOAuthClient
{
    private static readonly Uri AuthorizationEndpoint = new("https://openapi.baidu.com/oauth/2.0/authorize");
    private static readonly Uri TokenEndpoint = new("https://openapi.baidu.com/oauth/2.0/token");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly BaiduOAuthOptions _options;

    public BaiduOAuthClient(HttpClient httpClient, BaiduOAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options;
    }

    public Uri BuildAuthorizationUri(
        string state,
        bool forceLogin = false,
        string display = "page")
    {
        _options.Validate(requireSecret: false);

        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("OAuth state 不能为空。", nameof(state));
        }

        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scope,
            ["state"] = state,
            ["display"] = display
        };

        if (forceLogin)
        {
            parameters["force_login"] = "1";
        }

        return BuildUri(AuthorizationEndpoint, parameters);
    }

    public Task<BaiduTokenSet> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("授权码不能为空。", nameof(code));
        }

        _options.Validate(requireSecret: true);
        return RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret!,
                ["redirect_uri"] = _options.RedirectUri
            },
            cancellationToken);
    }

    public Task<BaiduTokenSet> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("Refresh Token 不能为空。", nameof(refreshToken));
        }

        _options.Validate(requireSecret: true);
        return RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret!
            },
            cancellationToken);
    }

    private async Task<BaiduTokenSet> RequestTokenAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        // 百度官方文档指定 token 端点使用 GET；不要记录完整请求 URI，以免泄露 Secret Key。
        var requestUri = BuildUri(TokenEndpoint, parameters);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        OAuthError? oauthError = null;
        try
        {
            oauthError = JsonSerializer.Deserialize<OAuthError>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            // 下面统一转成不包含密钥和 Token 的错误。
        }

        if (!response.IsSuccessStatusCode || !string.IsNullOrWhiteSpace(oauthError?.Error))
        {
            throw new BaiduOAuthException(
                oauthError?.Error ?? $"http_{(int)response.StatusCode}",
                oauthError?.ErrorDescription ?? "百度 OAuth 服务返回了无法识别的错误。",
                (int)response.StatusCode);
        }

        BaiduTokenSet? token;
        try
        {
            token = JsonSerializer.Deserialize<BaiduTokenSet>(payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new BaiduOAuthException(
                "invalid_response",
                "百度 OAuth 服务返回的 Token 数据不是有效 JSON。",
                (int)response.StatusCode,
                exception);
        }

        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) ||
            string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new BaiduOAuthException(
                "invalid_response",
                "百度 OAuth 服务返回的数据缺少 access_token 或 refresh_token。",
                (int)response.StatusCode);
        }

        return token with { AcquiredAtUtc = DateTimeOffset.UtcNow };
    }

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    private sealed record OAuthError
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }
}
