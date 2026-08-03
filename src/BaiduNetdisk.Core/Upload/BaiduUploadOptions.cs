namespace BaiduNetdisk.Upload;

public sealed record BaiduUploadOptions
{
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    public required string AppRoot { get; init; }

    public int ChunkSize { get; init; } = DefaultChunkSize;

    public int MaxChunkAttempts { get; init; } = 3;

    internal string GetValidatedAppRoot()
    {
        if (string.IsNullOrWhiteSpace(AppRoot))
        {
            throw new InvalidOperationException("上传前必须配置百度应用目录，例如 /apps/应用名。");
        }

        var root = AppRoot.TrimEnd('/');
        if (!root.StartsWith("/apps/", StringComparison.Ordinal) ||
            root.Length <= "/apps/".Length ||
            root.Contains("..", StringComparison.Ordinal) ||
            root.Contains('\\'))
        {
            throw new InvalidOperationException("百度应用目录必须采用 /apps/应用名 格式。");
        }

        if (ChunkSize != DefaultChunkSize)
        {
            throw new InvalidOperationException("当前百度开放平台上传分片必须固定为 4 MiB。");
        }

        if (MaxChunkAttempts is < 1 or > 10)
        {
            throw new InvalidOperationException("分片最大尝试次数必须在 1～10 之间。");
        }

        return root;
    }
}
