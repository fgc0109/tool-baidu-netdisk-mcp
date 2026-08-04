using BaiduNetdisk.OAuth;
using BaiduNetdisk.Storage;

namespace BaiduNetdisk.Mcp;

public sealed record BaiduMcpOptions
{
    public required BaiduOAuthOptions OAuth { get; init; }

    public required string TokenPath { get; init; }

    public BaiduTokenProtectionMode TokenProtection { get; init; }

    public string? AppRoot { get; init; }

    public IReadOnlyList<string> LocalRoots { get; init; } = [];

    public int MaximumItems { get; init; } = 100;

    public int MaximumResponseCharacters { get; init; } = 50_000;

    public static BaiduMcpOptions FromEnvironment()
    {
        var tokenPath = Environment.GetEnvironmentVariable("BAIDU_TOKEN_FILE");
        if (string.IsNullOrWhiteSpace(tokenPath))
        {
            tokenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BaiduNetdiskMcp",
                "tokens.json");
        }

        var roots = (Environment.GetEnvironmentVariable("BAIDU_LOCAL_ROOTS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        return new BaiduMcpOptions
        {
            OAuth = new BaiduOAuthOptions
            {
                ClientId = Environment.GetEnvironmentVariable("BAIDU_CLIENT_ID") ?? string.Empty,
                ClientSecret = Environment.GetEnvironmentVariable("BAIDU_CLIENT_SECRET"),
                RedirectUri = Environment.GetEnvironmentVariable("BAIDU_REDIRECT_URI")
                    ?? BaiduOAuthOptions.DefaultRedirectUri,
                Scope = Environment.GetEnvironmentVariable("BAIDU_OAUTH_SCOPE")
                    ?? BaiduOAuthOptions.DefaultScope
            },
            TokenPath = tokenPath,
            TokenProtection = BaiduTokenStoreFactory.ParseMode(
                Environment.GetEnvironmentVariable("BAIDU_TOKEN_PROTECTION")),
            AppRoot = Environment.GetEnvironmentVariable("BAIDU_APP_ROOT"),
            LocalRoots = roots,
            MaximumItems = ReadInteger("BAIDU_MCP_MAX_ITEMS", 100, 1, 100),
            MaximumResponseCharacters = ReadInteger(
                "BAIDU_MCP_MAX_RESPONSE_CHARS",
                50_000,
                1_000,
                1_000_000)
        };
    }

    public string RequireAppRoot() =>
        string.IsNullOrWhiteSpace(AppRoot)
            ? throw new InvalidOperationException(
                "写入工具需要 BAIDU_APP_ROOT，例如 /apps/你的应用名。")
            : AppRoot;

    internal static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static int ReadInteger(string name, int defaultValue, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException($"{name} 必须在 {minimum}～{maximum} 之间。");
        }

        return parsed;
    }
}
