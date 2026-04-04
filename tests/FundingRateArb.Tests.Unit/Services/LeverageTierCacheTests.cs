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
}
