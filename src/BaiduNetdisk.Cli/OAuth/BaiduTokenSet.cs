using System.Text.Json.Serialization;

namespace BaiduNetdisk.OAuth;

public sealed record BaiduTokenSet
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("expires_in")]
    public required int ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("session_key")]
    public string? SessionKey { get; init; }

    [JsonPropertyName("session_secret")]
    public string? SessionSecret { get; init; }

    [JsonPropertyName("acquired_at_utc")]
    public DateTimeOffset AcquiredAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public DateTimeOffset ExpiresAtUtc => AcquiredAtUtc.AddSeconds(ExpiresIn);

    [JsonIgnore]
    public bool IsExpiringSoon => ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5);
}
