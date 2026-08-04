using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BaiduNetdisk.Api;
using BaiduNetdisk.Diagnostics;
using BaiduNetdisk.Download;
using BaiduNetdisk.Management;
using BaiduNetdisk.OAuth;
using BaiduNetdisk.Storage;
using BaiduNetdisk.Upload;

Console.OutputEncoding = Encoding.UTF8;
return await BaiduCli.RunAsync(args);

internal static class BaiduCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            var commandArgs = args[1..];
            var options = ReadOptions();
            var tokenStore = BaiduTokenStoreFactory.Create(
                GetTokenPath(),
                BaiduTokenStoreFactory.ParseMode(
                    Environment.GetEnvironmentVariable("BAIDU_TOKEN_PROTECTION")));

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            var oauth = new BaiduOAuthClient(httpClient, options);
            using var authenticatedSession = new BaiduAuthenticatedSession(oauth, tokenStore);
            var netdisk = new BaiduNetdiskClient(httpClient);
            var downloader = new BaiduDownloadService(httpClient, netdisk);

            return command switch
            {
                "auth-url" => PrintAuthorizationUrl(oauth, commandArgs),
                "login" => await LoginAsync(oauth, tokenStore, commandArgs),
                "exchange" => await ExchangeAsync(oauth, tokenStore, commandArgs),
                "refresh" => await RefreshAsync(oauth, tokenStore, commandArgs),
                "show" => await ShowAsync(tokenStore),
                "account" => await ShowAccountAsync(netdisk, authenticatedSession),
                "quota" => await ShowQuotaAsync(netdisk, authenticatedSession),
                "ls" => await ListFilesAsync(netdisk, authenticatedSession, commandArgs),
                "search" => await SearchFilesAsync(netdisk, authenticatedSession, commandArgs),
                "meta" => await ShowMetadataAsync(netdisk, authenticatedSession, commandArgs),
                "download" => await DownloadFileAsync(downloader, authenticatedSession, commandArgs),
                "upload" => await UploadFileAsync(httpClient, authenticatedSession, commandArgs),
                "mkdir" => await CreateDirectoryAsync(httpClient, authenticatedSession, commandArgs),
                "copy" => await TransferFilesAsync(httpClient, authenticatedSession, commandArgs, move: false),
                "move" => await TransferFilesAsync(httpClient, authenticatedSession, commandArgs, move: true),
                "rename" => await RenameFileAsync(httpClient, authenticatedSession, commandArgs),
                "delete" => await DeleteFilesAsync(httpClient, authenticatedSession, commandArgs),
                _ => throw new ArgumentException($"未知命令：{command}")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 2;
        }
        catch (BaiduReauthorizationRequiredException exception)
        {
            Console.Error.WriteLine($"授权失效：{exception.Message}");
            return 3;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("错误：连接百度服务失败，请稍后重试。");
            return 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or BaiduOAuthException or BaiduNetdiskApiException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"错误：{SensitiveDataRedactor.Redact(exception.Message)}");
            return 1;
        }
    }

    private static int PrintAuthorizationUrl(BaiduOAuthClient oauth, string[] args)
    {
        var state = GetOption(args, "--state") ?? CreateState();
        var uri = oauth.BuildAuthorizationUri(state, HasFlag(args, "--force-login"));
        Console.WriteLine(uri.AbsoluteUri);
        Console.WriteLine($"state={state}");
        return 0;
    }

    private static async Task<int> LoginAsync(
        BaiduOAuthClient oauth,
        IBaiduTokenStore tokenStore,
        string[] args)
    {
        var state = CreateState();
        var uri = oauth.BuildAuthorizationUri(state, HasFlag(args, "--force-login"));
        Console.WriteLine("请在浏览器中登录百度帐号并授权：");
        Console.WriteLine(uri.AbsoluteUri);

        if (!HasFlag(args, "--no-browser"))
        {
            TryOpenBrowser(uri);
        }

        Console.Write("粘贴授权码（或完整回调地址）后按 Enter：");
        var input = Console.ReadLine();
        var expectedState = Uri.TryCreate(input, UriKind.Absolute, out _) ? state : null;
        var code = OAuthCallbackParser.GetCode(input ?? string.Empty, expectedState);
        var token = await oauth.ExchangeCodeAsync(code);
        await tokenStore.SaveAsync(token);
        PrintSavedToken(token, tokenStore.Path);
        return 0;
    }

    private static async Task<int> ExchangeAsync(
        BaiduOAuthClient oauth,
        IBaiduTokenStore tokenStore,
        string[] args)
    {
        var input = GetOption(args, "--code") ?? FirstPositional(args)
            ?? throw new ArgumentException("请提供 --code <授权码>，也可以直接传入完整回调地址。");
        var expectedState = GetOption(args, "--state");
        var code = OAuthCallbackParser.GetCode(input, expectedState);
        var token = await oauth.ExchangeCodeAsync(code);
        await tokenStore.SaveAsync(token);
        PrintSavedToken(token, tokenStore.Path);
        return 0;
    }

    private static async Task<int> RefreshAsync(
        BaiduOAuthClient oauth,
        IBaiduTokenStore tokenStore,
        string[] args)
    {
        var stored = await tokenStore.LoadAsync();
        var refreshToken = GetOption(args, "--refresh-token") ?? stored?.RefreshToken
            ?? throw new InvalidOperationException("没有找到已保存的 Refresh Token，请先执行 login。 ");
        var token = await oauth.RefreshTokenAsync(refreshToken);
        await tokenStore.SaveAsync(token);
        PrintSavedToken(token, tokenStore.Path);
        return 0;
    }

    private static async Task<int> ShowAsync(IBaiduTokenStore tokenStore)
    {
        var token = await tokenStore.LoadAsync()
            ?? throw new InvalidOperationException($"Token 文件不存在：{tokenStore.Path}");
        Console.WriteLine($"Access Token : {Redact(token.AccessToken)}");
        Console.WriteLine($"Refresh Token: {Redact(token.RefreshToken)}");
        Console.WriteLine($"Scope        : {token.Scope ?? "(未返回)"}");
        Console.WriteLine($"获取时间 UTC : {token.AcquiredAtUtc:O}");
        Console.WriteLine($"过期时间 UTC : {token.ExpiresAtUtc:O}");
        Console.WriteLine($"即将过期     : {(token.IsExpiringSoon ? "是" : "否")}");
        Console.WriteLine($"Token 文件   : {tokenStore.Path}");
        return 0;
    }

    private static async Task<int> ShowAccountAsync(
        BaiduNetdiskClient netdisk,
        BaiduAuthenticatedSession authenticatedSession)
    {
        var user = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) =>
                netdisk.GetUserInfoAsync(accessToken, cancellationToken));

        Console.WriteLine($"百度名称: {user.BaiduName ?? "(未返回)"}");
        Console.WriteLine($"网盘名称: {user.NetdiskName ?? "(未返回)"}");
        Console.WriteLine($"用户标识: {user.UserId}");
        Console.WriteLine($"会员类型: {FormatVipType(user.VipType)}");
        Console.WriteLine($"头像地址: {user.AvatarUrl ?? "(未返回)"}");
        return 0;
    }

    private static async Task<int> ShowQuotaAsync(
        BaiduNetdiskClient netdisk,
        BaiduAuthenticatedSession authenticatedSession)
    {
        var quota = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) =>
                netdisk.GetQuotaAsync(accessToken, cancellationToken));

        Console.WriteLine($"总容量 : {FormatBytes(quota.TotalBytes)} ({quota.TotalBytes} bytes)");
        Console.WriteLine($"已使用 : {FormatBytes(quota.UsedBytes)} ({quota.UsedBytes} bytes)");
        Console.WriteLine($"剩余   : {FormatBytes(quota.RemainingBytes)} ({quota.RemainingBytes} bytes)");
        Console.WriteLine($"使用率 : {quota.UsedRatio:P2}");
        return 0;
    }

    private static async Task<int> ListFilesAsync(
        BaiduNetdiskClient netdisk,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args)
    {
        var directory = GetOption(args, "--dir") ?? "/";
        var start = GetIntOption(args, "--start", 0);
        var limit = GetIntOption(args, "--limit", 100);
        var order = ParseFileOrder(GetOption(args, "--order") ?? "name");
        var page = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) => netdisk.ListFilesAsync(
                accessToken,
                directory,
                start,
                limit,
                order,
                descending: HasFlag(args, "--desc"),
                foldersOnly: HasFlag(args, "--folders-only"),
                cancellationToken: cancellationToken));

        PrintFileEntries(page.Items);
        Console.WriteLine($"返回 {page.Items.Count} 项；下一页 start={start + page.Items.Count}");
        return 0;
    }

    private static async Task<int> SearchFilesAsync(
        BaiduNetdiskClient netdisk,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args)
    {
        var keyword = GetOption(args, "--key")
            ?? throw new ArgumentException("请提供 --key <搜索关键词>。");
        var directory = GetOption(args, "--dir") ?? "/";
        var pageNumber = GetIntOption(args, "--page", 1);
        var pageSize = GetIntOption(args, "--page-size", 100);
        var result = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) => netdisk.SearchFilesAsync(
                accessToken,
                keyword,
                directory,
                pageNumber,
                pageSize,
                recursive: !HasFlag(args, "--current-dir-only"),
                cancellationToken: cancellationToken));

        PrintFileEntries(result.Items);
        Console.WriteLine($"返回 {result.Items.Count} 项；下一页 page={pageNumber + 1}");
        return 0;
    }

    private static async Task<int> ShowMetadataAsync(
        BaiduNetdiskClient netdisk,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args)
    {
        var ids = ParseFileSystemIds(GetOption(args, "--fs-id")
            ?? throw new ArgumentException("请提供 --fs-id <ID[,ID...]>。"));
        var result = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) =>
                netdisk.GetFileMetadataAsync(accessToken, ids, cancellationToken: cancellationToken));

        if (result.Items.Count == 0)
        {
            Console.WriteLine("未找到文件元数据。");
            return 0;
        }

        foreach (var item in result.Items)
        {
            Console.WriteLine($"{(item.IsDirectory ? "目录" : "文件")}: {item.Path}");
            Console.WriteLine($"  fs_id : {item.FileSystemId}");
            Console.WriteLine($"  大小  : {FormatBytes(item.SizeBytes)} ({item.SizeBytes} bytes)");
            Console.WriteLine($"  MD5   : {item.Md5 ?? "(未返回)"}");
            Console.WriteLine($"  修改  : {FormatTimestamp(item.ServerModifiedAt)}");
        }

        return 0;
    }

    private static void PrintFileEntries(IReadOnlyList<BaiduFileEntry> items)
    {
        if (items.Count == 0)
        {
            Console.WriteLine("未找到文件。");
            return;
        }

        foreach (var item in items)
        {
            Console.WriteLine($"{(item.IsDirectory ? "[D]" : "[F]")} {item.Path}");
            Console.WriteLine(
                $"    fs_id={item.FileSystemId}  size={FormatBytes(item.SizeBytes)}  modified={FormatTimestamp(item.ServerModifiedAt)}");
        }
    }

    private static async Task<int> DownloadFileAsync(
        BaiduDownloadService downloader,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args)
    {
        var ids = ParseFileSystemIds(GetOption(args, "--fs-id")
            ?? throw new ArgumentException("请提供 --fs-id <ID>。"));
        if (ids.Length != 1)
        {
            throw new ArgumentException("download 命令一次只能下载一个 fs_id。");
        }

        var outputPath = GetOption(args, "--output")
            ?? throw new ArgumentException("请提供 --output <本地文件路径>。");
        using var cancellation = new CancellationTokenSource();
        var reporter = new ConsoleDownloadProgress();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await authenticatedSession.ExecuteAsync(
                (accessToken, cancellationToken) => downloader.DownloadByFileSystemIdAsync(
                    accessToken,
                    ids[0],
                    outputPath,
                    overwrite: HasFlag(args, "--overwrite"),
                    progress: reporter,
                    cancellationToken: cancellationToken),
                cancellation.Token);
            reporter.Complete();
            Console.WriteLine($"下载完成: {result.DestinationPath}");
            Console.WriteLine($"文件大小: {FormatBytes(result.BytesWritten)} ({result.BytesWritten} bytes)");
            Console.WriteLine($"MD5     : {result.Md5}");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            reporter.Complete();
        }
    }

    private static async Task<int> UploadFileAsync(
        HttpClient httpClient,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args)
    {
        var localPath = GetOption(args, "--local")
            ?? throw new ArgumentException("请提供 --local <本地文件路径>。");
        var remotePath = GetOption(args, "--remote")
            ?? throw new ArgumentException("请提供 --remote <网盘文件路径>。");
        var appRoot = Environment.GetEnvironmentVariable("BAIDU_APP_ROOT")
            ?? throw new InvalidOperationException(
                "上传前请设置 BAIDU_APP_ROOT，例如 /apps/你的应用名。");
        var conflictPolicy = ParseUploadConflictPolicy(
            GetOption(args, "--on-conflict") ?? "rename");
        var uploader = new BaiduUploadService(
            httpClient,
            new BaiduUploadOptions { AppRoot = appRoot });
        using var cancellation = new CancellationTokenSource();
        var reporter = new ConsoleUploadProgress();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await authenticatedSession.ExecuteAsync(
                (accessToken, cancellationToken) => uploader.UploadFileAsync(
                    accessToken,
                    localPath,
                    remotePath,
                    conflictPolicy,
                    reporter,
                    cancellationToken),
                cancellation.Token);
            reporter.Complete();
            Console.WriteLine($"上传完成: {result.RemotePath}");
            Console.WriteLine($"文件标识: {result.FileSystemId}");
            Console.WriteLine($"文件大小: {FormatBytes(result.SizeBytes)} ({result.SizeBytes} bytes)");
            Console.WriteLine($"MD5     : {result.Md5 ?? "(未返回)"}");
            Console.WriteLine($"服务端秒传: {(result.UsedRapidUpload ? "是" : "否")}");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            reporter.Complete();
        }
    }

    private static async Task<int> CreateDirectoryAsync(
        HttpClient httpClient,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args)
    {
        var path = GetOption(args, "--path")
            ?? throw new ArgumentException("请提供 --path <网盘目录路径>。");
        var conflictPolicy = ParseFileConflictPolicy(GetOption(args, "--on-conflict") ?? "fail");
        var service = CreateManagementService(httpClient);
        var result = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) =>
                service.CreateDirectoryAsync(accessToken, path, conflictPolicy, cancellationToken));
        Console.WriteLine($"目录已创建: {result.Path}");
        Console.WriteLine($"文件标识: {result.FileSystemId}");
        return 0;
    }

    private static async Task<int> TransferFilesAsync(
        HttpClient httpClient,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args,
        bool move)
    {
        var sourcePaths = GetOptions(args, "--source");
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("请至少提供一个 --source <网盘路径>。");
        }

        var destination = GetOption(args, "--dest")
            ?? throw new ArgumentException("请提供 --dest <目标目录>。");
        var newName = GetOption(args, "--new-name");
        if (newName is not null && sourcePaths.Count != 1)
        {
            throw new ArgumentException("--new-name 只能与单个 --source 一起使用。");
        }

        var requests = sourcePaths
            .Select(path => new BaiduFileTransferRequest(path, destination, newName))
            .ToArray();
        var conflictPolicy = ParseFileConflictPolicy(GetOption(args, "--on-conflict") ?? "fail");
        var service = CreateManagementService(httpClient);
        var result = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) => move
                ? service.MoveAsync(accessToken, requests, conflictPolicy, cancellationToken)
                : service.CopyAsync(accessToken, requests, conflictPolicy, cancellationToken));
        return PrintBatchResult(result);
    }

    private static async Task<int> RenameFileAsync(
        HttpClient httpClient,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args)
    {
        var path = GetOption(args, "--path")
            ?? throw new ArgumentException("请提供 --path <网盘路径>。");
        var newName = GetOption(args, "--name")
            ?? throw new ArgumentException("请提供 --name <新名称>。");
        var service = CreateManagementService(httpClient);
        var result = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) => service.RenameAsync(
                accessToken,
                new[] { new BaiduFileRenameRequest(path, newName) },
                cancellationToken));
        return PrintBatchResult(result);
    }

    private static async Task<int> DeleteFilesAsync(
        HttpClient httpClient,
        BaiduAuthenticatedSession authenticatedSession,
        string[] args)
    {
        var paths = GetOptions(args, "--path");
        if (paths.Count == 0)
        {
            throw new ArgumentException("请至少提供一个 --path <网盘路径>。");
        }

        if (!HasFlag(args, "--confirm"))
        {
            throw new InvalidOperationException("删除属于破坏性操作，请显式传入 --confirm。");
        }

        var service = CreateManagementService(httpClient);
        var result = await authenticatedSession.ExecuteAsync(
            (accessToken, cancellationToken) => service.DeleteAsync(
                accessToken,
                paths,
                confirmDelete: true,
                cancellationToken));
        return PrintBatchResult(result);
    }

    private static BaiduFileManagementService CreateManagementService(HttpClient httpClient)
    {
        var appRoot = Environment.GetEnvironmentVariable("BAIDU_APP_ROOT")
            ?? throw new InvalidOperationException(
                "文件管理前请设置 BAIDU_APP_ROOT，例如 /apps/你的应用名。");
        return new BaiduFileManagementService(
            httpClient,
            new BaiduFileManagementOptions { AppRoot = appRoot });
    }

    private static int PrintBatchResult(BaiduBatchOperationResult result)
    {
        foreach (var item in result.Items)
        {
            if (item.Success)
            {
                var destination = item.DestinationPath is null
                    ? string.Empty
                    : $" -> {item.DestinationPath}";
                Console.WriteLine($"成功: {item.SourcePath}{destination}");
            }
            else
            {
                Console.Error.WriteLine(
                    $"失败: {item.SourcePath} (errno={item.ErrorCode}, {item.ErrorMessage ?? "未返回错误信息"})");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.RequestId))
        {
            Console.WriteLine($"Request ID: {result.RequestId}");
        }

        return result.Success ? 0 : 1;
    }

    private static BaiduOAuthOptions ReadOptions() => new()
    {
        ClientId = Environment.GetEnvironmentVariable("BAIDU_CLIENT_ID") ?? string.Empty,
        ClientSecret = Environment.GetEnvironmentVariable("BAIDU_CLIENT_SECRET"),
        RedirectUri = Environment.GetEnvironmentVariable("BAIDU_REDIRECT_URI") ?? BaiduOAuthOptions.DefaultRedirectUri,
        Scope = Environment.GetEnvironmentVariable("BAIDU_OAUTH_SCOPE") ?? BaiduOAuthOptions.DefaultScope
    };

    private static string GetTokenPath()
    {
        var configured = Environment.GetEnvironmentVariable("BAIDU_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BaiduNetdiskMcp",
            "tokens.json");
    }

    private static string CreateState() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void TryOpenBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            Console.WriteLine("未能自动打开浏览器，请手动复制上面的地址。");
        }
    }

    private static void PrintSavedToken(BaiduTokenSet token, string path)
    {
        Console.WriteLine("授权成功。");
        Console.WriteLine($"Access Token: {Redact(token.AccessToken)}");
        Console.WriteLine($"过期时间 UTC: {token.ExpiresAtUtc:O}");
        Console.WriteLine($"Token 已保存: {path}");
    }

    private static string Redact(string value) =>
        value.Length <= 12 ? "***" : $"{value[..6]}...{value[^4..]}";

    private static string FormatVipType(int vipType) => vipType switch
    {
        0 => "普通用户",
        1 => "会员",
        2 => "超级会员",
        _ => $"未知 ({vipType})"
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
        ?? "(未返回)";

    private static BaiduFileOrder ParseFileOrder(string value) => value.ToLowerInvariant() switch
    {
        "name" => BaiduFileOrder.Name,
        "time" => BaiduFileOrder.Time,
        "size" => BaiduFileOrder.Size,
        _ => throw new ArgumentException("--order 只支持 name、time 或 size。")
    };

    private static BaiduUploadConflictPolicy ParseUploadConflictPolicy(string value) =>
        value.ToLowerInvariant() switch
        {
            "rename" => BaiduUploadConflictPolicy.Rename,
            "rename-if-different" => BaiduUploadConflictPolicy.RenameIfDifferent,
            "overwrite" => BaiduUploadConflictPolicy.Overwrite,
            _ => throw new ArgumentException(
                "--on-conflict 只支持 rename、rename-if-different 或 overwrite。")
        };

    private static BaiduFileConflictPolicy ParseFileConflictPolicy(string value) =>
        value.ToLowerInvariant() switch
        {
            "fail" => BaiduFileConflictPolicy.Fail,
            "rename" => BaiduFileConflictPolicy.Rename,
            "overwrite" => BaiduFileConflictPolicy.Overwrite,
            _ => throw new ArgumentException("--on-conflict 只支持 fail、rename 或 overwrite。")
        };

    private static long[] ParseFileSystemIds(string value)
    {
        var segments = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment =>
                !long.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0))
        {
            throw new ArgumentException("--fs-id 必须是以逗号分隔的正整数。");
        }

        return segments.Select(segment => long.Parse(segment, CultureInfo.InvariantCulture)).ToArray();
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"参数 {name} 缺少值。");
            }

            return args[index + 1];
        }

        return null;
    }

    private static IReadOnlyList<string> GetOptions(string[] args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"参数 {name} 缺少值。");
            }

            values.Add(args[++index]);
        }

        return values;
    }

    private static int GetIntOption(string[] args, string name, int defaultValue)
    {
        var value = GetOption(args, name);
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ArgumentException($"参数 {name} 必须是整数。");
    }

    private static string? FirstPositional(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            return args[index];
        }

        return null;
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            百度网盘命令行工具

            环境变量：
              BAIDU_CLIENT_ID      必填，应用 API Key
              BAIDU_CLIENT_SECRET  换取/刷新 Token 时必填，应用 Secret Key
              BAIDU_REDIRECT_URI   可选，默认 oob
              BAIDU_OAUTH_SCOPE    可选，默认 "basic netdisk"
              BAIDU_TOKEN_FILE     可选，Token 保存路径
              BAIDU_TOKEN_PROTECTION 可选，auto（默认）、dpapi 或 plain
              BAIDU_APP_ROOT       upload 和文件管理必填，例如 /apps/你的应用名

            命令：
              auth-url [--state <值>] [--force-login]
              login [--no-browser] [--force-login]
              exchange --code <授权码或回调地址> [--state <期望值>]
              refresh [--refresh-token <值>]
              show
              account
              quota
              ls [--dir <路径>] [--start <偏移>] [--limit <数量>]
                 [--order name|time|size] [--desc] [--folders-only]
              search --key <关键词> [--dir <路径>] [--page <页码>]
                     [--page-size <数量>] [--current-dir-only]
              meta --fs-id <ID[,ID...]>
              download --fs-id <ID> --output <本地文件路径> [--overwrite]
              upload --local <本地文件路径> --remote <应用目录内网盘路径>
                     [--on-conflict rename|rename-if-different|overwrite]
              mkdir --path <应用目录内路径> [--on-conflict fail|rename|overwrite]
              copy --source <路径> [--source <路径>...] --dest <目标目录>
                   [--new-name <新名称>] [--on-conflict fail|rename|overwrite]
              move --source <路径> [--source <路径>...] --dest <目标目录>
                   [--new-name <新名称>] [--on-conflict fail|rename|overwrite]
              rename --path <路径> --name <新名称>
              delete --path <路径> [--path <路径>...] --confirm
            """);
    }

    private sealed class ConsoleDownloadProgress : IProgress<BaiduDownloadProgress>
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastReportMilliseconds = -1000;
        private bool _hasWritten;

        public void Report(BaiduDownloadProgress value)
        {
            if (value.BytesReceived < value.TotalBytes &&
                _stopwatch.ElapsedMilliseconds - _lastReportMilliseconds < 250)
            {
                return;
            }

            _lastReportMilliseconds = _stopwatch.ElapsedMilliseconds;
            var percentage = value.Percentage is null ? "--.--%" : $"{value.Percentage:P2}";
            Console.Write(
                $"\r下载中: {FormatBytes(value.BytesReceived)} / {FormatBytes(value.TotalBytes)} ({percentage})");
            _hasWritten = true;
        }

        public void Complete()
        {
            if (!_hasWritten)
            {
                return;
            }

            Console.WriteLine();
            _hasWritten = false;
        }
    }

    private sealed class ConsoleUploadProgress : IProgress<BaiduUploadProgress>
    {
        private bool _hasWritten;

        public void Report(BaiduUploadProgress value)
        {
            var percentage = value.Percentage is null ? "--.--%" : $"{value.Percentage:P2}";
            Console.Write(
                $"\r上传中: {FormatBytes(value.BytesCompleted)} / {FormatBytes(value.TotalBytes)} " +
                $"({percentage})，分片 {value.PartsCompleted}/{value.TotalParts}");
            _hasWritten = true;
        }

        public void Complete()
        {
            if (!_hasWritten)
            {
                return;
            }

            Console.WriteLine();
            _hasWritten = false;
        }
    }
}
