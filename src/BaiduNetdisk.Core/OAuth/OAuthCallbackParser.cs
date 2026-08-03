namespace BaiduNetdisk.OAuth;

public static class OAuthCallbackParser
{
    public static string GetCode(string codeOrCallbackUri, string? expectedState = null)
    {
        if (string.IsNullOrWhiteSpace(codeOrCallbackUri))
        {
            throw new ArgumentException("授权码或回调地址不能为空。", nameof(codeOrCallbackUri));
        }

        var input = codeOrCallbackUri.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var callbackUri))
        {
            return input;
        }

        var query = ParseQuery(callbackUri.Query);
        if (query.TryGetValue("error", out var error))
        {
            query.TryGetValue("error_description", out var description);
            throw new BaiduOAuthException(error, description);
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("回调地址中没有 code 参数。");
        }

        if (expectedState is not null &&
            (!query.TryGetValue("state", out var actualState) ||
             !string.Equals(actualState, expectedState, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("OAuth state 不匹配，已拒绝交换 Token。请重新发起授权。");
        }

        return code;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var key = separator < 0 ? segment : segment[..separator];
            var value = separator < 0 ? string.Empty : segment[(separator + 1)..];
            result[Decode(key)] = Decode(value);
        }

        return result;
    }

    private static string Decode(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));
}
