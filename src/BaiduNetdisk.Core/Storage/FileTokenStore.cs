using System.Text.Json;
using BaiduNetdisk.OAuth;

namespace BaiduNetdisk.Storage;

public sealed class FileTokenStore : IBaiduTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FileTokenStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Token 文件路径不能为空。", nameof(path));
        }

        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public async Task SaveAsync(BaiduTokenSet token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, token, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public async Task<BaiduTokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        await using var stream = File.OpenRead(Path);
        return await JsonSerializer.DeserializeAsync<BaiduTokenSet>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
