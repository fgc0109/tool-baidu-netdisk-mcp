using BaiduNetdisk.OAuth;

namespace BaiduNetdisk.Storage;

public interface IBaiduTokenStore
{
    string Path { get; }

    Task<BaiduTokenSet?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(BaiduTokenSet token, CancellationToken cancellationToken = default);
}
