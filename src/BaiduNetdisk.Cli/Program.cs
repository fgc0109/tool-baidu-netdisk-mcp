using System.Diagnostics;
using System.Security.Cryptography;
using BaiduNetdisk.OAuth;
using BaiduNetdisk.Storage;

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
            var tokenStore = new FileTokenStore(GetTokenPath());

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            var oauth = new BaiduOAuthClient(httpClient, options);

            return command switch
            {
                "auth-url" => PrintAuthorizationUrl(oauth, commandArgs),
                "login" => await LoginAsync(oauth, tokenStore, commandArgs),
                "exchange" => await ExchangeAsync(oauth, tokenStore, commandArgs),
                "refresh" => await RefreshAsync(oauth, tokenStore, commandArgs),
                "show" => await ShowAsync(tokenStore),
                _ => throw new ArgumentException($"未知命令：{command}")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or BaiduOAuthException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
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
        FileTokenStore tokenStore,
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
        FileTokenStore tokenStore,
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
        FileTokenStore tokenStore,
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

    private static async Task<int> ShowAsync(FileTokenStore tokenStore)
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
            百度网盘 OAuth 命令行工具

            环境变量：
              BAIDU_CLIENT_ID      必填，应用 API Key
              BAIDU_CLIENT_SECRET  换取/刷新 Token 时必填，应用 Secret Key
              BAIDU_REDIRECT_URI   可选，默认 oob
              BAIDU_OAUTH_SCOPE    可选，默认 "basic netdisk"
              BAIDU_TOKEN_FILE     可选，Token 保存路径

            命令：
              auth-url [--state <值>] [--force-login]
              login [--no-browser] [--force-login]
              exchange --code <授权码或回调地址> [--state <期望值>]
              refresh [--refresh-token <值>]
              show
            """);
    }
}
