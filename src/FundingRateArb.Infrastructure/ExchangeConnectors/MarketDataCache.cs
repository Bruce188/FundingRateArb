using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class MarketDataCache : IMarketDataCache
{
    private readonly ConcurrentDictionary<string, (FundingRateDto Rate, DateTime Timestamp)> _rates = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string exchangeName, string symbol) => $"{exchangeName}:{symbol}";

    public void Update(FundingRateDto rate)
    {
        var key = Key(rate.ExchangeName, rate.Symbol);

        // Preserve existing volume when the incoming update has 0 (e.g. WS stream without volume data)
        if (rate.Volume24hUsd == 0m && _rates.TryGetValue(key, out var existing) && existing.Rate.Volume24hUsd > 0m)
        {
            rate.Volume24hUsd = existing.Rate.Volume24hUsd; // N.B. mutates caller's dto
        }

        _rates[key] = (rate, DateTime.UtcNow);
    }

    public FundingRateDto? GetLatest(string exchangeName, string symbol)
    {
        var key = Key(exchangeName, symbol);
        return _rates.TryGetValue(key, out var entry) ? entry.Rate : null;
    }

    public List<FundingRateDto> GetAllLatest()
    {
        return _rates.Values.Select(e => e.Rate).ToList();
    }

    public List<FundingRateDto> GetAllForExchange(string exchangeName)
    {
        var prefix = exchangeName + ":";
        return _rates
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value.Rate)
            .ToList();
    }

    public decimal GetMarkPrice(string exchangeName, string symbol)
    {
        var key = Key(exchangeName, symbol);
        return _rates.TryGetValue(key, out var entry) ? entry.Rate.MarkPrice : 0m;
    }

    public DateTime? GetNextSettlement(string exchangeName, string symbol)
    {
        var key = Key(exchangeName, symbol);
        return _rates.TryGetValue(key, out var entry) ? entry.Rate.NextSettlementUtc : null;
    }

    public bool IsStale(string exchangeName, string symbol, TimeSpan maxAge)
    {
        var key = Key(exchangeName, symbol);
        if (!_rates.TryGetValue(key, out var entry))
        {
            return true;
        }

        return DateTime.UtcNow - entry.Timestamp > maxAge;
    }

    public DateTime? GetLastFetchTime()
    {
        var timestamps = _rates.Values.Select(e => (DateTime?)e.Timestamp);
        return timestamps.DefaultIfEmpty().Max();
    }

    public bool IsStaleForExchange(string exchangeName, TimeSpan maxAge)
    {
        var prefix = exchangeName + ":";
        var exchangeEntries = _rates
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exchangeEntries.Count == 0)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        return exchangeEntries.Any(kv => now - kv.Value.Timestamp > maxAge);
    }
}
