using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class LeverageTierCache : ILeverageTierProvider
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(string Exchange, string Asset), (LeverageTier[] Tiers, DateTime FetchedAtUtc)> _cache = new();
    private readonly ILogger<LeverageTierCache> _logger;

    public LeverageTierCache(ILogger<LeverageTierCache> logger)
    {
        _logger = logger;
    }

    public Task<LeverageTier[]> GetTiersAsync(string exchangeName, string asset, CancellationToken ct = default)
    {
        var key = NormalizeKey(exchangeName, asset);
        if (_cache.TryGetValue(key, out var entry) && !IsExpired(entry.FetchedAtUtc))
            return Task.FromResult(entry.Tiers);

        return Task.FromResult(Array.Empty<LeverageTier>());
    }

    public int GetEffectiveMaxLeverage(string exchangeName, string asset, decimal notionalUsdc)
    {
        var key = NormalizeKey(exchangeName, asset);
        if (!_cache.TryGetValue(key, out var entry) || entry.Tiers.Length == 0)
            return int.MaxValue;

        var tier = FindTier(entry.Tiers, notionalUsdc);
        return tier?.MaxLeverage ?? int.MaxValue;
    }

    public decimal GetMaintenanceMarginRate(string exchangeName, string asset, decimal notionalUsdc)
    {
        var key = NormalizeKey(exchangeName, asset);
        if (!_cache.TryGetValue(key, out var entry) || entry.Tiers.Length == 0)
            return 0m;

        var tier = FindTier(entry.Tiers, notionalUsdc);
        return tier?.MaintMarginRate ?? 0m;
    }

    public void UpdateTiers(string exchangeName, string asset, LeverageTier[] tiers)
    {
        var key = NormalizeKey(exchangeName, asset);
        _cache[key] = (tiers, DateTime.UtcNow);
        _logger.LogDebug("Leverage tier cache populated for {Exchange}/{Asset}: {Count} tiers",
            exchangeName, asset, tiers.Length);
    }

    public bool IsStale(string exchangeName, string asset)
    {
        var key = NormalizeKey(exchangeName, asset);
        if (!_cache.TryGetValue(key, out var entry))
            return true;

        return IsExpired(entry.FetchedAtUtc);
    }

    private static (string Exchange, string Asset) NormalizeKey(string exchange, string asset)
        => (exchange.ToUpperInvariant(), asset.ToUpperInvariant());

    private static bool IsExpired(DateTime fetchedAtUtc)
        => DateTime.UtcNow - fetchedAtUtc > Ttl;

    private static LeverageTier? FindTier(LeverageTier[] tiers, decimal notionalUsdc)
        => Array.Find(tiers, t => notionalUsdc >= t.NotionalFloor && notionalUsdc < t.NotionalCap);
}
