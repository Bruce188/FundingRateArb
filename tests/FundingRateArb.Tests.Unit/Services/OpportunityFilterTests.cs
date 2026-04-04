using FluentAssertions;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FundingRateArb.Tests.Unit.Services;

public class OpportunityFilterTests
{
    private readonly CircuitBreakerManager _circuitBreaker;
    private readonly OpportunityFilter _sut;

    public OpportunityFilterTests()
    {
        _circuitBreaker = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        _sut = new OpportunityFilter(_circuitBreaker, NullLogger<OpportunityFilter>.Instance);
    }

    private static ArbitrageOpportunityDto MakeOpp(
        int assetId = 1, int longExId = 1, int shortExId = 2,
        decimal netYield = 0.001m, string symbol = "ETH") => new()
        {
            AssetId = assetId,
            AssetSymbol = symbol,
            LongExchangeId = longExId,
            ShortExchangeId = shortExId,
            LongExchangeName = $"Exchange{longExId}",
            ShortExchangeName = $"Exchange{shortExId}",
            NetYieldPerHour = netYield,
            SpreadPerHour = netYield,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        };

    private static UserConfiguration MakeUserConfig(decimal threshold = 0.0001m) => new()
        {
            UserId = "test-user",
            IsEnabled = true,
            MaxConcurrentPositions = 5,
            OpenThreshold = threshold,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
        };

    // ── FilterUserOpportunities ──────────────────────────────────────────────

    [Fact]
    public void FilterUserOpportunities_ExcludesDisabledExchanges()
    {
        var opp = MakeOpp(longExId: 1, shortExId: 2);
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterUserOpportunities(
            [opp],
            enabledExchangeSet: new HashSet<int> { 1 }, // exchange 2 NOT enabled
            dataOnlyExchangeIds: new HashSet<int>(),
            circuitBrokenExchangeIds: new HashSet<int>(),
            enabledAssetSet: new HashSet<int> { 1 },
            MakeUserConfig(),
            tracker);

        result.Should().BeEmpty();
        tracker.ExchangeDisabledKeys.Should().HaveCount(1);
    }

    [Fact]
    public void FilterUserOpportunities_ExcludesDataOnlyExchanges()
    {
        var opp = MakeOpp(longExId: 1, shortExId: 2);
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterUserOpportunities(
            [opp],
            enabledExchangeSet: new HashSet<int> { 1, 2 },
            dataOnlyExchangeIds: new HashSet<int> { 2 },
            circuitBrokenExchangeIds: new HashSet<int>(),
            enabledAssetSet: new HashSet<int> { 1 },
            MakeUserConfig(),
            tracker);

        result.Should().BeEmpty();
        tracker.ExchangeDisabledKeys.Should().HaveCount(1);
    }

    [Fact]
    public void FilterUserOpportunities_ExcludesCircuitBrokenExchanges()
    {
        var opp = MakeOpp(longExId: 1, shortExId: 2);
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterUserOpportunities(
            [opp],
            enabledExchangeSet: new HashSet<int> { 1, 2 },
            dataOnlyExchangeIds: new HashSet<int>(),
            circuitBrokenExchangeIds: new HashSet<int> { 2 },
            enabledAssetSet: new HashSet<int> { 1 },
            MakeUserConfig(),
            tracker);

        result.Should().BeEmpty();
        tracker.CircuitBrokenKeys.Should().HaveCount(1);
    }

    [Fact]
    public void FilterUserOpportunities_ExcludesDisabledAssets()
    {
        var opp = MakeOpp(assetId: 5);
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterUserOpportunities(
            [opp],
            enabledExchangeSet: new HashSet<int> { 1, 2 },
            dataOnlyExchangeIds: new HashSet<int>(),
            circuitBrokenExchangeIds: new HashSet<int>(),
            enabledAssetSet: new HashSet<int> { 1, 2, 3 }, // asset 5 NOT enabled
            MakeUserConfig(),
            tracker);

        result.Should().BeEmpty();
        tracker.AssetDisabledKeys.Should().HaveCount(1);
    }

    [Fact]
    public void FilterUserOpportunities_ExcludesBelowThreshold()
    {
        var opp = MakeOpp(netYield: 0.00001m); // below threshold of 0.0001
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterUserOpportunities(
            [opp],
            enabledExchangeSet: new HashSet<int> { 1, 2 },
            dataOnlyExchangeIds: new HashSet<int>(),
            circuitBrokenExchangeIds: new HashSet<int>(),
            enabledAssetSet: new HashSet<int> { 1 },
            MakeUserConfig(threshold: 0.0001m),
            tracker);

        result.Should().BeEmpty();
        tracker.BelowThresholdCount.Should().Be(1);
        tracker.BestBelowThresholdYield.Should().Be(0.00001m);
    }

