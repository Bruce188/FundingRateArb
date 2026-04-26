using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Common.Exchanges;

public interface IMarketDataCache
{
    void Update(FundingRateDto rate);
    FundingRateDto? GetLatest(string exchangeName, string symbol);
    List<FundingRateDto> GetAllLatest();
    List<FundingRateDto> GetAllForExchange(string exchangeName);
    decimal GetMarkPrice(string exchangeName, string symbol);

    /// <summary>
    /// Returns the cached next funding settlement time for the given exchange and symbol.
    /// Returns null if not available in cache.
    /// </summary>
    DateTime? GetNextSettlement(string exchangeName, string symbol);

    /// <summary>
    /// Returns the cached best-bid price for the given exchange and symbol.
    /// Returns null when no snapshot is cached (treated as "no data" — not an empty book).
    /// Returns 0 when a snapshot is cached and the book is empty on the bid side.
    /// </summary>
    decimal? GetBestBid(string exchangeName, string symbol) => null;

    /// <summary>
    /// Returns the cached best-ask price for the given exchange and symbol.
    /// Returns null when no snapshot is cached (treated as "no data" — not an empty book).
    /// Returns 0 when a snapshot is cached and the book is empty on the ask side.
    /// </summary>
    decimal? GetBestAsk(string exchangeName, string symbol) => null;

    bool IsStale(string exchangeName, string symbol, TimeSpan maxAge);
    bool IsStaleForExchange(string exchangeName, TimeSpan maxAge);

    /// <summary>
    /// Returns the most recent cache-update timestamp across all cached entries.
    /// Returns null when the cache is empty.
    /// </summary>
    DateTime? GetLastFetchTime();
}
