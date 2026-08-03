using BaiduNetdisk.Api;
using BaiduNetdisk.Storage;

namespace BaiduNetdisk.OAuth;

public sealed class BaiduAuthenticatedSession : IDisposable
{
    private readonly BaiduOAuthClient _oauthClient;
    private readonly IBaiduTokenStore _tokenStore;
    private readonly BaiduAuthenticatedSessionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private BaiduTokenSet? _cachedToken;
    private bool _disposed;

    public BaiduAuthenticatedSession(
        BaiduOAuthClient oauthClient,
        IBaiduTokenStore tokenStore,
        BaiduAuthenticatedSessionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(oauthClient);
        ArgumentNullException.ThrowIfNull(tokenStore);
        _oauthClient = oauthClient;
        _tokenStore = tokenStore;
        _options = options ?? new BaiduAuthenticatedSessionOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BaiduTokenSet> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var token = Volatile.Read(ref _cachedToken);
        if (token is not null && !NeedsRefresh(token))
        {
            return token;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            token = _cachedToken ?? await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                throw ReauthorizationRequired("没有找到已保存的 Token，请先执行 login。");
            }

            _cachedToken = token;
            return NeedsRefresh(token)
                ? await RefreshAndSaveAsync(token, cancellationToken).ConfigureAwait(false)
                : token;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<T> ExecuteAsync<T>(
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(token.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (BaiduNetdiskApiException exception) when (IsAuthenticationFailure(exception))
        {
            var refreshed = await RefreshAfterRejectionAsync(
                token.AccessToken,
                cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(refreshed.AccessToken, cancellationToken).ConfigureAwait(false);
            }
            catch (BaiduNetdiskApiException retryException) when (IsAuthenticationFailure(retryException))
            {
                throw ReauthorizationRequired(
                    "百度仍拒绝刷新后的 Access Token，请重新执行 login 完成授权。",
                    retryException);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _refreshGate.Dispose();
        _disposed = true;
    }

    private async Task<BaiduTokenSet> RefreshAfterRejectionAsync(
        string rejectedAccessToken,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = _cachedToken ?? await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                throw ReauthorizationRequired("没有可刷新的 Token，请重新执行 login 完成授权。");
            }

            if (!string.Equals(token.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
            {
                return token;
            }

            return await RefreshAndSaveAsync(token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<BaiduTokenSet> RefreshAndSaveAsync(
        BaiduTokenSet token,
        CancellationToken cancellationToken)
    {
        BaiduTokenSet refreshed;
        try
        {
            refreshed = await _oauthClient.RefreshTokenAsync(token.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BaiduOAuthException exception) when (RequiresReauthorization(exception.Error))
        {
            throw ReauthorizationRequired(
                "Refresh Token 已失效或应用凭据不匹配，请重新执行 login 完成授权。",
                exception);
        }

        await _tokenStore.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _cachedToken, refreshed);
        return refreshed;
    }

    private bool NeedsRefresh(BaiduTokenSet token) =>
        token.ExpiresAtUtc <= _timeProvider.GetUtcNow().Add(_options.RefreshBeforeExpiry);

    private static bool IsAuthenticationFailure(BaiduNetdiskApiException exception) =>
        exception.ErrorCode is -6 or 110 or 111;

    private static bool RequiresReauthorization(string error) => error is
        "invalid_request" or
        "invalid_client" or
        "invalid_grant" or
        "unauthorized_client" or
        "invalid_scope" or
        "expired_token";

    private static BaiduReauthorizationRequiredException ReauthorizationRequired(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);
}
