using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaiduNetdisk.Api;
using BaiduNetdisk.Download;
using BaiduNetdisk.OAuth;
using BaiduNetdisk.Upload;

var tests = new (string Name, Func<Task> Run)[]
{
    ("授权地址包含必要参数", AuthorizationUriContainsRequiredParameters),
    ("回调地址校验 state", CallbackParserValidatesState),
    ("授权码可换取 Token", ExchangeCodeParsesToken),
    ("OAuth 错误被转换为异常", OAuthErrorIsMapped),
    ("无效响应被转换为异常", InvalidResponseIsMapped),
    ("读取网盘用户信息", UserInfoIsParsed),
    ("读取网盘容量", QuotaIsParsed),
    ("网盘业务错误被转换为异常", NetdiskErrorIsMapped),
    ("分页列出中文目录", FileListIsParsed),
    ("空目录返回空集合", EmptyDirectoryIsParsed),
    ("按特殊字符关键词搜索", FileSearchIsEncoded),
    ("批量读取文件元数据", FileMetadataIsParsed),
    ("拒绝无效文件查询参数", InvalidFileArgumentsAreRejected),
    ("流式下载并校验文件", FileDownloadIsVerified),
    ("默认拒绝覆盖文件", ExistingDownloadIsPreserved),
    ("校验失败时清理临时文件", InvalidDownloadIsCleanedUp),
    ("取消下载时清理临时文件", CancelledDownloadIsCleanedUp),
    ("完成预创建分片和最终创建", SmallFileUploadCompletes),
    ("仅上传缺失分片并重试", MissingUploadPartIsRetried),
    ("空文件支持服务端秒传", EmptyFileUsesRapidUpload),
    ("上传严格限制应用目录", UploadIsRestrictedToAppRoot),
    ("拒绝不可信上传域名", UntrustedUploadHostIsRejected)
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
    var client = CreateOAuthClient(_ => Json(HttpStatusCode.OK, "{}"));
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
    var client = CreateOAuthClient(request =>
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
    var client = CreateOAuthClient(_ => Json(
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
    var client = CreateOAuthClient(_ => Json(HttpStatusCode.OK, "not-json"));

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

static async Task UserInfoIsParsed()
{
    Uri? requestUri = null;
    var client = CreateNetdiskClient(request =>
    {
        requestUri = request.RequestUri;
        return Json(HttpStatusCode.OK, """
            {"errno":0,"errmsg":"succ","request_id":"req-1","baidu_name":"baidu-user","netdisk_name":"netdisk-user","avatar_url":"https://example.test/avatar","uk":123456,"vip_type":2}
            """);
    });

    var user = await client.GetUserInfoAsync("access-value");

    Assert(user.BaiduName == "baidu-user", "baidu_name 解析失败。");
    Assert(user.NetdiskName == "netdisk-user", "netdisk_name 解析失败。");
    Assert(user.UserId == 123456, "uk 解析失败。");
    Assert(user.VipType == 2, "vip_type 解析失败。");
    Assert(requestUri?.Query.Contains("method=uinfo", StringComparison.Ordinal) == true,
        "用户信息请求缺少 method=uinfo。");
}

static async Task QuotaIsParsed()
{
    var client = CreateNetdiskClient(_ => Json(HttpStatusCode.OK, """
        {"errno":0,"errmsg":"succ","request_id":42,"total":1000,"used":250}
        """));

    var quota = await client.GetQuotaAsync("access-value");

    Assert(quota.TotalBytes == 1000, "total 解析失败。");
    Assert(quota.UsedBytes == 250, "used 解析失败。");
    Assert(quota.RemainingBytes == 750, "剩余容量计算失败。");
    Assert(Math.Abs(quota.UsedRatio - 0.25) < 0.0001, "使用比例计算失败。");
    Assert(quota.RequestId == "42", "数字 request_id 兼容失败。");
}

static async Task NetdiskErrorIsMapped()
{
    var client = CreateNetdiskClient(_ => Json(HttpStatusCode.OK, """
        {"errno":-6,"errmsg":"No permission","request_id":"req-error"}
        """));

    try
    {
        await client.GetUserInfoAsync("access-value");
        throw new InvalidOperationException("预期抛出 BaiduNetdiskApiException。");
    }
    catch (BaiduNetdiskApiException exception)
    {
        Assert(exception.ErrorCode == -6, "errno 映射失败。");
        Assert(exception.ErrorMessage == "No permission", "errmsg 映射失败。");
        Assert(exception.RequestId == "req-error", "request_id 映射失败。");
    }
}

static async Task FileListIsParsed()
{
    Uri? requestUri = null;
    var client = CreateNetdiskClient(request =>
    {
        requestUri = request.RequestUri;
        return Json(HttpStatusCode.OK, """
            {"errno":0,"request_id":101,"list":[{"fs_id":9001,"server_filename":"报告 & 计划.txt","path":"/资料/报告 & 计划.txt","isdir":0,"size":2048,"category":4,"server_mtime":1710000000},{"fs_id":9002,"server_filename":"子目录","path":"/资料/子目录","isdir":1,"size":0,"category":6}]}
            """);
    });

    var page = await client.ListFilesAsync(
        "access-value",
        "/资料",
        start: 20,
        limit: 10,
        order: BaiduFileOrder.Time,
        descending: true);

    Assert(page.Items.Count == 2, "文件列表数量不正确。");
    Assert(page.Items[0].FileName == "报告 & 计划.txt", "中文文件名解析失败。");
    Assert(page.Items[0].SizeBytes == 2048, "文件大小解析失败。");
    Assert(!page.Items[0].IsDirectory && page.Items[1].IsDirectory, "目录标记解析失败。");
    Assert(page.RequestId == "101", "列表 request_id 解析失败。");
    Assert(requestUri?.Query.Contains("dir=%2F%E8%B5%84%E6%96%99", StringComparison.OrdinalIgnoreCase) == true,
        "中文目录没有正确编码。");
    Assert(requestUri?.Query.Contains("start=20", StringComparison.Ordinal) == true, "start 参数不正确。");
    Assert(requestUri?.Query.Contains("limit=10", StringComparison.Ordinal) == true, "limit 参数不正确。");
    Assert(requestUri?.Query.Contains("order=time", StringComparison.Ordinal) == true, "order 参数不正确。");
    Assert(requestUri?.Query.Contains("desc=1", StringComparison.Ordinal) == true, "desc 参数不正确。");
}

static async Task EmptyDirectoryIsParsed()
{
    var client = CreateNetdiskClient(_ => Json(HttpStatusCode.OK, """
        {"errno":0,"request_id":"empty","list":[]}
        """));

    var page = await client.ListFilesAsync("access-value", "/空目录");
    Assert(page.Items.Count == 0, "空目录应返回空集合。");
}

static async Task FileSearchIsEncoded()
{
    Uri? requestUri = null;
    var client = CreateNetdiskClient(request =>
    {
        requestUri = request.RequestUri;
        return Json(HttpStatusCode.OK, """
            {"errno":0,"request_id":"search","list":[{"fs_id":88,"server_filename":"预算 100%.xlsx","path":"/资料/预算 100%.xlsx","isdir":0,"size":10}]}
            """);
    });

    var page = await client.SearchFilesAsync(
        "access-value",
        "预算 & 100%",
        "/资料",
        page: 2,
        pageSize: 25,
        recursive: false);

    Assert(page.Items.Count == 1, "搜索结果解析失败。");
    Assert(requestUri?.Query.Contains("key=%E9%A2%84%E7%AE%97%20%26%20100%25", StringComparison.OrdinalIgnoreCase) == true,
        "搜索关键词没有正确编码。");
    Assert(requestUri?.Query.Contains("page=2", StringComparison.Ordinal) == true, "page 参数不正确。");
    Assert(requestUri?.Query.Contains("num=25", StringComparison.Ordinal) == true, "num 参数不正确。");
    Assert(requestUri?.Query.Contains("recursion=0", StringComparison.Ordinal) == true,
        "recursion 参数不正确。");
}

static async Task FileMetadataIsParsed()
{
    Uri? requestUri = null;
    var client = CreateNetdiskClient(request =>
    {
        requestUri = request.RequestUri;
        return Json(HttpStatusCode.OK, """
            {"errno":0,"request_id":202,"list":[{"fs_id":11,"filename":"文件.txt","path":"/文件.txt","isdir":0,"size":123,"category":4,"md5":"abc123","dlink":"https://example.test/download","server_mtime":1710000000}]}
            """);
    });

    var result = await client.GetFileMetadataAsync(
        "access-value",
        new long[] { 11, 22 },
        includeDownloadLink: true);

    Assert(result.Items.Count == 1, "元数据数量不正确。");
    Assert(result.Items[0].FileName == "文件.txt", "元数据 filename 解析失败。");
    Assert(result.Items[0].Md5 == "abc123", "MD5 解析失败。");
    Assert(result.Items[0].DownloadLink == "https://example.test/download", "下载地址解析失败。");
    Assert(requestUri?.Query.Contains("fsids=%5B11%2C22%5D", StringComparison.OrdinalIgnoreCase) == true,
        "fsids 没有正确编码。");
    Assert(requestUri?.Query.Contains("dlink=1", StringComparison.Ordinal) == true, "dlink 参数不正确。");
}

static Task InvalidFileArgumentsAreRejected()
{
    var client = CreateNetdiskClient(_ => Json(HttpStatusCode.OK, "{}"));
    AssertThrows<ArgumentException>(() => client.ListFilesAsync("access-value", "relative/path"));
    AssertThrows<ArgumentOutOfRangeException>(() =>
        client.ListFilesAsync("access-value", "/", limit: 1001));
    AssertThrows<ArgumentException>(() =>
        client.SearchFilesAsync("access-value", " ", "/"));
    AssertThrows<ArgumentOutOfRangeException>(() =>
        client.GetFileMetadataAsync("access-value", Array.Empty<long>()));
    return Task.CompletedTask;
}

static async Task FileDownloadIsVerified()
{
    var payload = Encoding.UTF8.GetBytes("hello from baidu netdisk");
    var md5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
    HttpRequestMessage? downloadRequest = null;
    var client = CreateDownloadService(request =>
    {
        if (request.RequestUri?.Host == "pan.baidu.com")
        {
            return MetadataResponse(501, payload.Length, md5);
        }

        downloadRequest = request;
        return Bytes(HttpStatusCode.OK, payload);
    });
    var directory = CreateTemporaryDirectory();
    var destination = Path.Combine(directory, "download.txt");
    var progress = new RecordingProgress();

    try
    {
        var result = await client.DownloadByFileSystemIdAsync(
            "access-value",
            501,
            destination,
            progress: progress);

        Assert(File.ReadAllBytes(destination).SequenceEqual(payload), "下载文件内容不正确。");
        Assert(result.BytesWritten == payload.Length, "下载字节数不正确。");
        Assert(result.Md5 == md5, "下载 MD5 不正确。");
        Assert(downloadRequest?.Headers.UserAgent.ToString() == "pan.baidu.com", "下载 User-Agent 不正确。");
        Assert(downloadRequest?.RequestUri?.Query.Contains("access_token=access-value", StringComparison.Ordinal) == true,
            "下载地址没有附加 Access Token。");
        Assert(progress.Last?.BytesReceived == payload.Length, "没有报告最终下载进度。");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task ExistingDownloadIsPreserved()
{
    var requestCount = 0;
    var client = CreateDownloadService(_ =>
    {
        requestCount++;
        return Json(HttpStatusCode.OK, "{}");
    });
    var directory = CreateTemporaryDirectory();
    var destination = Path.Combine(directory, "existing.txt");
    await File.WriteAllTextAsync(destination, "original");

    try
    {
        await AssertThrowsAsync<IOException>(() =>
            client.DownloadByFileSystemIdAsync("access-value", 501, destination));
        Assert(await File.ReadAllTextAsync(destination) == "original", "已有文件被修改。");
        Assert(requestCount == 0, "发现目标已存在后不应发起网络请求。");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task InvalidDownloadIsCleanedUp()
{
    var payload = Encoding.UTF8.GetBytes("corrupted");
    var client = CreateDownloadService(request =>
        request.RequestUri?.Host == "pan.baidu.com"
            ? MetadataResponse(502, payload.Length, "00000000000000000000000000000000")
            : Bytes(HttpStatusCode.OK, payload));
    var directory = CreateTemporaryDirectory();
    var destination = Path.Combine(directory, "invalid.bin");

    try
    {
        await AssertThrowsAsync<BaiduDownloadIntegrityException>(() =>
            client.DownloadByFileSystemIdAsync("access-value", 502, destination));
        Assert(!File.Exists(destination), "校验失败后不应生成目标文件。");
        Assert(Directory.GetFiles(directory, "*.partial").Length == 0, "校验失败后残留临时文件。");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task CancelledDownloadIsCleanedUp()
{
    var payload = new byte[300_000];
    Random.Shared.NextBytes(payload);
    var md5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
    var client = CreateDownloadService(request =>
        request.RequestUri?.Host == "pan.baidu.com"
            ? MetadataResponse(503, payload.Length, md5)
            : Bytes(HttpStatusCode.OK, payload));
    var directory = CreateTemporaryDirectory();
    var destination = Path.Combine(directory, "cancelled.bin");
    using var cancellation = new CancellationTokenSource();
    var progress = new CallbackProgress(value =>
    {
        if (value.BytesReceived > 0)
        {
            cancellation.Cancel();
        }
    });

    try
    {
        await AssertThrowsAsync<OperationCanceledException>(() =>
            client.DownloadByFileSystemIdAsync(
                "access-value",
                503,
                destination,
                progress: progress,
                cancellationToken: cancellation.Token));
        Assert(!File.Exists(destination), "取消后不应生成目标文件。");
        Assert(Directory.GetFiles(directory, "*.partial").Length == 0, "取消后残留临时文件。");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task SmallFileUploadCompletes()
{
    var payload = Encoding.UTF8.GetBytes("small upload content");
    var expectedMd5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
    var directory = CreateTemporaryDirectory();
    var localPath = Path.Combine(directory, "upload.txt");
    await File.WriteAllBytesAsync(localPath, payload);
    var calls = new List<string>();
    var progress = new RecordingUploadProgress();
    var service = CreateUploadService(request =>
    {
        var method = QueryValue(request.RequestUri!, "method");
        calls.Add(method ?? "unknown");
        return method switch
        {
            "precreate" => HandlePrecreate(request, expectedMd5, payload.Length, [0]),
            "locateupload" => Json(HttpStatusCode.OK,
                """{"errno":0,"request_id":2,"servers":[{"server":"https://c1.pcs.baidu.com"}]}"""),
            "upload" => HandleUploadPart(request, payload, expectedPartIndex: 0),
            "create" => HandleCreate(request, expectedMd5, payload.Length, 7001),
            _ => throw new InvalidOperationException($"未知上传调用：{method}")
        };
    });

    try
    {
        var result = await service.UploadFileAsync(
            "access-value",
            localPath,
            "/apps/demo/upload.txt",
            progress: progress);

        Assert(calls.SequenceEqual(new[] { "precreate", "locateupload", "upload", "create" }),
            "上传调用顺序不正确。");
        Assert(result.FileSystemId == 7001, "上传结果 fs_id 不正确。");
        Assert(result.RemotePath == "/apps/demo/upload.txt", "上传结果路径不正确。");
        Assert(!result.UsedRapidUpload, "普通上传不应标记为秒传。");
        Assert(progress.Last?.BytesCompleted == payload.Length, "上传最终进度不正确。");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task MissingUploadPartIsRetried()
{
    var payload = new byte[BaiduUploadOptions.DefaultChunkSize + 3];
    Random.Shared.NextBytes(payload);
    var directory = CreateTemporaryDirectory();
    var localPath = Path.Combine(directory, "multi.bin");
    await File.WriteAllBytesAsync(localPath, payload);
    var uploadAttempts = 0;
    var service = CreateUploadService(request =>
    {
        var method = QueryValue(request.RequestUri!, "method");
        if (method == "precreate")
        {
            var form = ParseForm(request.Content!);
            var hashes = JsonSerializer.Deserialize<string[]>(form["block_list"]);
            Assert(hashes?.Length == 2, "大文件没有按 4 MiB 分片。");
            return Json(HttpStatusCode.OK,
                """{"errno":0,"request_id":1,"uploadid":"upload-2","block_list":[1]}""");
        }

        if (method == "locateupload")
        {
            return Json(HttpStatusCode.OK,
                """{"errno":0,"servers":[{"server":"https://c2.pcs.baidu.com"}]}""");
        }

        if (method == "upload")
        {
            uploadAttempts++;
            Assert(QueryValue(request.RequestUri!, "partseq") == "1", "上传了非缺失分片。");
            if (uploadAttempts == 1)
            {
                return Json(HttpStatusCode.ServiceUnavailable,
                    """{"errno":-1,"errmsg":"temporary"}""");
            }

            return HandleUploadPart(request, payload[^3..], expectedPartIndex: 1);
        }

        if (method == "create")
        {
            return HandleCreate(request, "server-md5", payload.Length, 7002);
        }

        throw new InvalidOperationException($"未知上传调用：{method}");
    });

    try
    {
        var result = await service.UploadFileAsync(
            "access-value",
            localPath,
            "/apps/demo/multi.bin");
        Assert(uploadAttempts == 2, "临时错误后没有重试一次。");
        Assert(result.SizeBytes == payload.Length, "多分片上传大小不正确。");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task EmptyFileUsesRapidUpload()
{
    var directory = CreateTemporaryDirectory();
    var localPath = Path.Combine(directory, "empty.txt");
    await File.WriteAllBytesAsync(localPath, Array.Empty<byte>());
    var calls = new List<string>();
    var emptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";
    var service = CreateUploadService(request =>
    {
        var method = QueryValue(request.RequestUri!, "method")!;
        calls.Add(method);
        if (method == "precreate")
        {
            return HandlePrecreate(request, emptyMd5, 0, []);
        }

        if (method == "create")
        {
            return HandleCreate(request, emptyMd5, 0, 7003);
        }

        throw new InvalidOperationException("秒传不应请求上传域名或发送分片。");
    });

    try
    {
        var result = await service.UploadFileAsync(
            "access-value",
            localPath,
            "/apps/demo/empty.txt");
        Assert(result.UsedRapidUpload, "空文件未使用秒传流程。");
        Assert(calls.SequenceEqual(new[] { "precreate", "create" }), "秒传调用顺序不正确。");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task UploadIsRestrictedToAppRoot()
{
    var directory = CreateTemporaryDirectory();
    var localPath = Path.Combine(directory, "local.txt");
    await File.WriteAllTextAsync(localPath, "content");
    var requestCount = 0;
    var service = CreateUploadService(_ =>
    {
        requestCount++;
        return Json(HttpStatusCode.OK, "{}");
    });

    try
    {
        await AssertThrowsAsync<ArgumentException>(() =>
            service.UploadFileAsync("access-value", localPath, "/outside/local.txt"));
        await AssertThrowsAsync<ArgumentException>(() =>
            service.UploadFileAsync("access-value", localPath, "/apps/demo/../escape.txt"));
        Assert(requestCount == 0, "非法远程路径不应发起网络请求。");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task UntrustedUploadHostIsRejected()
{
    var directory = CreateTemporaryDirectory();
    var localPath = Path.Combine(directory, "local.txt");
    await File.WriteAllTextAsync(localPath, "content");
    var service = CreateUploadService(request =>
    {
        var method = QueryValue(request.RequestUri!, "method");
        return method switch
        {
            "precreate" => Json(HttpStatusCode.OK,
                """{"errno":0,"uploadid":"upload-4","block_list":[0]}"""),
            "locateupload" => Json(HttpStatusCode.OK,
                """{"errno":0,"servers":[{"server":"https://malicious.example/upload"}]}"""),
            _ => throw new InvalidOperationException("不可信域名不应收到分片或创建请求。")
        };
    });

    try
    {
        await AssertThrowsAsync<BaiduNetdiskApiException>(() =>
            service.UploadFileAsync("access-value", localPath, "/apps/demo/local.txt"));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static BaiduOAuthClient CreateOAuthClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
{
    var options = new BaiduOAuthOptions
    {
        ClientId = "client-id",
        ClientSecret = "client-secret"
    };
    return new BaiduOAuthClient(new HttpClient(new StubHandler(responseFactory)), options);
}

static BaiduNetdiskClient CreateNetdiskClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
    new(new HttpClient(new StubHandler(responseFactory)));

static BaiduDownloadService CreateDownloadService(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
{
    var httpClient = new HttpClient(new StubHandler(responseFactory));
    return new BaiduDownloadService(httpClient, new BaiduNetdiskClient(httpClient));
}

static BaiduUploadService CreateUploadService(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
{
    var httpClient = new HttpClient(new StubHandler(responseFactory));
    return new BaiduUploadService(httpClient, new BaiduUploadOptions { AppRoot = "/apps/demo" });
}

static HttpResponseMessage HandlePrecreate(
    HttpRequestMessage request,
    string expectedMd5,
    int expectedSize,
    int[] requestedParts)
{
    Assert(request.Method == HttpMethod.Post, "预创建必须使用 POST。");
    Assert(QueryValue(request.RequestUri!, "openapi") == "xpansdk", "预创建缺少 openapi 标记。");
    var form = ParseForm(request.Content!);
    Assert(form["path"] == "/apps/demo/upload.txt" ||
           form["path"] == "/apps/demo/empty.txt", "预创建远程路径不正确。");
    Assert(form["size"] == expectedSize.ToString(), "预创建文件大小不正确。");
    Assert(form["content-md5"] == expectedMd5, "预创建文件 MD5 不正确。");
    Assert(form["rtype"] == "1", "默认重名策略应为重命名。");
    return Json(HttpStatusCode.OK, $$"""
        {"errno":0,"request_id":1,"uploadid":"upload-1","block_list":{{JsonSerializer.Serialize(requestedParts)}}}
        """);
}

static HttpResponseMessage HandleUploadPart(
    HttpRequestMessage request,
    byte[] expectedPayload,
    int expectedPartIndex)
{
    Assert(request.RequestUri?.Host.EndsWith(".pcs.baidu.com", StringComparison.Ordinal) == true,
        "没有使用 locateupload 返回的上传域名。");
    Assert(QueryValue(request.RequestUri!, "openapi") == "xpansdk", "分片请求缺少 openapi 标记。");
    Assert(QueryValue(request.RequestUri!, "partseq") == expectedPartIndex.ToString(), "分片序号不正确。");
    var multipart = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    Assert(ContainsSequence(multipart, expectedPayload), "multipart 请求不包含预期分片内容。");
    return Json(HttpStatusCode.OK, """{"errno":0,"md5":"cloud-part-md5"}""");
}

static HttpResponseMessage HandleCreate(
    HttpRequestMessage request,
    string md5,
    int size,
    long fileSystemId)
{
    Assert(QueryValue(request.RequestUri!, "openapi") == "xpansdk", "最终创建缺少 openapi 标记。");
    var form = ParseForm(request.Content!);
    Assert(form["uploadid"] == "upload-1" || form["uploadid"] == "upload-2",
        "最终创建 uploadid 不正确。");
    Assert(form["block_list"].StartsWith("[", StringComparison.Ordinal), "最终创建缺少 block_list。");
    return Json(HttpStatusCode.OK, $$"""
        {"errno":0,"fs_id":{{fileSystemId}},"path":"{{form["path"]}}","size":{{size}},"md5":"{{md5}}"}
        """);
}

static Dictionary<string, string> ParseForm(HttpContent content) =>
    content.ReadAsStringAsync().GetAwaiter().GetResult()
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => segment.Split('=', 2))
        .ToDictionary(
            pair => WebUtility.UrlDecode(pair[0]),
            pair => WebUtility.UrlDecode(pair.Length == 1 ? string.Empty : pair[1]),
            StringComparer.Ordinal);

static string? QueryValue(Uri uri, string name)
{
    foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var pair = segment.Split('=', 2);
        if (string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.Ordinal))
        {
            return Uri.UnescapeDataString(pair.Length == 1 ? string.Empty : pair[1]);
        }
    }

    return null;
}

static bool ContainsSequence(byte[] source, byte[] expected)
{
    if (expected.Length == 0)
    {
        return true;
    }

    return source.AsSpan().IndexOf(expected) >= 0;
}

static HttpResponseMessage MetadataResponse(long fileSystemId, int size, string md5) =>
    Json(HttpStatusCode.OK, $$"""
        {"errno":0,"request_id":1,"list":[{"fs_id":{{fileSystemId}},"filename":"test.bin","path":"/test.bin","isdir":0,"size":{{size}},"md5":"{{md5}}","dlink":"https://d.pcs.baidu.com/file/test"}]}
        """);

static HttpResponseMessage Bytes(HttpStatusCode statusCode, byte[] content) => new(statusCode)
{
    Content = new ByteArrayContent(content)
};

static string CreateTemporaryDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), $"baidu-netdisk-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    return path;
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

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
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

file sealed class RecordingProgress : IProgress<BaiduDownloadProgress>
{
    public BaiduDownloadProgress? Last { get; private set; }

    public void Report(BaiduDownloadProgress value) => Last = value;
}

file sealed class CallbackProgress(Action<BaiduDownloadProgress> callback) : IProgress<BaiduDownloadProgress>
{
    public void Report(BaiduDownloadProgress value) => callback(value);
}

file sealed class RecordingUploadProgress : IProgress<BaiduUploadProgress>
{
    public BaiduUploadProgress? Last { get; private set; }

    public void Report(BaiduUploadProgress value) => Last = value;
}
