namespace BaiduNetdisk.OAuth;

public sealed record BaiduOAuthOptions
{
    public const string DefaultRedirectUri = "oob";
    public const string DefaultScope = "basic netdisk";

    public required string ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string RedirectUri { get; init; } = DefaultRedirectUri;

    public string Scope { get; init; } = DefaultScope;

    public void Validate(bool requireSecret)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("缺少 BAIDU_CLIENT_ID（百度应用的 API Key）。");
        }

        if (requireSecret && string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException("缺少 BAIDU_CLIENT_SECRET（百度应用的 Secret Key）。");
        }

        if (string.IsNullOrWhiteSpace(RedirectUri))
        {
            throw new InvalidOperationException("BAIDU_REDIRECT_URI 不能为空。");
        }
    }
}
