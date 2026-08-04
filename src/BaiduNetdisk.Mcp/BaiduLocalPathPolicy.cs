namespace BaiduNetdisk.Mcp;

public sealed class BaiduLocalPathPolicy(BaiduMcpOptions options)
{
    private readonly IReadOnlyList<string> _roots = options.LocalRoots
        .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
        .ToArray();

    public string ValidateUploadSource(string path)
    {
        var fullPath = ValidatePath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("本地上传文件不存在。", fullPath);
        }

        return fullPath;
    }

    public string ValidateDownloadDestination(string path)
    {
        var fullPath = ValidatePath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("下载目标目录不存在。");
        }

        return fullPath;
    }

    private string ValidatePath(string path)
    {
        if (_roots.Count == 0)
        {
            throw new InvalidOperationException(
                "本地文件工具需要 BAIDU_LOCAL_ROOTS，多个根目录用系统路径分隔符隔开。");
        }

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("本地文件路径必须是绝对路径。", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var matchingRoot = _roots.FirstOrDefault(root => IsWithinRoot(fullPath, root));
        if (matchingRoot is null)
        {
            throw new UnauthorizedAccessException("本地文件路径不在 BAIDU_LOCAL_ROOTS 允许范围内。");
        }

        EnsureLinksRemainWithinRoot(fullPath, matchingRoot);

        return fullPath;
    }

    private static void EnsureLinksRemainWithinRoot(string path, string configuredRoot)
    {
        var rootInfo = new DirectoryInfo(configuredRoot);
        if (!rootInfo.Exists)
        {
            throw new DirectoryNotFoundException("BAIDU_LOCAL_ROOTS 中配置的目录不存在。");
        }

        var canonicalRoot = rootInfo.LinkTarget is null
            ? rootInfo.FullName
            : rootInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? rootInfo.FullName;
        var relative = Path.GetRelativePath(configuredRoot, path);
        var current = canonicalRoot;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info?.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }

            if (!IsWithinRoot(current, canonicalRoot))
            {
                throw new UnauthorizedAccessException(
                    "本地文件路径通过符号链接越出了 BAIDU_LOCAL_ROOTS。");
            }
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathFullyQualified(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
