using FluentAssertions;
using FundingRateArb.Domain.ValueObjects;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FundingRateArb.Tests.Unit.Services;

public class LeverageTierCacheTests
{
    private readonly LeverageTierCache _sut = new(NullLogger<LeverageTierCache>.Instance);

    private static readonly LeverageTier[] BinanceTiers =
    [
        new(0m, 50_000m, 50, 0.004m),
        new(50_000m, 250_000m, 25, 0.005m),
        new(250_000m, 1_000_000m, 10, 0.01m),
        new(1_000_000m, decimal.MaxValue, 5, 0.025m),
    ];

    // ── Effective leverage constrained by most restrictive tier ──────────────

    [Fact]
    public void GetEffectiveMaxLeverage_SmallNotional_ReturnsHighestTierLeverage()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        var result = _sut.GetEffectiveMaxLeverage("Binance", "ETH", 10_000m);

        result.Should().Be(50);
    }

    [Fact]
    public void GetEffectiveMaxLeverage_LargeNotional_ReturnsRestrictedLeverage()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        var result = _sut.GetEffectiveMaxLeverage("Binance", "ETH", 500_000m);

        result.Should().Be(10);
    }

    [Fact]
    public void GetEffectiveMaxLeverage_NoTiersCached_ReturnsIntMaxValue()
    {
        var result = _sut.GetEffectiveMaxLeverage("Unknown", "BTC", 10_000m);

        result.Should().Be(int.MaxValue);
    }

    // ── Maintenance margin rate lookup ───────────────────────────────────────

    [Fact]
    public void GetMaintenanceMarginRate_SmallNotional_ReturnsFirstTierRate()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        var result = _sut.GetMaintenanceMarginRate("Binance", "ETH", 10_000m);

        result.Should().Be(0.004m);
    }

    [Fact]
    public void GetMaintenanceMarginRate_LargeNotional_ReturnsHigherTierRate()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        var result = _sut.GetMaintenanceMarginRate("Binance", "ETH", 500_000m);

        result.Should().Be(0.01m);
    }

    [Fact]
    public void GetMaintenanceMarginRate_NoTiersCached_ReturnsZero()
    {
        var result = _sut.GetMaintenanceMarginRate("Unknown", "BTC", 10_000m);

        result.Should().Be(0m);
    }

    // ── Cache TTL / staleness ────────────────────────────────────────────────

    [Fact]
    public void IsStale_NeverPopulated_ReturnsTrue()
    {
        var result = _sut.IsStale("Binance", "ETH");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsStale_JustPopulated_ReturnsFalse()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        var result = _sut.IsStale("Binance", "ETH");

        result.Should().BeFalse();
    }

    // ── Case insensitivity ───────────────────────────────────────────────────

    [Fact]
    public void GetEffectiveMaxLeverage_CaseInsensitive_ReturnsSameResult()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        var lower = _sut.GetEffectiveMaxLeverage("binance", "eth", 10_000m);
        var upper = _sut.GetEffectiveMaxLeverage("BINANCE", "ETH", 10_000m);
        var mixed = _sut.GetEffectiveMaxLeverage("Binance", "Eth", 10_000m);

        lower.Should().Be(50);
        upper.Should().Be(50);
        mixed.Should().Be(50);
    }

    // ── GetTiersAsync returns cached data ────────────────────────────────────

    [Fact]
    public async Task GetTiersAsync_WhenPopulated_ReturnsTiers()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        var tiers = await _sut.GetTiersAsync("Binance", "ETH");

        tiers.Should().HaveCount(4);
        tiers[0].MaxLeverage.Should().Be(50);
    }

    [Fact]
    public async Task GetTiersAsync_WhenNotPopulated_ReturnsEmpty()
    {
        var tiers = await _sut.GetTiersAsync("Unknown", "BTC");

        tiers.Should().BeEmpty();
    }

    // ── UpdateTiers replaces previous data ───────────────────────────────────

    [Fact]
    public void UpdateTiers_OverwritesPreviousData()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        var newTiers = new LeverageTier[]
        {
            new(0m, decimal.MaxValue, 3, 0.05m),
        };
        _sut.UpdateTiers("Binance", "ETH", newTiers);

        var result = _sut.GetEffectiveMaxLeverage("Binance", "ETH", 10_000m);
        result.Should().Be(3);
    }

    // ── NB6: Exact boundary notional returns next tier leverage ─────────────

    [Fact]
    public void GetEffectiveMaxLeverage_ExactBoundaryNotional_ReturnsNextTierLeverage()
    {
        // Tier 1: [0, 50_000) = 50x, Tier 2: [50_000, 250_000) = 25x
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        // At exactly 50_000 — no longer in tier 1 (NotionalCap is exclusive), should be in tier 2
        var result = _sut.GetEffectiveMaxLeverage("Binance", "ETH", 50_000m);

        result.Should().Be(25, "notional at exact tier boundary should fall into the next tier");
    }

    // ── NB7: TTL expiration verified via reflection ─────────────────────────

    [Fact]
    public void IsStale_AfterTtlExpired_ReturnsTrue()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);
        _sut.IsStale("Binance", "ETH").Should().BeFalse("should not be stale immediately after update");

        BackdateCacheEntry(_sut, "Binance", "ETH", TimeSpan.FromHours(2));

        _sut.IsStale("Binance", "ETH").Should().BeTrue("should be stale after TTL expiration");
    }

    [Fact]
    public async Task GetTiersAsync_AfterTtlExpired_ReturnsEmpty()
    {
        _sut.UpdateTiers("Binance", "ETH", BinanceTiers);

        BackdateCacheEntry(_sut, "Binance", "ETH", TimeSpan.FromHours(2));

        var result = await _sut.GetTiersAsync("Binance", "ETH");
        result.Should().BeEmpty("expired entries should not be returned");
    }

    /// <summary>
    /// Uses reflection to back-date a cache entry's FetchedAtUtc by the given age,
    /// using the strongly-typed ConcurrentDictionary to avoid IDictionary boxing issues.
    /// </summary>
    private static void BackdateCacheEntry(LeverageTierCache cache, string exchange, string asset, TimeSpan age)
    {
        var cacheField = typeof(LeverageTierCache)
            .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = cacheField.GetValue(cache)!;

        // The dictionary type is ConcurrentDictionary<(string, string), (LeverageTier[], DateTime)>
        // We need to enumerate it and replace the value tuple with a back-dated one
        var dictType = dict.GetType();
        var enumerator = ((System.Collections.IEnumerable)dict).GetEnumerator();
        while (enumerator.MoveNext())
        {
            var kvp = enumerator.Current!;
            var kvpType = kvp.GetType();
            var key = kvpType.GetProperty("Key")!.GetValue(kvp)!;
            var value = kvpType.GetProperty("Value")!.GetValue(kvp)!;
            var tiersField = value.GetType().GetField("Item1")!;
            var tiers = (LeverageTier[])tiersField.GetValue(value)!;

            // Create a new value tuple with back-dated time
            var newValue = (tiers, DateTime.UtcNow - age);

            // Use TryUpdate via the indexer — access via dynamic to avoid generic type issues
            var indexerSetter = dictType.GetProperty("Item")!;
            indexerSetter.SetValue(dict, newValue, new[] { key });
            break;
        }
    }

    // ── NB1: Fail-safe when notional exceeds all tiers ──────────────────────

    [Fact]
    public void GetEffectiveMaxLeverage_NotionalExceedsAllTiers_ReturnsMostRestrictive()
    {
        // Use tiers where the last tier has a finite NotionalCap (gap exists)
        var tiersWithGap = new LeverageTier[]
        {
            new(0m, 50_000m, 50, 0.004m),
            new(50_000m, 100_000m, 25, 0.005m),
        };
        _sut.UpdateTiers("Binance", "ETH", tiersWithGap);

        // Query with notional beyond all tiers
        var result = _sut.GetEffectiveMaxLeverage("Binance", "ETH", 200_000m);

        result.Should().Be(25, "should fail-safe to most restrictive tier when notional exceeds all tiers");
    }
}
