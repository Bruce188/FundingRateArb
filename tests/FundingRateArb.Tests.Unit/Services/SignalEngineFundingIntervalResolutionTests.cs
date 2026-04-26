using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Moq;
using IAssetExchangeFundingIntervalRepository =
    FundingRateArb.Application.Interfaces.IAssetExchangeFundingIntervalRepository;

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Verifies SignalEngine resolves per-symbol funding intervals using the documented
/// fallback chain:
///  1. Per-symbol map hit (ExchangeId, AssetId) → stored interval hours
///  2. Per-symbol map miss → Exchange.FundingIntervalHours (exchange-level default)
///  3. Exchange == null AND no per-symbol entry → floor of 1
///
/// Uses a stub IAssetExchangeFundingIntervalRepository backed by a hand-built
/// IReadOnlyDictionary. No ILeverageTierProvider is supplied (consistent with the
/// pattern in SignalEngineTests) — CyclesPerYear must therefore be populated by a
/// code path that does NOT depend on tier data.
///
/// CyclesPerYear = (24 / cycleHours) × 365 is the observable proxy for cycleHours:
///   cycleHours = 4  → CyclesPerYear = 2190
///   cycleHours = 8  → CyclesPerYear = 1095
///   cycleHours = 1  → CyclesPerYear = 8760
/// </summary>
public class SignalEngineFundingIntervalResolutionTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRates = new();
    private readonly Mock<IMarketDataCache> _mockCache = new();

    // Stable exchange / asset IDs shared across all tests
    private const int AsterId = 1;
    private const int LighterId = 2;
    private const int BtcId = 1;
    private const int EthId = 2;

    public SignalEngineFundingIntervalResolutionTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRates.Object);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static FundingRateSnapshot MakeRate(
        int exchangeId, string exchangeName,
        int assetId, string symbol,
        decimal ratePerHour,
        int fundingIntervalHours = 1,
        decimal volume = 2_000_000m) =>
        new FundingRateSnapshot
        {
            ExchangeId = exchangeId,
            AssetId = assetId,
            RatePerHour = ratePerHour,
            MarkPrice = 50_000m,
            Volume24hUsd = volume,
            RecordedAt = DateTime.UtcNow,
            Exchange = new Exchange
            {
                Id = exchangeId,
                Name = exchangeName,
                FundingIntervalHours = fundingIntervalHours,
                ApiBaseUrl = "https://test.example",
                WsBaseUrl = "wss://test.example",
            },
            Asset = new Asset { Id = assetId, Symbol = symbol },
        };

    /// <summary>
    /// Stub that wraps a hand-built dictionary so SignalEngine can call GetIntervalsAsync.
    /// </summary>
    private static IAssetExchangeFundingIntervalRepository BuildIntervalRepo(
        IReadOnlyDictionary<(int ExchangeId, int AssetId), int> intervals)
    {
        var mock = new Mock<IAssetExchangeFundingIntervalRepository>();
        mock.Setup(r => r.GetIntervalsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(intervals);
        return mock.Object;
    }

    /// <summary>
    /// Relaxed BotConfiguration that lets any above-threshold spread become an opportunity.
    /// Trend filter disabled so no history mocking is required.
    /// </summary>
    private static BotConfiguration RelaxedConfig() => new BotConfiguration
    {
        SlippageBufferBps = 0,
        OpenThreshold = 0.0001m,
        FeeAmortizationHours = 24,
        MinEdgeMultiplier = 0.001m,         // effectively disables the edge-multiplier guard
        MinConsecutiveFavorableCycles = 0,  // disables trend filter
        UseBreakEvenSizeFilter = false,
    };

    // =========================================================================
    // Scenario 1 — Mixed-interval Aster
    //
    // per-symbol map = { (AsterId, BtcId) → 4 }; Exchange.FundingIntervalHours = 8.
    // BTC opportunity: per-symbol wins → cycleHours = max(4,1) = 4  → CyclesPerYear = 2190
    // ETH opportunity: no map entry → fallback 8h → cycleHours = max(8,1) = 8 → CyclesPerYear = 1095
    //
    // No ILeverageTierProvider is injected (following the pattern in SignalEngineTests).
    // CyclesPerYear must be populated by a tier-provider-independent code path.
    // =========================================================================
    [Fact]
    public async Task MixedIntervalAster_BtcUsesPerSymbolInterval4_EthUsesExchangeInterval8()
    {
        // Arrange
        var perSymbol = new Dictionary<(int ExchangeId, int AssetId), int>
        {
            { (AsterId, BtcId), 4 }   // BTC on Aster: per-symbol 4 h (overrides exchange-level 8 h)
        };
        var intervalRepo = BuildIntervalRepo(perSymbol);

        // Aster legs: FundingIntervalHours = 8 (exchange-level default)
        // Lighter legs: FundingIntervalHours = 1
        // Large spread ensures both pairs clear all opportunity filters
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(AsterId,   "Aster",   BtcId, "BTC", ratePerHour: 0.0001m, fundingIntervalHours: 8),
            MakeRate(LighterId, "Lighter", BtcId, "BTC", ratePerHour: 0.008m,  fundingIntervalHours: 1),
            MakeRate(AsterId,   "Aster",   EthId, "ETH", ratePerHour: 0.0001m, fundingIntervalHours: 8),
            MakeRate(LighterId, "Lighter", EthId, "ETH", ratePerHour: 0.008m,  fundingIntervalHours: 1),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(RelaxedConfig());
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // No ILeverageTierProvider — CyclesPerYear must be populated without tier data
        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            intervalRepo: intervalRepo);

        // Act
        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert: two opportunities produced
        result.Should().HaveCount(2,
            "both BTC and ETH pairs have spreads large enough to pass all filters");

        var btcOpp = result.Single(o => o.AssetSymbol == "BTC");
        var ethOpp = result.Single(o => o.AssetSymbol == "ETH");

        // BTC: per-symbol interval 4 h → cycleHours = max(4, lighter=1) = 4
        //      CyclesPerYear = (24 / 4) × 365 = 2190
        btcOpp.CyclesPerYear.Should().Be(2190,
            "BTC/Aster has a per-symbol interval of 4 h (overrides exchange-level 8 h); " +
            "cycleHours = max(4,1) = 4 → 2190 cycles/year; " +
            "this field must be populated without a tier provider");

        // ETH: not in per-symbol map → Exchange.FundingIntervalHours = 8 h
        //      cycleHours = max(8, lighter=1) = 8 → CyclesPerYear = (24 / 8) × 365 = 1095
        ethOpp.CyclesPerYear.Should().Be(1095,
            "ETH/Aster has no per-symbol entry; fallback FundingIntervalHours=8; " +
            "cycleHours = max(8,1) = 8 → 1095 cycles/year; " +
            "this field must be populated without a tier provider");
    }

    // =========================================================================
    // Scenario 2 — Empty map → exchange-level fallback only (regression guard)
    //
    // When the per-symbol map is empty every leg falls back to
    // Exchange.FundingIntervalHours. Aster FundingIntervalHours = 8, Lighter = 1.
    // cycleHours = max(8, 1) = 8 for every pair → CyclesPerYear = 1095.
    //
    // This is a regression guard: ensures that supplying an empty interval repo
    // does NOT silently override the exchange-level defaults with 1 (the
    // map-miss-floor), and that the fallback path returns the exchange value.
    // =========================================================================
    [Fact]
    public async Task EmptyPerSymbolMap_AllOpportunitiesUseFundingIntervalHoursFromExchange()
    {
        // Arrange
        var emptyMap = new Dictionary<(int ExchangeId, int AssetId), int>();
        var intervalRepo = BuildIntervalRepo(emptyMap);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(AsterId,   "Aster",   BtcId, "BTC", ratePerHour: 0.0001m, fundingIntervalHours: 8),
            MakeRate(LighterId, "Lighter", BtcId, "BTC", ratePerHour: 0.008m,  fundingIntervalHours: 1),
            MakeRate(AsterId,   "Aster",   EthId, "ETH", ratePerHour: 0.0001m, fundingIntervalHours: 8),
            MakeRate(LighterId, "Lighter", EthId, "ETH", ratePerHour: 0.008m,  fundingIntervalHours: 1),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(RelaxedConfig());
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // No ILeverageTierProvider — CyclesPerYear must be populated without tier data
        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            intervalRepo: intervalRepo);

        // Act
        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert: both opportunities resolved using Exchange.FundingIntervalHours = 8
        result.Should().HaveCount(2,
            "both BTC and ETH pairs should pass all filters");

        result.Should().AllSatisfy(opp =>
        {
            opp.CyclesPerYear.Should().Be(1095,
                $"{opp.AssetSymbol}: empty map → Aster FundingIntervalHours=8; " +
                "cycleHours=max(8,1)=8 → 1095 cycles/year; " +
                "must be populated without a tier provider");
        });
    }

    // =========================================================================
    // Scenario 3 — Null exchange + no per-symbol entry → floor of 1
    //
    // Rates whose Exchange property is null are filtered out by SignalEngine's
    // guard (`r.Exchange is not null`) before any pair evaluation. With an empty
    // per-symbol map and all rates having null Exchange, no rates survive the
    // filter, the maxIntervalHours computation uses DefaultIfEmpty(1) → 1,
    // and no opportunities are produced. The engine must not crash.
    // =========================================================================
    [Fact]
    public async Task NullExchangeRates_EmptyPerSymbolMap_NoOpportunities_EngineDoesNotCrash()
    {
        // Arrange
        var emptyMap = new Dictionary<(int ExchangeId, int AssetId), int>();
        var intervalRepo = BuildIntervalRepo(emptyMap);

        // Both rates have Exchange = null.
        // They are discarded at the `r.Exchange is not null` guard, so
        // TotalRatesLoaded == 0 and no pairs are formed.
        var rates = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 99,
                AssetId = BtcId,
                RatePerHour = 0.005m,
                MarkPrice = 50_000m,
                Volume24hUsd = 2_000_000m,
                RecordedAt = DateTime.UtcNow,
                Exchange = null!,   // null → filtered; interval would resolve to ?? 1
                Asset = new Asset { Id = BtcId, Symbol = "BTC" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 100,
                AssetId = BtcId,
                RatePerHour = 0.001m,
                MarkPrice = 50_000m,
                Volume24hUsd = 2_000_000m,
                RecordedAt = DateTime.UtcNow,
                Exchange = null!,   // null → filtered; interval would resolve to ?? 1
                Asset = new Asset { Id = BtcId, Symbol = "BTC" },
            },
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(RelaxedConfig());
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            intervalRepo: intervalRepo);

        // Act
        var diagnosticsResult = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: null-exchange rates are silently dropped → no opportunities generated.
        // maxIntervalHours uses DefaultIfEmpty(1) when rates is empty (the floor of 1).
        diagnosticsResult.Opportunities.Should().BeEmpty(
            "all rates had Exchange == null and were removed by the null-guard before pair evaluation");

        diagnosticsResult.Diagnostics.Should().NotBeNull(
            "diagnostics should always be populated even when no rates survive the filter");

        diagnosticsResult.Diagnostics!.TotalRatesLoaded.Should().Be(0,
            "null-exchange rates are excluded by the `r.Exchange is not null` filter; " +
            "this confirms maxIntervalHours falls back to DefaultIfEmpty(1)");
    }
}
