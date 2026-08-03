namespace BaiduNetdisk.OAuth;

public sealed record BaiduAuthenticatedSessionOptions
{
    public TimeSpan RefreshBeforeExpiry { get; init; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (RefreshBeforeExpiry < TimeSpan.Zero || RefreshBeforeExpiry > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("Token 提前刷新时间必须在 0～1 天之间。");
        }
    }
}
