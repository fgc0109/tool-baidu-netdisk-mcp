namespace BaiduNetdisk.Storage;

public static class BaiduTokenStoreFactory
{
    public static IBaiduTokenStore Create(
        string path,
        BaiduTokenProtectionMode protectionMode = BaiduTokenProtectionMode.Auto) =>
        protectionMode switch
        {
            BaiduTokenProtectionMode.Auto when OperatingSystem.IsWindows() =>
                new ProtectedFileTokenStore(path),
            BaiduTokenProtectionMode.Auto => new FileTokenStore(path),
            BaiduTokenProtectionMode.DpapiCurrentUser when OperatingSystem.IsWindows() =>
                new ProtectedFileTokenStore(path),
            BaiduTokenProtectionMode.DpapiCurrentUser =>
                throw new PlatformNotSupportedException("DPAPI Token 保护仅支持 Windows。"),
            BaiduTokenProtectionMode.PlainText => new FileTokenStore(path),
            _ => throw new ArgumentOutOfRangeException(nameof(protectionMode))
        };

    public static BaiduTokenProtectionMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return BaiduTokenProtectionMode.Auto;
        }

        if (value.Equals("dpapi", StringComparison.OrdinalIgnoreCase)
            || value.Equals("dpapi-current-user", StringComparison.OrdinalIgnoreCase))
        {
            return BaiduTokenProtectionMode.DpapiCurrentUser;
        }

        if (value.Equals("plain", StringComparison.OrdinalIgnoreCase)
            || value.Equals("plaintext", StringComparison.OrdinalIgnoreCase))
        {
            return BaiduTokenProtectionMode.PlainText;
        }

        throw new InvalidOperationException(
            "BAIDU_TOKEN_PROTECTION 只支持 auto、dpapi 或 plain。");
    }
}