    [Fact]
    public void FilterUserOpportunities_TracksSkipReasonsCorrectly()
    {
        var oppDisabledExchange = MakeOpp(assetId: 1, longExId: 99, shortExId: 2);
        var oppCircuitBroken = MakeOpp(assetId: 2, longExId: 1, shortExId: 3);
        var oppDisabledAsset = MakeOpp(assetId: 99, longExId: 1, shortExId: 2);
        var oppAboveThreshold = MakeOpp(assetId: 1, longExId: 1, shortExId: 2, netYield: 0.01m);

        var tracker = new SkipReasonTracker();

        var result = _sut.FilterUserOpportunities(
            [oppDisabledExchange, oppCircuitBroken, oppDisabledAsset, oppAboveThreshold],
            enabledExchangeSet: new HashSet<int> { 1, 2, 3 }, // include exchange 3 so CB filter catches it
            dataOnlyExchangeIds: new HashSet<int>(),
            circuitBrokenExchangeIds: new HashSet<int> { 3 },
            enabledAssetSet: new HashSet<int> { 1, 2, 3 },
            MakeUserConfig(),
            tracker);

        result.Should().HaveCount(1);
        tracker.ExchangeDisabledKeys.Should().HaveCount(1); // exchange 99 not in enabled set
        tracker.CircuitBrokenKeys.Should().HaveCount(1); // exchange 3 is circuit-broken
        tracker.AssetDisabledKeys.Should().HaveCount(1); // asset 99 not in enabled set
    }

    // ── FilterCandidates ─────────────────────────────────────────────────────

    [Fact]
    public void FilterCandidates_ExcludesActivePositions()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        var activeKeys = new HashSet<string> { "1_1_2" };
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterCandidates([opp], activeKeys, "user1", tracker, out var cooldownSkips);

        result.Should().BeEmpty();
        cooldownSkips.Should().BeEmpty();
    }

    [Fact]
    public void FilterCandidates_ExcludesCooledDownOpportunities()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        _circuitBreaker.SetCooldown("user1:1_1_2", DateTime.UtcNow.AddMinutes(10), 1);
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterCandidates([opp], new HashSet<string>(), "user1", tracker, out var cooldownSkips);

        result.Should().BeEmpty();
        cooldownSkips.Should().HaveCount(1);
        cooldownSkips[0].Asset.Should().Be("ETH");
        tracker.CooldownKeys.Should().Contain("1_1_2");
    }

    [Fact]
    public void FilterCandidates_ExcludesAssetExchangeCooledDown()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        _circuitBreaker.AssetExchangeCooldowns[(1, 1)] = (3, DateTime.UtcNow.AddMinutes(10));
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterCandidates([opp], new HashSet<string>(), "user1", tracker, out _);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterCandidates_ExcludesAssetExchangeCooledDown_ShortExchange()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        _circuitBreaker.AssetExchangeCooldowns[(1, 2)] = (3, DateTime.UtcNow.AddMinutes(10)); // cooldown on SHORT exchange
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterCandidates([opp], new HashSet<string>(), "user1", tracker, out _);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterCandidates_PassesNonCooledDown()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        var tracker = new SkipReasonTracker();

        var result = _sut.FilterCandidates([opp], new HashSet<string>(), "user1", tracker, out var cooldownSkips);

        result.Should().HaveCount(1);
        cooldownSkips.Should().BeEmpty();
    }

    // ── FindAdaptiveCandidates ───────────────────────────────────────────────

    [Fact]
    public void FindAdaptiveCandidates_ReturnsBestNetPositive()
    {
        var opp1 = MakeOpp(assetId: 1, longExId: 1, shortExId: 2, netYield: 0.0001m);
        var opp2 = MakeOpp(assetId: 2, longExId: 1, shortExId: 2, netYield: 0.0005m, symbol: "BTC");
        var tracker = new SkipReasonTracker();

        var result = _sut.FindAdaptiveCandidates(
            [opp1, opp2],
            new HashSet<int> { 1, 2 },
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int> { 1, 2 },
            new HashSet<string>(),
            "user1",
            tracker);

        result.Should().HaveCount(1);
        result[0].AssetSymbol.Should().Be("BTC");
    }

    [Fact]
    public void FindAdaptiveCandidates_AppliesSameFilters()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        _circuitBreaker.SetCooldown("user1:1_1_2", DateTime.UtcNow.AddMinutes(10), 1);
        var tracker = new SkipReasonTracker();

        var result = _sut.FindAdaptiveCandidates(
            [opp],
            new HashSet<int> { 1, 2 },
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int> { 1 },
            new HashSet<string>(),
            "user1",
            tracker);

        result.Should().BeEmpty();
        tracker.CooldownKeys.Should().Contain("1_1_2");
    }

    [Fact]
    public void FindAdaptiveCandidates_ExcludesDataOnlyExchanges()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        var tracker = new SkipReasonTracker();

        var result = _sut.FindAdaptiveCandidates(
            [opp],
            new HashSet<int> { 1, 2 },
            new HashSet<int> { 2 }, // short exchange is data-only
            new HashSet<int>(),
            new HashSet<int> { 1 },
            new HashSet<string>(),
            "user1",
            tracker);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindAdaptiveCandidates_ExcludesActiveKeys()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        var tracker = new SkipReasonTracker();

        var result = _sut.FindAdaptiveCandidates(
            [opp],
            new HashSet<int> { 1, 2 },
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int> { 1 },
            new HashSet<string> { "1_1_2" }, // active key matches opportunity
            "user1",
            tracker);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindAdaptiveCandidates_ExcludesCircuitBrokenExchanges()
    {
        var opp = MakeOpp(assetId: 1, longExId: 1, shortExId: 2);
        var tracker = new SkipReasonTracker();

        var result = _sut.FindAdaptiveCandidates(
            [opp],
            new HashSet<int> { 1, 2 },
            new HashSet<int>(),
            new HashSet<int> { 2 }, // short exchange is circuit-broken
            new HashSet<int> { 1 },
            new HashSet<string>(),
            "user1",
            tracker);

        result.Should().BeEmpty();
    }
}
