namespace BaiduNetdisk.Management;

public sealed record BaiduFileManagementOptions
{
    public required string AppRoot { get; init; }

    internal string GetValidatedAppRoot()
    {
        if (string.IsNullOrWhiteSpace(AppRoot))
        {
            throw new InvalidOperationException("文件管理前必须配置百度应用目录，例如 /apps/应用名。");
        }

        var root = AppRoot.TrimEnd('/');
        if (!root.StartsWith("/apps/", StringComparison.Ordinal) ||
            root.Length <= "/apps/".Length ||
            root.Contains("..", StringComparison.Ordinal) ||
            root.Contains('\\'))
        {
            throw new InvalidOperationException("百度应用目录必须采用 /apps/应用名 格式。");
        }

        return root;
    }
}
