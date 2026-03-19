namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Thread-safe mark price cache with thundering-herd prevention.
/// Shared by HyperliquidConnector and AsterConnector to eliminate code duplication.
/// Concurrent callers on an expired cache all await the same in-flight HTTP fetch via
/// <see cref="_pendingRefresh"/> rather than each issuing a separate request.
/// The lock is NOT held during HTTP I/O — it is acquired only to check/set _pendingRefresh
/// and to commit the new cache.
/// </summary>
public sealed class MarkPriceCacheHelper : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private Dictionary<string, decimal> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _cacheExpiry;
    private Task<Dictionary<string, decimal>>? _pendingRefresh;

    public async Task<decimal> GetOrRefreshAsync(
        string key,
        Func<CancellationToken, Task<Dictionary<string, decimal>>> fetchFactory,
        CancellationToken ct = default)
    {
        // 1. Fast path (no lock)
        var currentCache = _cache;
        if (DateTime.UtcNow < _cacheExpiry && currentCache.TryGetValue(key, out var fastPrice))
            return fastPrice;

        // 2. Acquire lock to check/start refresh
        Task<Dictionary<string, decimal>> refreshTask;
        await _cacheLock.WaitAsync(ct);
        try
        {
            // Re-check cache under lock
            if (DateTime.UtcNow < _cacheExpiry && _cache.TryGetValue(key, out var lockedPrice))
                return lockedPrice;

            // Share existing in-flight refresh or start new one
            if (_pendingRefresh is not null)
                refreshTask = _pendingRefresh;
            else
            {
                _pendingRefresh = fetchFactory(ct);
                refreshTask = _pendingRefresh;
            }
        }
        finally { _cacheLock.Release(); }

        // 3. Await shared task (outside lock)
        var newCache = await refreshTask;

        // 4. Commit under lock
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow >= _cacheExpiry)
            {
                _cache = newCache;
                _cacheExpiry = DateTime.UtcNow + CacheTtl;
            }
            _pendingRefresh = null;
        }
        finally { _cacheLock.Release(); }

        if (!newCache.TryGetValue(key, out var price))
            throw new KeyNotFoundException($"Asset '{key}' not found in mark price cache.");
        return price;
    }

    public void Dispose() => _cacheLock.Dispose();
}
