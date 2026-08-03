using System.Net;
using System.Text;
using BaiduNetdisk.OAuth;

var tests = new (string Name, Func<Task> Run)[]
{
    ("授权地址包含必要参数", AuthorizationUriContainsRequiredParameters),
    ("回调地址校验 state", CallbackParserValidatesState),
    ("授权码可换取 Token", ExchangeCodeParsesToken),
    ("OAuth 错误被转换为异常", OAuthErrorIsMapped),
    ("无效响应被转换为异常", InvalidResponseIsMapped)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static Task AuthorizationUriContainsRequiredParameters()
{
    var client = CreateClient(_ => Json(HttpStatusCode.OK, "{}"));
    var uri = client.BuildAuthorizationUri("state-value", forceLogin: true);

    Assert(uri.Scheme == Uri.UriSchemeHttps, "授权地址必须使用 HTTPS。");
    Assert(uri.Host == "openapi.baidu.com", "授权地址 Host 不正确。");
    Assert(uri.Query.Contains("response_type=code", StringComparison.Ordinal), "缺少 response_type。");
    Assert(uri.Query.Contains("client_id=client-id", StringComparison.Ordinal), "缺少 client_id。");
    Assert(uri.Query.Contains("scope=basic%20netdisk", StringComparison.Ordinal), "scope 编码不正确。");
    Assert(uri.Query.Contains("state=state-value", StringComparison.Ordinal), "缺少 state。");
    Assert(uri.Query.Contains("force_login=1", StringComparison.Ordinal), "缺少 force_login。");
    return Task.CompletedTask;
}

static Task CallbackParserValidatesState()
{
    var code = OAuthCallbackParser.GetCode(
        "https://example.test/callback?code=abc%2B123&state=expected",
        "expected");
    Assert(code == "abc+123", "没有正确解码 code。");

    AssertThrows<InvalidOperationException>(() =>
        OAuthCallbackParser.GetCode(
            "https://example.test/callback?code=abc&state=other",
            "expected"));
    return Task.CompletedTask;
}

static async Task ExchangeCodeParsesToken()
{
    Uri? requestUri = null;
    var client = CreateClient(request =>
    {
        requestUri = request.RequestUri;
        return Json(
            HttpStatusCode.OK,
            """
            {"access_token":"access-value","expires_in":3600,"refresh_token":"refresh-value","scope":"basic netdisk"}
            """);
    });

    var token = await client.ExchangeCodeAsync("code-value");

    Assert(token.AccessToken == "access-value", "access_token 解析失败。");
    Assert(token.RefreshToken == "refresh-value", "refresh_token 解析失败。");
    Assert(token.ExpiresIn == 3600, "expires_in 解析失败。");
    Assert(requestUri?.Query.Contains("grant_type=authorization_code", StringComparison.Ordinal) == true,
        "Token 请求 grant_type 不正确。");
    Assert(requestUri?.Query.Contains("redirect_uri=oob", StringComparison.Ordinal) == true,
        "Token 请求缺少 redirect_uri。");
}

static async Task OAuthErrorIsMapped()
{
    var client = CreateClient(_ => Json(
        HttpStatusCode.BadRequest,
        """{"error":"invalid_grant","error_description":"code expired"}"""));

    try
    {
        await client.ExchangeCodeAsync("expired-code");
        throw new InvalidOperationException("预期抛出 BaiduOAuthException。");
    }
    catch (BaiduOAuthException exception)
    {
        Assert(exception.Error == "invalid_grant", "OAuth error 映射失败。");
        Assert(exception.Description == "code expired", "OAuth error_description 映射失败。");
        Assert(exception.StatusCode == 400, "HTTP 状态码映射失败。");
    }
}

static async Task InvalidResponseIsMapped()
{
    var client = CreateClient(_ => Json(HttpStatusCode.OK, "not-json"));

    try
    {
        await client.ExchangeCodeAsync("code-value");
        throw new InvalidOperationException("预期抛出 BaiduOAuthException。");
    }
    catch (BaiduOAuthException exception)
    {
        Assert(exception.Error == "invalid_response", "无效响应没有被安全映射。");
    }
}

static BaiduOAuthClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
{
    var options = new BaiduOAuthOptions
    {
        ClientId = "client-id",
        ClientSecret = "client-secret"
    };
    return new BaiduOAuthClient(new HttpClient(new StubHandler(responseFactory)), options);
}

static HttpResponseMessage Json(HttpStatusCode statusCode, string content) => new(statusCode)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json")
};

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
}

file sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responseFactory(request));
}
