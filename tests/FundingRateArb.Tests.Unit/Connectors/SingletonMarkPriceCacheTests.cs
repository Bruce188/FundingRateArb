using FluentAssertions;
using FundingRateArb.Infrastructure.ExchangeConnectors;

namespace FundingRateArb.Tests.Unit.Connectors;

public class SingletonMarkPriceCacheTests : IDisposable
{
    private readonly SingletonMarkPriceCache _cache = new();

    [Fact]
    public async Task TwoCallersShareSameCachedPrice()
    {
        var fetchCount = 0;
        Task<Dictionary<string, decimal>> FetchFactory(CancellationToken ct)
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTC"] = 65_000m
            });
        }

        // First caller triggers the fetch
        var price1 = await _cache.GetOrRefreshAsync("Hyperliquid", "BTC", FetchFactory);
        // Second caller should get the cached value (no new fetch)
        var price2 = await _cache.GetOrRefreshAsync("Hyperliquid", "BTC", FetchFactory);

        price1.Should().Be(65_000m);
        price2.Should().Be(65_000m);
        fetchCount.Should().Be(1, "the second call should use the cached value");
    }

    [Fact]
    public async Task DifferentExchangesGetSeparateCaches()
    {
        Task<Dictionary<string, decimal>> HyperliquidFetch(CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTC"] = 65_000m
            });

        Task<Dictionary<string, decimal>> AsterFetch(CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTC"] = 65_100m
            });

        var hyperliquidPrice = await _cache.GetOrRefreshAsync("Hyperliquid", "BTC", HyperliquidFetch);
        var asterPrice = await _cache.GetOrRefreshAsync("AsterDex", "BTC", AsterFetch);

        hyperliquidPrice.Should().Be(65_000m);
        asterPrice.Should().Be(65_100m, "each exchange should have its own cache partition");
    }

    public void Dispose() => _cache.Dispose();
}
