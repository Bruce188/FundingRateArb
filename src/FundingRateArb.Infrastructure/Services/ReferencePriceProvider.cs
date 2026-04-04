using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Interfaces;

namespace FundingRateArb.Infrastructure.Services;

public class ReferencePriceProvider : IReferencePriceProvider
{
    private readonly IMarketDataCache _marketDataCache;

    public ReferencePriceProvider(IMarketDataCache marketDataCache)
    {
        _marketDataCache = marketDataCache;
    }

    public decimal GetUnifiedPrice(string asset, string longExchange, string shortExchange)
    {
        var longData = _marketDataCache.GetLatest(longExchange, asset);
        var shortData = _marketDataCache.GetLatest(shortExchange, asset);

        if (longData is null && shortData is null)
        {
            return 0m;
        }

        var longIndex = longData?.IndexPrice ?? 0m;
        var shortIndex = shortData?.IndexPrice ?? 0m;
        var longMark = longData?.MarkPrice ?? 0m;
        var shortMark = shortData?.MarkPrice ?? 0m;

        // If either exchange is Binance, use its index price (leads price discovery)
        if (longExchange.Equals("Binance", StringComparison.OrdinalIgnoreCase) && longIndex > 0)
        {
            return longIndex;
        }

        if (shortExchange.Equals("Binance", StringComparison.OrdinalIgnoreCase) && shortIndex > 0)
        {
            return shortIndex;
        }

        // Both non-Binance (DEX): average both index prices
        if (longIndex > 0 && shortIndex > 0)
        {
            return (longIndex + shortIndex) / 2m;
        }

        // Fallback: average of both mark prices
        if (longMark > 0 && shortMark > 0)
        {
            return (longMark + shortMark) / 2m;
        }

        // Single side available
        if (longMark > 0)
        {
            return longMark;
        }

        if (shortMark > 0)
        {
            return shortMark;
        }

        return 0m;
    }
}
