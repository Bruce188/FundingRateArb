using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Singleton implementation of IMarkPriceCache. Each exchange gets its own
/// MarkPriceCacheHelper instance, preserving per-exchange TTL and thundering-herd prevention.
/// Survives across DI scopes so scoped connectors share cached mark prices.
/// </summary>
public sealed class SingletonMarkPriceCache : IMarkPriceCache, IDisposable
{
    private readonly ConcurrentDictionary<string, MarkPriceCacheHelper> _caches = new(StringComparer.OrdinalIgnoreCase);

    public Task<decimal> GetOrRefreshAsync(
        string exchangeName,
        string asset,
        Func<CancellationToken, Task<Dictionary<string, decimal>>> fetchFactory,
        CancellationToken ct = default)
    {
        var helper = _caches.GetOrAdd(exchangeName, _ => new MarkPriceCacheHelper());
        return helper.GetOrRefreshAsync(asset, fetchFactory, ct);
    }

    public void Dispose()
    {
        foreach (var helper in _caches.Values)
        {
            helper.Dispose();
        }
        _caches.Clear();
    }
}
