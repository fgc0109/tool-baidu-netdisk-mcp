using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduNetdisk.OAuth;

namespace BaiduNetdisk.Storage;

[SupportedOSPlatform("windows")]
public sealed class ProtectedFileTokenStore : IBaiduTokenStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly byte[] AdditionalEntropy =
        Encoding.UTF8.GetBytes("BaiduNetdiskMcp.TokenStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ProtectedFileTokenStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Token 文件路径不能为空。", nameof(path));
        }

        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public async Task<BaiduTokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        var fileInfo = new FileInfo(Path);
        if (fileInfo.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Token 文件超过允许的大小。 ");
        }

        var storedBytes = await File.ReadAllBytesAsync(Path, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(storedBytes);
            if (!document.RootElement.TryGetProperty("protection", out _))
            {
                return await MigratePlainTextAsync(cancellationToken).ConfigureAwait(false);
            }

            var envelope = JsonSerializer.Deserialize<ProtectedTokenEnvelope>(storedBytes, JsonOptions)
                ?? throw new InvalidDataException("无法读取加密 Token 文件。 ");
            if (envelope.Version != 1
                || !string.Equals(envelope.Protection, "dpapi-current-user", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(envelope.Data))
            {
                throw new InvalidDataException("Token 文件的加密格式不受支持。 ");
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(envelope.Data);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Token 文件的加密数据无效。", exception);
            }

            var plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                AdditionalEntropy,
                DataProtectionScope.CurrentUser);
            try
            {
                var token = JsonSerializer.Deserialize<BaiduTokenSet>(plainBytes, JsonOptions)
                    ?? throw new InvalidDataException("解密后的 Token 数据为空。 ");
                ValidateToken(token);
                return token;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Token 文件不是有效的 JSON。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(storedBytes);
        }
    }

    public async Task SaveAsync(BaiduTokenSet token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        ValidateToken(token);

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                plainBytes,
                AdditionalEntropy,
                DataProtectionScope.CurrentUser);
            var envelope = new ProtectedTokenEnvelope(
                1,
                "dpapi-current-user",
                Convert.ToBase64String(protectedBytes));
            await SaveEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private async Task<BaiduTokenSet?> MigratePlainTextAsync(CancellationToken cancellationToken)
    {
        var token = await new FileTokenStore(Path).LoadAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return null;
        }

        ValidateToken(token);
        await SaveAsync(token, cancellationToken).ConfigureAwait(false);
        return token;
    }

    private async Task SaveEnvelopeAsync(
        ProtectedTokenEnvelope envelope,
        CancellationToken cancellationToken)
    {
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
                await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void ValidateToken(BaiduTokenSet token)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken)
            || string.IsNullOrWhiteSpace(token.RefreshToken)
            || token.ExpiresIn <= 0)
        {
            throw new InvalidDataException("Token 文件缺少必要字段。 ");
        }
    }

    private sealed record ProtectedTokenEnvelope(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("protection")] string Protection,
        [property: JsonPropertyName("data")] string Data);
}
