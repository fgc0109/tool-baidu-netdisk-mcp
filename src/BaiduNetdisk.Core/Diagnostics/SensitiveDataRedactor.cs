using System.Text.RegularExpressions;

namespace BaiduNetdisk.Diagnostics;

public static partial class SensitiveDataRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = SecretFieldRegex().Replace(
            value,
            match => $"{match.Groups["name"].Value}{match.Groups["separator"].Value}***");
        return BearerTokenRegex().Replace(redacted, "${prefix}***");
    }

    [GeneratedRegex(
        "(?<name>access_token|refresh_token|client_secret|session_secret)(?<separator>\\s*(?:=|:)\\s*[\\\"']?)(?<value>[^&\\s\\\"',}\\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretFieldRegex();

    [GeneratedRegex(
        "(?<prefix>Authorization\\s*:\\s*Bearer\\s+)[^\\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();
}
