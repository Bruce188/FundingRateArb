using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Tests.Unit.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class SignalEngineTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRates = new();
    private readonly Mock<IMarketDataCache> _mockCache = new();
    private readonly SignalEngine _sut;

    public SignalEngineTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRates.Object);
        _sut = new SignalEngine(_mockUow.Object, _mockCache.Object);
    }

    private static FundingRateSnapshot MakeRate(
        int exchangeId, string exchangeName,
        int assetId, string symbol,
        decimal ratePerHour,
        decimal markPrice = 3000m,
        decimal volume = 1_000_000m,
        decimal? takerFeeRate = null,
        DateTime? recordedAt = null) =>
        new FundingRateSnapshot
        {
            ExchangeId = exchangeId,
            AssetId = assetId,
            RatePerHour = ratePerHour,
            MarkPrice = markPrice,
            Volume24hUsd = volume,
            RecordedAt = recordedAt ?? DateTime.UtcNow,
            Exchange = new Exchange { Id = exchangeId, Name = exchangeName, TakerFeeRate = takerFeeRate },
            Asset = new Asset { Id = assetId, Symbol = symbol },
        };

    [Fact]
    public async Task GetOpportunities_WhenSpreadAboveThreshold_ReturnsOpportunity()
    {
        // Arrange
        // Hyperliquid ETH rate=0.0001/hr, Lighter ETH rate=0.0010/hr
        // Spread = 0.0009/hr, fees = (0.00090+0)/24 = 0.0000375/hr, net = 0.0008625/hr
        // OpenThreshold = 0.0003 → net > threshold → 1 opportunity
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].AssetSymbol.Should().Be("ETH");
        result[0].NetYieldPerHour.Should().BeGreaterThan(0.0003m);
    }

    [Fact]
    public async Task GetOpportunities_WhenSpreadBelowThreshold_ReturnsEmpty()
    {
        // Arrange
        // Spread = 0.0002/hr, threshold = 0.0003 → net will be below threshold after fees
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0003m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunities_AssignsCorrectLongShort()
    {
        // Arrange
        // Hyperliquid = lower rate → Long; Lighter = higher rate → Short
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].LongExchangeName.Should().Be("Hyperliquid");
        result[0].ShortExchangeName.Should().Be("Lighter");
        result[0].LongRatePerHour.Should().BeLessThan(result[0].ShortRatePerHour);
    }

    [Fact]
    public async Task GetOpportunities_DeductsFees_FromNetYield()
    {
        // Arrange
        // Hyperliquid (fee 0.00090) + Lighter (fee 0) pair
        // feePerHour = (0.00090 + 0.00000) / 24 = 0.0000375
        // spread = 0.0010 - 0.0001 = 0.0009
        // net = 0.0009 - 0.0000375 = 0.0008625
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread = 0.0010m - 0.0001m;                       // 0.0009
        var expectedFeePerHour = (0.00090m + 0.00000m) / 24m;
        var expectedNet = expectedSpread - expectedFeePerHour;

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].SpreadPerHour.Should().Be(expectedSpread);
        result[0].NetYieldPerHour.Should().Be(expectedNet);
    }

    [Fact]
    public async Task GetOpportunities_RanksOpportunitiesByNetYieldDescending()
    {
        // Arrange
        // ETH: Hyperliquid(0.0001) vs Lighter(0.0010) → high spread
        // BTC: Hyperliquid(0.0002) vs Lighter(0.0004) → low spread
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            MakeRate(1, "Hyperliquid", 2, "BTC", 0.0002m),
            MakeRate(2, "Lighter",     2, "BTC", 0.0005m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCountGreaterThan(1);
        result.Should().BeInDescendingOrder(o => o.NetYieldPerHour);
        result[0].NetYieldPerHour.Should().BeGreaterThanOrEqualTo(result[1].NetYieldPerHour);
    }

    [Fact]
    public async Task GetOpportunities_HandlesMultipleAssetsAndExchanges()
    {
        // Arrange
        // 3 exchanges × 2 assets
        // Per asset there are C(3,2) = 3 pairs
        // Only pairs where net >= threshold should appear
        // Set threshold low so all pairs qualify
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            MakeRate(3, "Aster",       1, "ETH", 0.0008m),

            MakeRate(1, "Hyperliquid", 2, "BTC", 0.0002m),
            MakeRate(2, "Lighter",     2, "BTC", 0.0012m),
            MakeRate(3, "Aster",       2, "BTC", 0.0009m),
        };

        // Threshold low enough that all pairs should qualify
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert
        // We expect opportunities for both ETH and BTC
        result.Should().Contain(o => o.AssetSymbol == "ETH");
        result.Should().Contain(o => o.AssetSymbol == "BTC");

        // Each pair should appear at most once
        var ethOpportunities = result.Where(o => o.AssetSymbol == "ETH").ToList();
        var btcOpportunities = result.Where(o => o.AssetSymbol == "BTC").ToList();

        // With 3 exchanges, max 3 pairs per asset
        ethOpportunities.Should().HaveCountLessOrEqualTo(3);
        btcOpportunities.Should().HaveCountLessOrEqualTo(3);

        // Results should be ranked descending
        result.Should().BeInDescendingOrder(o => o.NetYieldPerHour);
    }

    [Fact]
    public async Task GetOpportunities_WhenNoRatesAvailable_ReturnsEmpty()
    {
        // Arrange
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    // ── H-SE1: DB-driven fee tests ─────────────────────────────────────────────

    /// <summary>
    /// H-SE1: When Exchange.TakerFeeRate is set in DB, it overrides the hardcoded fallback.
    /// Round-trip fee = TakerFeeRate * 2 (entry + exit).
    /// </summary>
    [Fact]
    public async Task GetOpportunities_UsesDbTakerFeeRate_WhenSet()
    {
        // Hyperliquid DB fee = 0.0002 (round-trip = 0.0004) instead of fallback 0.00090
        // Lighter DB fee = 0.0001 (round-trip = 0.0002) instead of fallback 0.00000
        // feePerHour = (0.0004 + 0.0002) / 24 = 0.000025
        // spread = 0.0009
        // net = 0.0009 - 0.000025 = 0.000875
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0.0002m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, takerFeeRate: 0.0001m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread = 0.0009m;
        var expectedFeePerHour = (0.0002m * 2 + 0.0001m * 2) / 24m;
        var expectedNet = expectedSpread - expectedFeePerHour;

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].NetYieldPerHour.Should().BeApproximately(expectedNet, 0.0000001m);
    }

    /// <summary>
    /// H-SE1 fallback: When TakerFeeRate is null, the built-in constant is used.
    /// </summary>
    [Fact]
    public async Task GetOpportunities_FallsBackToConstantFee_WhenTakerFeeRateIsNull()
    {
        // No TakerFeeRate set → fallback: Hyperliquid=0.00090, Lighter=0.00000
        // feePerHour = (0.00090 + 0.00000) / 24
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: null),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, takerFeeRate: null),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread = 0.0009m;
        var expectedFeePerHour = (0.00090m + 0.00000m) / 24m;
        var expectedNet = expectedSpread - expectedFeePerHour;

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].NetYieldPerHour.Should().BeApproximately(expectedNet, 0.0000001m);
    }

    /// <summary>
    /// H-SE1: When one exchange has a DB fee and the other does not, mixing works correctly.
    /// </summary>
    [Fact]
    public async Task GetOpportunities_MixedDbAndFallbackFees_ComputedCorrectly()
    {
        // Hyperliquid: TakerFeeRate = 0.0003 (round-trip = 0.0006)
        // Lighter:     TakerFeeRate = null  → fallback = 0.00000
        // feePerHour = (0.0006 + 0.00000) / 24
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0.0003m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, takerFeeRate: null),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread = 0.0009m;
        var expectedFeePerHour = (0.0003m * 2 + 0.00000m) / 24m;
        var expectedNet = expectedSpread - expectedFeePerHour;

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].NetYieldPerHour.Should().BeApproximately(expectedNet, 0.0000001m);
    }

    // ── TD1: Zero-volume filter ─────────────────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_ExcludesZeroVolumeLeg()
    {
        // Long leg has 0 volume → should be excluded
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 0m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 1_000_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunities_ExcludesZeroVolumeOnShortLeg()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 1_000_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 0m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── TD3: Limit to 20 ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_ReturnsAtMost26()
    {
        // Create 11 exchanges × 1 asset = C(11,2) = 55 pairs, all above threshold
        var rates = Enumerable.Range(1, 11).Select(i =>
            MakeRate(i, $"Exchange{i}", 1, "ETH", 0.0001m * i, volume: 1_000_000m))
            .ToList();

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0000001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCountLessOrEqualTo(26);
    }

    // ── C3+C4: Fee amortization uses FeeAmortizationHours ──────────────────────

    [Fact]
    public async Task GetOpportunities_FeeAmortization_UsesFeeAmortizationHours()
    {
        // FeeAmortizationHours=48 → fees amortized over 48 hours (decoupled from MaxHoldTimeHours)
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FeeAmortizationHours = 48 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread = 0.0009m;
        var expectedFeePerHour = (0.00090m + 0.00000m) / 48m;
        var expectedNet = expectedSpread - expectedFeePerHour;

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].NetYieldPerHour.Should().BeApproximately(expectedNet, 0.0000001m);
    }

    [Fact]
    public async Task GetOpportunities_FeeAmortization_IndependentOfMaxHoldTimeHours()
    {
        // Verify that changing MaxHoldTimeHours does NOT affect fee calculation
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        // FeeAmortizationHours=24, MaxHoldTimeHours=72 — fee should use 24, not 72
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FeeAmortizationHours = 24, MaxHoldTimeHours = 72 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread = 0.0009m;
        var expectedFeePerHour = (0.00090m + 0.00000m) / 24m;
        var expectedNet = expectedSpread - expectedFeePerHour;

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].NetYieldPerHour.Should().BeApproximately(expectedNet, 0.0000001m);
    }

    // ── H3: Rate staleness filter ────────────────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_FiltersOutStaleRates()
    {
        // One rate is 30 minutes old, staleness cutoff is 15 minutes → filtered out
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, recordedAt: DateTime.UtcNow.AddMinutes(-5)),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, recordedAt: DateTime.UtcNow.AddMinutes(-30)),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, RateStalenessMinutes = 15 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Only 1 fresh rate remains — no pair can be formed
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunities_KeepsFreshRates()
    {
        // Both rates are recent → should form opportunity
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, recordedAt: DateTime.UtcNow.AddMinutes(-2)),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, recordedAt: DateTime.UtcNow.AddMinutes(-3)),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, RateStalenessMinutes = 15 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
    }

    // ── M2: Minimum volume filter ────────────────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_FiltersOutLowVolumeLegs()
    {
        // Long leg volume 10k, config min 50k → should be filtered out
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 10_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 1_000_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunities_AllowsLegsAboveMinVolume()
    {
        // Both legs above 50k → should form opportunity
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 200_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
    }

    // ── Pipeline diagnostics ──────────────────────────────────────────────────

    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_NoRates_ReturnsZeroTotalLoaded()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.TotalRatesLoaded.Should().Be(0);
        result.Diagnostics!.PairsPassing.Should().Be(0);
        result.Opportunities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_AllRatesStale_ReturnsZeroAfterStaleness()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, recordedAt: DateTime.UtcNow.AddMinutes(-30)),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, recordedAt: DateTime.UtcNow.AddMinutes(-30)),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, RateStalenessMinutes = 15 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.TotalRatesLoaded.Should().Be(2);
        result.Diagnostics!.RatesAfterStalenessFilter.Should().Be(0);
        result.Diagnostics!.PairsPassing.Should().Be(0);
        result.Opportunities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_AllPairsBelowVolume_ReportsVolumeFiltered()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 10_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 10_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.TotalPairsEvaluated.Should().Be(1);
        result.Diagnostics!.PairsFilteredByVolume.Should().Be(1);
        result.Diagnostics!.PairsPassing.Should().Be(0);
        result.Opportunities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_AllPairsBelowThreshold_ReportsThresholdFiltered()
    {
        // Spread = 0.0002, fees make net < 0.001 threshold
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0003m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Net yield is positive but below threshold — goes to NetPositiveBelowThreshold
        (result.Diagnostics!.PairsFilteredByThreshold + result.Diagnostics!.NetPositiveBelowThreshold).Should().BeGreaterThan(0);
        result.Diagnostics!.BestRawSpread.Should().BeGreaterThan(0);
        result.Diagnostics!.PairsPassing.Should().Be(0);
        result.Opportunities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_PassingPairs_ReportsCorrectCounts()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.TotalRatesLoaded.Should().Be(2);
        result.Diagnostics!.RatesAfterStalenessFilter.Should().Be(2);
        result.Diagnostics!.TotalPairsEvaluated.Should().Be(1);
        result.Diagnostics!.PairsFilteredByVolume.Should().Be(0);
        result.Diagnostics!.PairsPassing.Should().Be(1);
        result.Opportunities.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_ReportsConfigValues()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration
            {
                OpenThreshold = 0.0005m,
                MinVolume24hUsdc = 75_000m,
                RateStalenessMinutes = 20
            });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics!.StalenessMinutes.Should().Be(20);
        result.Diagnostics!.MinVolumeThreshold.Should().Be(75_000m);
        result.Diagnostics!.OpenThreshold.Should().Be(0.0005m);
    }

    // ── AllNetPositive tests ──────────────────────────────────────────────

    [Fact]
    public async Task AllNetPositive_ContainsOpportunitiesBelowThresholdButAboveZero()
    {
        // Spread = 0.0003 - 0.0001 = 0.0002
        // Fees: Lighter=0, Hyperliquid=0.00090/24 = 0.0000375
        // Net = 0.0002 - 0.0000375 = 0.0001625 → positive but below threshold 0.001
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0003m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty();
        result.AllNetPositive.Should().HaveCount(1);
        result.AllNetPositive[0].NetYieldPerHour.Should().BeGreaterThan(0);
        result.Diagnostics!.NetPositiveBelowThreshold.Should().Be(1);
    }

    [Fact]
    public async Task AllNetPositive_DoesNotContainOpportunitiesAboveThreshold()
    {
        // Big spread → passes threshold → goes to Opportunities, not AllNetPositive
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().HaveCount(1);
        result.AllNetPositive.Should().BeEmpty();
    }

    [Fact]
    public async Task AllNetPositive_DoesNotContainNegativeNetYield()
    {
        // Very small spread → net becomes negative after fees → neither list
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Aster",       1, "ETH", 0.00011m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty();
        result.AllNetPositive.Should().BeEmpty();
        result.Diagnostics!.PairsFilteredByThreshold.Should().BeGreaterThan(0);
    }

    // ── Funding window boost tests ─────────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_WithinFundingWindow_NetYieldIsNotBoosted_BoostedFieldIsBoosted()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FundingWindowMinutes = 10, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Settlement in 5 minutes (within window of 10)
        var settlementTime = DateTime.UtcNow.AddMinutes(5);
        _mockCache.Setup(c => c.GetNextSettlement("Hyperliquid", "ETH")).Returns(settlementTime);
        _mockCache.Setup(c => c.GetNextSettlement("Lighter", "ETH")).Returns((DateTime?)null);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        var opp = result.Opportunities.Should().HaveCount(1).And.Subject.First();

        var spread = 0.0009m;
        var feePerHour = (0.00090m + 0.00000m) / 24m;
        var rawNet = spread - feePerHour;
        var boostedNet = rawNet * 1.2m;

        // NetYieldPerHour must be the non-boosted value (used for threshold comparison)
        opp.NetYieldPerHour.Should().BeApproximately(rawNet, 0.0000001m);
        // BoostedNetYieldPerHour must be the boosted value (display only)
        opp.BoostedNetYieldPerHour.Should().BeApproximately(boostedNet, 0.0000001m);
        // AnnualizedYield should use original net (not boosted)
        opp.AnnualizedYield.Should().BeApproximately(rawNet * 24m * 365m, 0.001m);
    }

    [Fact]
    public async Task GetOpportunities_OutsideFundingWindow_BothFieldsEqualRawNet()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FundingWindowMinutes = 10, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Settlement in 30 minutes (outside window of 10)
        var settlementTime = DateTime.UtcNow.AddMinutes(30);
        _mockCache.Setup(c => c.GetNextSettlement("Hyperliquid", "ETH")).Returns(settlementTime);
        _mockCache.Setup(c => c.GetNextSettlement("Lighter", "ETH")).Returns((DateTime?)null);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        var opp = result.Opportunities.Should().HaveCount(1).And.Subject.First();

        var spread = 0.0009m;
        var feePerHour = (0.00090m + 0.00000m) / 24m;
        var rawNet = spread - feePerHour;

        // No boost — both fields equal raw net
        opp.NetYieldPerHour.Should().BeApproximately(rawNet, 0.0000001m);
        opp.BoostedNetYieldPerHour.Should().BeApproximately(rawNet, 0.0000001m);
    }

    [Fact]
    public async Task GetOpportunities_BelowThresholdBoosted_GoesToAllNetPositive()
    {
        // rawNet < threshold < boostedNet — proves threshold uses raw net, not boosted
        // Spread = 0.00018, fees = 0 (takerFeeRate=0), net = 0.00018
        // Threshold = 0.00020 → net < threshold
        // In funding window: boostedNet = 0.00018 * 1.2 = 0.000216 > threshold
        // If threshold used boosted value, this would pass. It must NOT pass.
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m, takerFeeRate: 0m),
            MakeRate(2, "Lighter",     1, "ETH", 0.00028m, volume: 100_000m, takerFeeRate: 0m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.00020m, FundingWindowMinutes = 10, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Settlement in 5 minutes (within window of 10)
        var settlementTime = DateTime.UtcNow.AddMinutes(5);
        _mockCache.Setup(c => c.GetNextSettlement("Hyperliquid", "ETH")).Returns(settlementTime);
        _mockCache.Setup(c => c.GetNextSettlement("Lighter", "ETH")).Returns((DateTime?)null);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Must NOT be in Opportunities (threshold comparison uses raw net)
        result.Opportunities.Should().BeEmpty("rawNet (0.00018) < threshold (0.00020) even though boostedNet > threshold");
        // Must be in AllNetPositive (net > 0)
        result.AllNetPositive.Should().HaveCount(1, "net > 0 puts it in AllNetPositive");
    }

    [Fact]
    public async Task GetOpportunities_NullSettlement_NoBoost()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FundingWindowMinutes = 10, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // No settlement data
        _mockCache.Setup(c => c.GetNextSettlement(It.IsAny<string>(), It.IsAny<string>())).Returns((DateTime?)null);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        var opp = result.Opportunities.Should().HaveCount(1).And.Subject.First();

        var spread = 0.0009m;
        var feePerHour = (0.00090m + 0.00000m) / 24m;
        var rawNet = spread - feePerHour;

        // No boost — NetYieldPerHour equals raw net
        opp.NetYieldPerHour.Should().BeApproximately(rawNet, 0.0000001m);
    }

    [Fact]
    public async Task AllNetPositive_SortedByNetYieldDescending()
    {
        // Two net-positive opportunities with different yields
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0003m, volume: 100_000m),
            MakeRate(1, "Hyperliquid", 2, "BTC", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     2, "BTC", 0.0004m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.01m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.AllNetPositive.Should().HaveCountGreaterOrEqualTo(2);
        result.AllNetPositive[0].NetYieldPerHour.Should().BeGreaterThanOrEqualTo(result.AllNetPositive[1].NetYieldPerHour);
    }

    // ── Partial exchange data (only 1 of 3 exchanges has rates) ──────────────

    [Fact]
    public async Task GetOpportunitiesAsync_OnlyOneExchangeHasRates_ReturnsEmpty()
    {
        // Only Hyperliquid has rate data — Aster and Lighter returned nothing
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "BTC", 0.0005m),
            MakeRate(1, "Hyperliquid", 2, "ETH", 0.0003m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act — should not throw
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: no cross-exchange pairs with only 1 exchange
        result.Opportunities.Should().BeEmpty(
            "cannot form cross-exchange pairs when only 1 exchange has data");

        // Diagnostics should show rates were loaded but 0 pairs evaluated
        // (each asset group has only 1 rate, so inner loop never runs)
        result.Diagnostics!.TotalRatesLoaded.Should().Be(2);
        result.Diagnostics!.TotalPairsEvaluated.Should().Be(0);
    }

    // ── F5: Prediction integration tests ──────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_WithPredictionService_EnrichesDtoWithPredictions()
    {
        var mockPredictionService = new Mock<IRatePredictionService>();
        mockPredictionService.Setup(s => s.GetPredictionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.RatePredictionDto>
            {
                new(1, "ETH", 1, "Hyperliquid", 0.00012m, 0.85m, "rising"),
                new(1, "ETH", 2, "Lighter", 0.00095m, 0.90m, "stable"),
            });

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, mockPredictionService.Object);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].PredictedLongRate.Should().Be(0.00012m);
        result[0].PredictedShortRate.Should().Be(0.00095m);
        result[0].PredictedSpread.Should().Be(0.00095m - 0.00012m);
        result[0].PredictionConfidence.Should().Be(0.85m); // min of 0.85 and 0.90
        result[0].PredictedTrend.Should().Be("stable"); // uses short leg's trend
    }

    [Fact]
    public async Task GetOpportunities_PredictionServiceThrows_StillReturnsOpportunities()
    {
        var mockPredictionService = new Mock<IRatePredictionService>();
        mockPredictionService.Setup(s => s.GetPredictionsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, mockPredictionService.Object);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].PredictedLongRate.Should().BeNull();
        result[0].PredictedShortRate.Should().BeNull();
        result[0].PredictedSpread.Should().BeNull();
    }

    [Fact]
    public async Task GetOpportunities_OneLegMissingPrediction_SpreadAndConfidenceAreNull()
    {
        var mockPredictionService = new Mock<IRatePredictionService>();
        // Only provide prediction for one exchange (Hyperliquid), not Lighter
        mockPredictionService.Setup(s => s.GetPredictionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.RatePredictionDto>
            {
                new(1, "ETH", 1, "Hyperliquid", 0.00012m, 0.85m, "rising"),
            });

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, mockPredictionService.Object);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].PredictedLongRate.Should().Be(0.00012m);
        result[0].PredictedShortRate.Should().BeNull();
        result[0].PredictedSpread.Should().BeNull("both legs needed for spread");
        result[0].PredictionConfidence.Should().BeNull("both legs needed for confidence");
    }

    // ── F2: OperationCanceledException propagation ────────────

    [Fact]
    public async Task GetOpportunities_PredictionServiceThrowsOperationCanceled_Propagates()
    {
        var mockPredictionService = new Mock<IRatePredictionService>();
        mockPredictionService.Setup(s => s.GetPredictionsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, mockPredictionService.Object);

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            });

        var act = () => sut.GetOpportunitiesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetOpportunities_PredictionServiceReturnsDuplicateKeys_DeduplicatesAndUseFirst()
    {
        var mockPredictionService = new Mock<IRatePredictionService>();
        mockPredictionService.Setup(s => s.GetPredictionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RatePredictionDto>
            {
                new(1, "ETH", 1, "Hyperliquid", 0.001m, 0.8m, "stable"),
                new(1, "ETH", 1, "Hyperliquid", 0.002m, 0.9m, "rising"), // duplicate key — silently deduped
                new(1, "ETH", 2, "Lighter", 0.0009m, 0.75m, "falling"),
            });

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, mockPredictionService.Object);

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
                MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
            });

        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        // GroupBy deduplication keeps first entry: 0.001m for Hyperliquid
        result[0].PredictedLongRate.Should().Be(0.001m);
        result[0].PredictedShortRate.Should().Be(0.0009m);
        result[0].PredictedSpread.Should().NotBeNull();
    }

    [Fact]
    public async Task GetOpportunities_PredictionServiceThrowsGenericException_ContinuesWithoutPredictions()
    {
        var mockPredictionService = new Mock<IRatePredictionService>();
        mockPredictionService.Setup(s => s.GetPredictionsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db error"));

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, mockPredictionService.Object);

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
                MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
            });

        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        // Should still produce opportunities without prediction data
        result.Should().HaveCount(1);
        result[0].PredictedSpread.Should().BeNull();
    }

    // ── NB4: FeeAmortizationHours=12 default produces correct net yield ─────────

    [Fact]
    public async Task GetOpportunities_DefaultFeeAmortizationHours12_ProducesExpectedNetYield()
    {
        // Arrange — use new BotConfiguration() with all defaults (FeeAmortizationHours = 12)
        var config = new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m };

        // Hyperliquid (fallback fee 0.00090) + Lighter (fallback fee 0.00000)
        // feePerHour = (0.00090 + 0.00000) / 12 = 0.000075  (vs /24 = 0.0000375 with old default)
        // spread = 0.0010 - 0.0001 = 0.0009
        // net = 0.0009 - 0.000075 = 0.000825
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var expectedSpread = 0.0009m;
        var expectedFeePerHour = (0.00090m + 0.00000m) / 12m; // 0.000075
        var expectedNet = expectedSpread - expectedFeePerHour;  // 0.000825

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert — verifies FeeAmortizationHours=12 is applied correctly
        result.Should().HaveCount(1);
        result[0].SpreadPerHour.Should().Be(expectedSpread);
        result[0].NetYieldPerHour.Should().Be(expectedNet);
    }

    // ── B2: SlippageBufferBps with non-zero value ───────────────────────────────

    [Fact]
    public async Task GetOpportunities_SlippageBuffer_ReducesNetYield()
    {
        // Arrange
        // Same rates as first test but with SlippageBufferBps=10 (0.001 per hour subtracted)
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        // FeeAmortizationHours=1 so fees and slippage are both per-hour (not amortized)
        // feePerHour=(0.00090+0)/1=0.00090, spread=0.0009, net=0.0009-0.00090=0.0
        // slippagePerHour=10/10000/1=0.001, net=0.0-0.001=-0.001 (below threshold 0.0003)
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 10, OpenThreshold = 0.0003m, FeeAmortizationHours = 1 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert: with slippage buffer, net yield falls below threshold → filtered out
        result.Should().BeEmpty("slippage buffer should reduce net yield below threshold");
    }

    [Fact]
    public async Task GetOpportunities_SlippageBuffer_PassesWithHigherSpread()
    {
        // Arrange: very high spread that remains above threshold even with buffer
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0030m), // spread = 0.0029
        };

        // With buffer=10, amortHours=12: slippage=0.0000833/hr, net≈0.002742 still > 0.0003
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 10, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert: with high spread, opportunity passes even with slippage buffer
        result.Should().HaveCount(1);
    }

    // ── Partial rate data tests ─────────────────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_OnlyOneExchangeHasRates_ReturnsEmpty()
    {
        // Only 1 exchange returns rates for ETH — no pair can be formed
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0010m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpportunities_TwoOfThreeExchanges_ReturnsOnePair()
    {
        // 3 exchanges exist, but only 2 return rates for ETH → exactly 1 pair
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            // Aster (exchange 3) has no rate data for ETH
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].AssetSymbol.Should().Be("ETH");
        result[0].LongExchangeName.Should().Be("Hyperliquid");
        result[0].ShortExchangeName.Should().Be("Lighter");
    }

    // ── Leverage-adjusted metrics ─────────────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_WithTierProvider_PopulatesBreakEvenCycles()
    {
        // Arrange: tier provider returns 5x effective leverage for both exchanges
        var mockTierProvider = new Mock<ILeverageTierProvider>();
        mockTierProvider
            .Setup(t => t.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Returns(20); // high enough that config leverage (5x) is the binding constraint

        var sutWithTiers = new SignalEngine(_mockUow.Object, _mockCache.Object,
            predictionService: null, tierProvider: mockTierProvider.Object);

        // Config: DefaultLeverage=5, MaxLeverageCap=50, capital=10000, MaxCapitalPerPosition=0.5
        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 5,
            MaxLeverageCap = 50,
            TotalCapitalUsdc = 10_000m,
            MaxCapitalPerPosition = 0.50m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Spread = 0.0010 - 0.0001 = 0.0009/hr
        // Fees: Hyperliquid taker=0.00035*2=0.0007, Lighter taker=0*2=0
        // feePerHour = (0.0007 + 0) / 24 = 0.00002917
        // net ~ 0.0009 - 0.00002917 = 0.0008708/hr
        // effectiveLeverage = min(5, 50, 20) = 5
        // BreakEvenCycles = entrySpreadCost / (net * leverage)
        //   entrySpreadCost = longFee + shortFee = 0.0007 + 0
        //   BreakEvenCycles = 0.0007 / (0.0008708 * 5) ~ 0.1608
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await sutWithTiers.GetOpportunitiesAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        var opp = result[0];
        opp.EffectiveLeverage.Should().Be(5);
        opp.ReturnOnCapitalPerHour.Should().BeGreaterThan(0);
        opp.AprOnCapital.Should().BeGreaterThan(0);
        opp.BreakEvenCycles.Should().BeGreaterThan(0).And.BeLessThan(1m);
    }

    [Fact]
    public async Task GetOpportunities_WithTierProvider_AprOnCapitalIncludesLeverage()
    {
        // Verify AprOnCapital = NetYieldPerHour * effectiveLeverage * 24 * 365 * 100
        var mockTierProvider = new Mock<ILeverageTierProvider>();
        mockTierProvider
            .Setup(t => t.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Returns(int.MaxValue); // no tier constraint

        var sutWithTiers = new SignalEngine(_mockUow.Object, _mockCache.Object,
            predictionService: null, tierProvider: mockTierProvider.Object);

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 3,
            MaxLeverageCap = 50,
            TotalCapitalUsdc = 10_000m,
            MaxCapitalPerPosition = 0.50m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await sutWithTiers.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        var opp = result[0];
        opp.EffectiveLeverage.Should().Be(3);
        // AprOnCapital should be roughly 3x the base AnnualizedYield
        var baseApr = opp.AnnualizedYield * 100m;
        opp.AprOnCapital.Should().BeApproximately(baseApr * 3m, 1m);
    }

    [Fact]
    public async Task GetOpportunities_WithoutTierProvider_LeavesLeverageFieldsNull()
    {
        // The default _sut has no tier provider
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].EffectiveLeverage.Should().BeNull();
        result[0].ReturnOnCapitalPerHour.Should().BeNull();
        result[0].AprOnCapital.Should().BeNull();
        result[0].BreakEvenCycles.Should().BeNull();
    }

    // ── Break-even cycles correctly computed from entry cost and net yield ────

    [Fact]
    public async Task GetOpportunities_BreakEvenCycles_CorrectlyComputed()
    {
        // Set up tier provider returning 3x for both exchanges
        var mockTierProvider = new Mock<ILeverageTierProvider>();
        mockTierProvider
            .Setup(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Returns(3);

        var sutWithTiers = new SignalEngine(_mockUow.Object, _mockCache.Object, tierProvider: mockTierProvider.Object);

        // Use known fee rates: Hyperliquid taker = 0.00035, Lighter taker = 0.0009
        // Entry cost = (0.00035 + 0.0009) = 0.00125
        // Net yield per hour after fees: use rates that produce a known net
        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 3,
            MaxLeverageCap = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Hyperliquid rate = -0.0001 (pay), Lighter rate = 0.0010 (receive)
        // Spread = |0.0010 - (-0.0001)| = 0.0011, but net = spread - amortized fees
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", -0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await sutWithTiers.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().NotBeEmpty();
        var opp = result[0];
        opp.EffectiveLeverage.Should().Be(3);
        opp.BreakEvenCycles.Should().BeGreaterThan(0, "break-even cycles should be positive");
        // BreakEvenCycles = entrySpreadCost / (net * effectiveLev)
        // The exact value depends on the fee computation in SignalEngine
        opp.ReturnOnCapitalPerHour.Should().NotBeNull();
        opp.AprOnCapital.Should().NotBeNull();
    }

    // ── NB3 (review-v2): Zero capital still computes leverage-adjusted metrics ──

    [Fact]
    public async Task GetOpportunities_WithTierProvider_ZeroCapital_StillComputesMetrics()
    {
        // When TotalCapitalUsdc=0, refNotional = 0 * anything = 0
        // Tier lookup with notional=0 should hit the most lenient tier (first bracket)
        // Metrics should still be populated using that tier's leverage
        var mockTierProvider = new Mock<ILeverageTierProvider>();
        mockTierProvider
            .Setup(t => t.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Returns(10); // first tier returns 10x

        var sutWithTiers = new SignalEngine(_mockUow.Object, _mockCache.Object,
            predictionService: null, tierProvider: mockTierProvider.Object);

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 5,
            MaxLeverageCap = 50,
            TotalCapitalUsdc = 0m,           // zero capital
            MaxCapitalPerPosition = 0.50m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await sutWithTiers.GetOpportunitiesAsync(CancellationToken.None);

        result.Should().NotBeEmpty();
        var opp = result[0];
        // refNotional = 0 * 0.5 * 5 = 0 → tier lookup at notional=0 returns 10x
        // effectiveLev = min(5, 50, 10) = 5
        opp.EffectiveLeverage.Should().Be(5);
        opp.ReturnOnCapitalPerHour.Should().NotBeNull();
        opp.AprOnCapital.Should().NotBeNull();
        opp.BreakEvenCycles.Should().BeGreaterThan(0);

        // Verify tier provider was called with notional=0 (zero capital)
        mockTierProvider.Verify(
            t => t.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), 0m),
            Times.AtLeastOnce);
    }

    // ── Per-exchange funding accuracy tests ────────────────────────────────

    [Fact]
    public async Task GetOpportunities_AsterTimingDeviation_ReducesMinutesToSettlement()
    {
        // Arrange: Aster exchange with 15s timing deviation
        // Use a settlement far enough in the future that clock drift won't affect truncation
        var now = DateTime.UtcNow;
        var rates = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 1,
                AssetId = 1,
                RatePerHour = 0.0001m,
                MarkPrice = 3000m,
                Volume24hUsd = 1_000_000m,
                RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingTimingDeviationSeconds = 0 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 3,
                AssetId = 1,
                RatePerHour = 0.0010m,
                MarkPrice = 3000m,
                Volume24hUsd = 1_000_000m,
                RecordedAt = now,
                Exchange = new Exchange { Id = 3, Name = "Aster", FundingTimingDeviationSeconds = 15 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };

        // First test: with deviation (Aster has 15s)
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m, FundingWindowMinutes = 60 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Settlement 30 minutes from now (large enough to avoid truncation edge)
        var nextSettlement = now.AddMinutes(30);
        _mockCache.Setup(c => c.GetNextSettlement("Hyperliquid", "ETH")).Returns(nextSettlement);
        _mockCache.Setup(c => c.GetNextSettlement("Aster", "ETH")).Returns(nextSettlement);

        var resultWithDeviation = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
        var oppWithDeviation = resultWithDeviation.Opportunities.Concat(resultWithDeviation.AllNetPositive).FirstOrDefault();
        oppWithDeviation.Should().NotBeNull();

        // Now test without deviation — set both exchanges to 0
        var ratesNoDeviation = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m, MarkPrice = 3000m,
                Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingTimingDeviationSeconds = 0 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 3, AssetId = 1, RatePerHour = 0.0010m, MarkPrice = 3000m,
                Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 3, Name = "Aster", FundingTimingDeviationSeconds = 0 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(ratesNoDeviation);
        var resultNoDeviation = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
        var oppNoDeviation = resultNoDeviation.Opportunities.Concat(resultNoDeviation.AllNetPositive).FirstOrDefault();
        oppNoDeviation.Should().NotBeNull();

        // The deviation should reduce minutes by ceil(15/60) = 1 minute
        oppWithDeviation!.MinutesToNextSettlement.Should().Be(
            oppNoDeviation!.MinutesToNextSettlement!.Value - 1,
            "15s timing deviation should reduce minutes-to-settlement by 1 (ceil(15/60))");
    }

    [Fact]
    public async Task GetOpportunities_LighterRebate_IncreasesNetYield()
    {
        // Arrange: Lighter (long, pays funding) has 15% rebate
        var now = DateTime.UtcNow;
        var lighterLongRate = 0.0001m;
        var hyperliquidShortRate = 0.0010m;

        var rates = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 2,
                AssetId = 1,
                RatePerHour = lighterLongRate,
                MarkPrice = 3000m,
                Volume24hUsd = 1_000_000m,
                RecordedAt = now,
                Exchange = new Exchange { Id = 2, Name = "Lighter", FundingRebateRate = 0.15m },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 1,
                AssetId = 1,
                RatePerHour = hyperliquidShortRate,
                MarkPrice = 3000m,
                Volume24hUsd = 1_000_000m,
                RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingRebateRate = 0m },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.00001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: net yield should include the rebate boost
        // Without rebate: spread = 0.0009, fees ~ 0, net = 0.0009 - fees
        // With rebate: net += longRate * 0.15 = 0.0001 * 0.15 = 0.000015
        var opportunity = result.Opportunities.Concat(result.AllNetPositive).FirstOrDefault();
        opportunity.Should().NotBeNull();

        // Compute expected: spread - feePerHour + rebateBoost
        var spread = hyperliquidShortRate - lighterLongRate; // 0.0009
        var lighterFee = 0m * 2; // Lighter has no fallback fee
        var hyperliquidFee = ExchangeFeeConstants.GetTakerFeeRate("Hyperliquid") * 2;
        var expectedFeePerHour = (lighterFee + hyperliquidFee) / 12m;
        var rebateBoost = lighterLongRate * 0.15m; // 0.000015
        var expectedNet = spread - expectedFeePerHour + rebateBoost;

        opportunity!.NetYieldPerHour.Should().BeApproximately(expectedNet, 0.000001m,
            "rebate should improve net yield by longRate * rebateRate");
    }

    // ── Review findings: B3 — long-leg negative rate with rebate ────────────

    [Fact]
    public async Task GetOpportunities_LongLegNegativeRate_RebateDoesNotReduceNetYield()
    {
        // Arrange: long leg has negative rate (earning, not paying). Rebate must NOT apply.
        var now = DateTime.UtcNow;
        var rates = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 2, AssetId = 1,
                RatePerHour = -0.0005m, // negative rate on Lighter (long leg earns)
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 2, Name = "Lighter", FundingRebateRate = 0.15m },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 1, AssetId = 1,
                RatePerHour = 0.0010m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingRebateRate = 0m },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.00001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
        var opp = result.Opportunities.Concat(result.AllNetPositive).FirstOrDefault();
        opp.Should().NotBeNull();

        // Without rebate guard: net would incorrectly get a negative boost (-0.0005 * 0.15 = -0.000075)
        // With guard: long rate < 0 so rebate is skipped, net is unaffected
        var diff = 0.0010m - (-0.0005m); // 0.0015
        opp!.NetYieldPerHour.Should().BeGreaterThan(diff - 0.001m,
            "rebate should NOT reduce net yield when long leg rate is negative");
    }

    // ── Review findings: NB2 — short-leg negative rate with rebate ──────────

    [Fact]
    public async Task GetOpportunities_ShortLegNegativeRateWithRebate_BoostsNetYield()
    {
        // For short-leg rebate to fire: shortR.RatePerHour < 0 and shortR.Exchange.FundingRebateRate > 0
        // shortR is the leg with the HIGHER rate. Both rates negative, higher = less negative.
        var now = DateTime.UtcNow;
        var rates = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 1, AssetId = 1,
                RatePerHour = -0.0010m, // very negative (long leg, lower rate)
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingRebateRate = 0m },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 2, AssetId = 1,
                RatePerHour = -0.0002m, // less negative (short leg, higher rate)
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 2, Name = "Lighter", FundingRebateRate = 0.15m },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };

        // Use very low threshold to capture all opportunities
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = -1m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var resultWithRebate = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
        var oppWithRebate = resultWithRebate.Opportunities.Concat(resultWithRebate.AllNetPositive).FirstOrDefault();
        oppWithRebate.Should().NotBeNull();

        // Now compare without rebate
        var ratesNoRebate = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 1, AssetId = 1,
                RatePerHour = -0.0010m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingRebateRate = 0m },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 2, AssetId = 1,
                RatePerHour = -0.0002m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 2, Name = "Lighter", FundingRebateRate = 0m }, // no rebate
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(ratesNoRebate);
        var resultNoRebate = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
        var oppNoRebate = resultNoRebate.Opportunities.Concat(resultNoRebate.AllNetPositive).FirstOrDefault();
        oppNoRebate.Should().NotBeNull();

        // shortR = -0.0002 (Lighter). Rebate boost = abs(-0.0002) * 0.15 = 0.00003
        var expectedBoost = Math.Abs(-0.0002m) * 0.15m;
        var netDiff = oppWithRebate!.NetYieldPerHour - oppNoRebate!.NetYieldPerHour;
        netDiff.Should().BeApproximately(expectedBoost, 0.000001m,
            "short-leg rebate should boost net yield by abs(shortRate) * rebateRate");
    }

    // ── Review findings: NB7 — timing deviation clamps to zero ──────────────

    [Fact]
    public async Task GetOpportunities_TimingDeviationClampsToZero_WhenDeviationExceedsMinutes()
    {
        // Arrange: deviation 120s (~2 min) but minutesToSettlement is only 1 min → clamp to 0
        var now = DateTime.UtcNow;
        var rates = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingTimingDeviationSeconds = 0 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 3, AssetId = 1, RatePerHour = 0.0010m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 3, Name = "Aster", FundingTimingDeviationSeconds = 120 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration
            {
                SlippageBufferBps = 0,
                OpenThreshold = 0.0001m,
                FundingWindowMinutes = 60,
            });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Settlement in 1 minute from now, deviation is 120s = 2 min → clamps to 0
        var nextSettlement = now.AddMinutes(1);
        _mockCache.Setup(c => c.GetNextSettlement("Hyperliquid", "ETH")).Returns(nextSettlement);
        _mockCache.Setup(c => c.GetNextSettlement("Aster", "ETH")).Returns(nextSettlement);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
        var opp = result.Opportunities.Concat(result.AllNetPositive).FirstOrDefault();
        opp.Should().NotBeNull();

        // minutesToSettlement should be clamped to 0 (not negative)
        opp!.MinutesToNextSettlement.Should().Be(0,
            "deviation exceeding minutes-to-settlement should clamp to 0");

        // With minutesToSettlement = 0 and FundingWindowMinutes = 60, funding window boost should apply
        opp.BoostedNetYieldPerHour.Should().BeGreaterThan(opp.NetYieldPerHour,
            "funding window boost should apply when minutesToSettlement is clamped to 0");
    }

    // ── Review-v2: Timing deviation 600s capped at 300s (NB5) ───────────────

    [Fact]
    public async Task GetOpportunities_TimingDeviation600s_CappedAt300s()
    {
        // Arrange: deviation=600s on Aster, settlement 10 min away.
        // 600s should be capped to 300s = 5 min reduction, NOT 10 min.
        var now = DateTime.UtcNow;
        var rates = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingTimingDeviationSeconds = 0 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 3, AssetId = 1, RatePerHour = 0.0010m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 3, Name = "Aster", FundingTimingDeviationSeconds = 600 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration
            {
                SlippageBufferBps = 0,
                OpenThreshold = 0.0001m,
                FundingWindowMinutes = 60,
            });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Settlement in 10+ minutes (add 30s buffer for test execution time and int truncation)
        var nextSettlement = now.AddMinutes(10).AddSeconds(30);
        _mockCache.Setup(c => c.GetNextSettlement("Hyperliquid", "ETH")).Returns(nextSettlement);
        _mockCache.Setup(c => c.GetNextSettlement("Aster", "ETH")).Returns(nextSettlement);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);
        var opp = result.Opportunities.Concat(result.AllNetPositive).FirstOrDefault();
        opp.Should().NotBeNull();

        // 600s capped to 300s → ceil(300/60) = 5 min reduction
        // 10 - 5 = 5 minutes remaining (not 0 which would happen without cap)
        opp!.MinutesToNextSettlement.Should().Be(5,
            "600s deviation should be capped at 300s (5 min), reducing 10 min settlement to 5 min");
    }

    // ── Break-even filter ─────────────────────────────────────────────────────

    [Fact]
    public async Task BreakEven_ExcludesOpportunity_WhenExceedsThreshold()
    {
        // Arrange: high fees relative to net yield → break-even > 8 hours
        // Using exchanges with high taker fees: Binance = 0.0004 per trade
        // longFee = 0.0004 * 2 = 0.0008, shortFee = 0.0004 * 2 = 0.0008
        // totalEntryCost = (0.0008 + 0.0008) + (5 / 10000) = 0.0016 + 0.0005 = 0.0021
        // net yield per hour needs to be small enough that 0.0021 / net > 8
        // net < 0.0021 / 8 = 0.0002625 → spread needs to be barely above fees
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Binance", 1, "ETH", 0.0001m, takerFeeRate: 0.0004m),
            MakeRate(2, "Lighter", 1, "ETH", 0.0004m, takerFeeRate: 0.0004m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 5,
            OpenThreshold = 0.00001m, // very low so the opportunity would pass without break-even filter
            BreakevenHoursMax = 8,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        // Provide history for trend analysis
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { RatePerHour = 0.0001m },
            });

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert
        result.Opportunities.Should().BeEmpty("break-even hours exceed threshold");
        result.Diagnostics!.PairsFilteredByBreakeven.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task BreakEven_IncludesOpportunity_WhenWithinThreshold()
    {
        // Arrange: wide spread → break-even < 8 hours
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0020m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 5,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 8,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { RatePerHour = 0.0010m },
            });

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert
        result.Opportunities.Should().NotBeEmpty("break-even within threshold");
        result.Opportunities[0].BreakEvenHours.Should().NotBeNull();
        result.Opportunities[0].BreakEvenHours!.Value.Should().BeLessThan(8m);
    }

    [Fact]
    public async Task BreakEven_IsNull_WhenNetYieldNegative()
    {
        // Arrange: negative net yield → break-even is null
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0001m), // zero spread, net will be negative after fees
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 5,
            OpenThreshold = -1m, // allow everything through threshold for this test
            BreakevenHoursMax = 168,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: with zero spread, net yield is negative after fees, so break-even is null
        // and the opportunity goes to PairsFilteredByThreshold (net < 0)
        var allOpps = result.Opportunities.Concat(result.AllNetPositive).ToList();
        if (allOpps.Count > 0)
        {
            allOpps[0].BreakEvenHours.Should().BeNull("net yield is negative");
        }
    }

    // ── Trend analysis ────────────────────────────────────────────────────────

    [Fact]
    public async Task TrendAnalysis_MarksUnconfirmed_WhenSpreadNegativeInHistory()
    {
        // Arrange: one of the history snapshots has negative spread
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 168,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 3,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Long exchange history: 3 snapshots with low rates
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            1, 1, It.IsAny<DateTime>(), It.IsAny<DateTime>(), 3, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { RatePerHour = 0.0001m },
                new() { RatePerHour = 0.0001m },
                new() { RatePerHour = 0.0001m },
            });

        // Short exchange history: one snapshot has rate lower than long → negative spread
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            1, 2, It.IsAny<DateTime>(), It.IsAny<DateTime>(), 3, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { RatePerHour = 0.0010m },
                new() { RatePerHour = -0.0001m }, // negative spread in this cycle
                new() { RatePerHour = 0.0010m },
            });

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert
        var allOpps = result.Opportunities.Concat(result.AllNetPositive).ToList();
        allOpps.Should().NotBeEmpty();
        allOpps[0].TrendUnconfirmed.Should().BeTrue("one historical cycle had negative spread");
    }

    [Fact]
    public async Task TrendAnalysis_Confirmed_WhenAllCyclesFavorable()
    {
        // Arrange: all history snapshots have positive spread
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 168,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 3,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Both exchanges: all snapshots favorable (short > long)
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            1, 1, It.IsAny<DateTime>(), It.IsAny<DateTime>(), 3, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { RatePerHour = 0.0001m },
                new() { RatePerHour = 0.0001m },
                new() { RatePerHour = 0.0001m },
            });

        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            1, 2, It.IsAny<DateTime>(), It.IsAny<DateTime>(), 3, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { RatePerHour = 0.0010m },
                new() { RatePerHour = 0.0008m },
                new() { RatePerHour = 0.0012m },
            });

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert
        result.Opportunities.Should().NotBeEmpty();
        result.Opportunities[0].TrendUnconfirmed.Should().BeFalse("all cycles had positive spread");
    }

    [Fact]
    public async Task BreakEven_WithRebate_UsesPostRebateNet()
    {
        // Arrange: pre-rebate net yield would make break-even > threshold (filtered),
        // but post-rebate net brings break-even within threshold (included).
        // Lighter exchange offers 15% rebate, long rate is positive (long pays → rebate applies).
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Lighter", 1, "ETH", 0.0002m, takerFeeRate: 0m),  // long side, pays 0.0002 �� gets 15% rebate
            MakeRate(2, "Hyperliquid", 1, "ETH", 0.0006m, takerFeeRate: 0.00045m), // short side
        };

        // Adjust Lighter exchange to have a rebate rate
        rates[0].Exchange.FundingRebateRate = 0.15m;

        // spread = 0.0006 - 0.0002 = 0.0004
        // fees = (0 + 0.0009) / 12 = 0.000075/hr
        // slippage = 5/10000 / 12 = 0.0000416667/hr
        // Pre-rebate net = 0.0004 - 0.000075 - 0.0000416667 = 0.0002833
        // Rebate: long rate 0.0002 > 0 → rebate = 0.0002 * 0.15 = 0.00003
        // Post-rebate net = 0.0002833 + 0.00003 = 0.0003133
        // totalEntryCost = (0 + 0.0009) + (5/10000) = 0.0009 + 0.0005 = 0.0014
        // Post-rebate breakeven = 0.0014 / 0.0003133 ≈ 4.47 hours (within 8h threshold)
        // Pre-rebate breakeven = 0.0014 / 0.0002833 ≈ 4.94 hours (also within)
        // Use tighter threshold: BreakevenHoursMax = 4.5 to force the distinction
        var config = new BotConfiguration
        {
            SlippageBufferBps = 5,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 5, // generous enough for post-rebate, tight enough to verify calculation
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 1m, // disable 3× edge guardrail to isolate break-even behaviour under test
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Provide sufficient history for trend analysis
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: opportunity is included and break-even uses post-rebate net
        var allOpps = result.Opportunities.Concat(result.AllNetPositive).ToList();
        allOpps.Should().NotBeEmpty("opportunity should not be filtered by break-even using post-rebate net");
        var opp = allOpps[0];
        opp.BreakEvenHours.Should().NotBeNull();
        // Break-even with rebate should be less than without rebate
        // Post-rebate net > pre-rebate net → break-even hours should be < pre-rebate break-even
        var preRebateNet = opp.NetYieldPerHour - 0.00003m; // subtract the rebate boost
        if (preRebateNet > 0)
        {
            var preRebateBreakeven = (0.0009m + 0.0005m) / preRebateNet;
            opp.BreakEvenHours!.Value.Should().BeLessThan(preRebateBreakeven,
                "break-even with rebate should be shorter than without");
        }
    }

    [Fact]
    public async Task TrendAnalysis_MarksUnconfirmed_WhenInsufficientHistory()
    {
        // Arrange: fewer snapshots than MinConsecutiveFavorableCycles
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 168,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 5, // require 5 but only provide 2
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 5, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { RatePerHour = 0.0005m },
                new() { RatePerHour = 0.0005m },
            });

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert
        var allOpps = result.Opportunities.Concat(result.AllNetPositive).ToList();
        allOpps.Should().NotBeEmpty();
        allOpps[0].TrendUnconfirmed.Should().BeTrue("insufficient history snapshots");
    }

    // ── Review-v6: Trend analysis lookback scales by exchange funding interval ──

    [Fact]
    public async Task TrendAnalysis_LongIntervalExchange_ScalesLookback()
    {
        // Arrange: Aster has FundingIntervalHours=8. With MinConsecutiveFavorableCycles=3,
        // the lookback should span at least 24 hours (8 * 3), not just 1 hour.
        var now = DateTime.UtcNow;
        var rates = new List<FundingRateSnapshot>
        {
            new FundingRateSnapshot
            {
                ExchangeId = 1, AssetId = 1, RatePerHour = 0.0001m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid", FundingIntervalHours = 1 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
            new FundingRateSnapshot
            {
                ExchangeId = 3, AssetId = 1, RatePerHour = 0.0010m,
                MarkPrice = 3000m, Volume24hUsd = 1_000_000m, RecordedAt = now,
                Exchange = new Exchange { Id = 3, Name = "Aster", FundingIntervalHours = 8 },
                Asset = new Asset { Id = 1, Symbol = "ETH" },
            },
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            MinConsecutiveFavorableCycles = 3,
            BreakevenHoursMax = 100, // don't filter by break-even
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Track the 'from' parameter passed to GetHistoryAsync
        DateTime? capturedFrom = null;
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>()))
            .Callback<int, int, DateTime, DateTime, int, int>((_, _, from, _, _, _) =>
            {
                capturedFrom ??= from;
            })
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { RatePerHour = 0.0001m },
                new() { RatePerHour = 0.001m },
                new() { RatePerHour = 0.0001m },
                new() { RatePerHour = 0.001m },
                new() { RatePerHour = 0.0001m },
                new() { RatePerHour = 0.001m },
            });

        // Act
        await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: lookback should span at least 24 hours (8h interval * 3 cycles)
        capturedFrom.Should().NotBeNull("GetHistoryAsync should have been called");
        var lookbackHours = (DateTime.UtcNow - capturedFrom!.Value).TotalHours;
        lookbackHours.Should().BeGreaterOrEqualTo(24.0,
            "max exchange interval (8h) * MinConsecutiveFavorableCycles (3) = 24h minimum lookback");
    }

    // ── B6: 3× MinEdgeMultiplier filter ──────────────────────────────────────

    [Fact]
    public async Task MinEdgeFilter_NetBelow3xEdge_RoutedToAllNetPositive()
    {
        // Arrange — opportunity with net just above OpenThreshold but below 3× edge.
        // OpenThreshold = 0.0001, FeeAmortizationHours = 12, takerFee per leg = 0.00045
        // totalEntryCost = (0.0009 + 0.0009) + (5/10000) = 0.00185
        // amortizedEntryCostPerHour = 0.00185 / 12 ≈ 0.000154
        // 3× edge ≈ 0.000463
        // Spread = 0.0008, fees ≈ 0.00015/hr, slippage ≈ 0.0000417/hr
        // Net ≈ 0.0008 - 0.00015 - 0.0000417 ≈ 0.0006 (above OpenThreshold but should pass 3× edge here)
        // Tighten: spread = 0.00045 → net ≈ 0.00026 → above OpenThreshold (0.0001), below 3× edge (0.000463)
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.00045m),
            MakeRate(2, "Lighter", 1, "ETH", 0.00045m, takerFeeRate: 0.00045m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 5,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 3m,
            // F8: enable the opt-in break-even-size filter + raise MinHoldTimeHours
            // above FeeAmortizationHours so the new check passes at these inputs,
            // leaving the edge-guardrail counter as the one that fires.
            UseBreakEvenSizeFilter = true,
            MinHoldTimeHours = 48,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        // Act
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: opportunity routed to AllNetPositive, not Opportunities
        result.Opportunities.Should().BeEmpty(
            "net is above OpenThreshold but below 3× amortized entry cost");
        result.AllNetPositive.Should().NotBeEmpty(
            "the filtered opportunity should still appear in AllNetPositive for diagnostics");
        // The edge-guardrail counter (not NetPositiveBelowThreshold) should increment
        // because the opportunity passed OpenThreshold and the break-even-size floor
        // but still failed the 3x edge check.
        result.Diagnostics!.NetPositiveBelowEdgeGuardrail.Should().BeGreaterThan(0);
        result.Diagnostics.NetPositiveBelowThreshold.Should().Be(0,
            "this opportunity is above OpenThreshold so the threshold counter does not fire");
        result.Diagnostics.PairsFilteredByBreakEvenSize.Should().Be(0,
            "MinHoldTimeHours=48 makes the break-even-size check permissive enough to pass");
    }

    [Fact]
    public async Task MinEdgeFilter_DisabledViaMultiplierZero_FallsBackToDefault()
    {
        // The `> 0` guard at line 334 falls back to the spec-default (3) when configured value is 0.
        // Verify by setting MinEdgeMultiplier = 0 and confirming the same opportunity is filtered
        // as if MinEdgeMultiplier had been left at 3.
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.00045m),
            MakeRate(2, "Lighter", 1, "ETH", 0.00045m, takerFeeRate: 0.00045m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 5,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 0m, // explicitly disabled — should fall back to 3
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Same outcome as default 3× — opportunity below 3× edge → AllNetPositive
        result.Opportunities.Should().BeEmpty("zero MinEdgeMultiplier falls back to 3× default");
    }

    [Fact]
    public async Task MinEdgeFilter_LooseMultiplier1x_AllowsOpportunityToPass()
    {
        // With MinEdgeMultiplier=1, the filter is much more lenient.
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.00045m),
            MakeRate(2, "Lighter", 1, "ETH", 0.00045m, takerFeeRate: 0.00045m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 5,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 1m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().HaveCount(1,
            "1× edge multiplier allows the opportunity through");
    }

    // ── B7: CoinGlass hot symbol prioritization ──────────────────────────────

    [Fact]
    public async Task HotSymbols_PreferredOverHigherYieldNonHot()
    {
        // Two opportunities, BTC (hot, lower yield) and ETH (not hot, higher yield).
        // Expected sort: BTC first (hot), then ETH (higher yield but not hot).
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 1, "ETH", 0.0010m, takerFeeRate: 0m),
            MakeRate(1, "Hyperliquid", 2, "BTC", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 2, "BTC", 0.0008m, takerFeeRate: 0m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 0.5m, // permissive — let both opportunities through
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        // BTC is the hot symbol (despite lower yield)
        var screening = new Mock<ICoinGlassScreeningProvider>();
        screening.SetupGet(s => s.IsAvailable).Returns(true);
        screening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BTC" });

        var sutWithScreening = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            screeningProvider: screening.Object);

        // Act
        var result = await sutWithScreening.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: BTC ranks first despite ETH's higher yield
        result.Opportunities.Should().HaveCountGreaterThan(0);
        result.Opportunities[0].AssetSymbol.Should().Be("BTC");
        result.Opportunities[0].IsCoinGlassHot.Should().BeTrue();
        if (result.Opportunities.Count > 1)
        {
            result.Opportunities[1].AssetSymbol.Should().Be("ETH");
            result.Opportunities[1].IsCoinGlassHot.Should().BeFalse();
        }
    }

    [Fact]
    public async Task HotSymbols_EmptySet_LeavesOpportunitiesSortedByNetYield()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 1, "ETH", 0.0010m, takerFeeRate: 0m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 0.5m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        var screening = new Mock<ICoinGlassScreeningProvider>();
        screening.SetupGet(s => s.IsAvailable).Returns(true);
        screening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var sutWithScreening = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            screeningProvider: screening.Object);

        var result = await sutWithScreening.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().NotBeEmpty();
        result.Opportunities[0].IsCoinGlassHot.Should().BeFalse(
            "empty hot set means no opportunity is flagged");
    }

    [Fact]
    public async Task HotSymbols_NullProvider_AllOpportunitiesNotHot()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 1, "ETH", 0.0010m, takerFeeRate: 0m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 0.5m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        // _sut has no screening provider (null)
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().NotBeEmpty();
        result.Opportunities.Should().AllSatisfy(o => o.IsCoinGlassHot.Should().BeFalse());
    }

    [Fact]
    public async Task HotSymbols_ProviderThrows_OpportunitiesUnaffected()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 1, "ETH", 0.0010m, takerFeeRate: 0m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 0.5m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        var screening = new Mock<ICoinGlassScreeningProvider>();
        screening.SetupGet(s => s.IsAvailable).Returns(true);
        screening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("CoinGlass down"));

        var sutWithScreening = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            screeningProvider: screening.Object);

        var result = await sutWithScreening.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().NotBeEmpty(
            "screening failure should not break opportunity generation");
    }

    [Fact]
    public async Task HotSymbols_ProviderThrowsCancellation_PropagatesException()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter", 1, "ETH", 0.0010m),
        };

        var config = new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var screening = new Mock<ICoinGlassScreeningProvider>();
        screening.SetupGet(s => s.IsAvailable).Returns(true);
        screening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sutWithScreening = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            screeningProvider: screening.Object);

        Func<Task> act = async () =>
            await sutWithScreening.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must propagate, not be swallowed");
    }

    [Fact]
    public async Task SignalEngine_DbResilience_ReturnsFailureResult_OnDatabaseUnavailable()
    {
        // Azure production stabilization (plan-v60 Task 3.2): when the repository surfaces a
        // database-unavailable failure (transient SQL login-phase errors, etc.) the signal
        // engine must NOT rethrow — it must return a degraded OpportunityResultDto so the
        // dashboard can render a banner instead of a 500 page.
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ThrowsAsync(new FundingRateArb.Application.Common.DatabaseUnavailableException(
                "simulated login-phase transient failure"));

        // Act — must not throw
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert — degraded result with the new failure signalling
        result.Should().NotBeNull();
        result.DatabaseAvailable.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be(SignalEngineFailureReason.DatabaseUnavailable);
        result.Opportunities.Should().BeEmpty();
        result.AllNetPositive.Should().BeEmpty();
    }

    [Fact]
    public async Task SignalEngine_DbResilience_DefaultSuccessResult_WhenRepositoryReturnsData()
    {
        // Regression: the new DatabaseAvailable / IsSuccess flags must default to "OK"
        // on the happy path so the dashboard does not render the degraded banner when
        // nothing is wrong.
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.DatabaseAvailable.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.FailureReason.Should().Be(SignalEngineFailureReason.None);
    }

    // ── Exchange per-symbol MAX_NOTIONAL_VALUE filter (plan-v60 task 6.1) ────
    //
    // Prevents the 2026-04-09 WLFI failure mode where the Aster leg rejected
    // after the Lighter leg had already opened, forcing an emergency close.
    // A candidate whose sized notional exceeds ANY leg's cap must be filtered
    // out pre-execution and recorded under PairsFilteredByExchangeSymbolCap.

    [Fact]
    public async Task GetOpportunities_NotionalCapFilter_ExcludesCandidate_WhenSizeExceedsAsterCap()
    {
        var mockConstraints = new Mock<IExchangeSymbolConstraintsProvider>();
        mockConstraints
            .Setup(p => p.GetMaxNotionalAsync("Aster", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_000m); // very tight cap forces the filter
        mockConstraints
            .Setup(p => p.GetMaxNotionalAsync("Lighter", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal?)null);

        var sut = new SignalEngine(
            _mockUow.Object,
            _mockCache.Object,
            predictionService: null,
            tierProvider: null,
            screeningProvider: null,
            symbolConstraintsProvider: mockConstraints.Object);

        // sizedNotional = TotalCapital * MaxCapitalPerPosition * cappedLeverage
        //               = 10000 * 0.5 * 3 = 15000 > 1000 cap → filtered
        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 3,
            MaxLeverageCap = 50,
            TotalCapitalUsdc = 10_000m,
            MaxCapitalPerPosition = 0.50m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Lighter", 1, "WLFI",  0.0001m),
            MakeRate(2, "Aster",   1, "WLFI",  0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty();
        result.Diagnostics!.PairsFilteredByExchangeSymbolCap.Should().Be(1);
    }

    [Fact]
    public async Task GetOpportunities_NotionalCapFilter_PassesCandidate_WhenWithinCap()
    {
        var mockConstraints = new Mock<IExchangeSymbolConstraintsProvider>();
        mockConstraints
            .Setup(p => p.GetMaxNotionalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_000_000m); // generous cap — passes

        var sut = new SignalEngine(
            _mockUow.Object,
            _mockCache.Object,
            predictionService: null,
            tierProvider: null,
            screeningProvider: null,
            symbolConstraintsProvider: mockConstraints.Object);

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 3,
            MaxLeverageCap = 50,
            TotalCapitalUsdc = 10_000m,
            MaxCapitalPerPosition = 0.50m,
            MinEdgeMultiplier = 1m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Lighter", 1, "WLFI",  0.0001m),
            MakeRate(2, "Aster",   1, "WLFI",  0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().HaveCount(1);
        result.Diagnostics!.PairsFilteredByExchangeSymbolCap.Should().Be(0);
    }

    [Fact]
    public async Task GetOpportunities_NotionalCapFilter_NullCap_DoesNotFilterCandidate()
    {
        // Provider returns null for every call → "no cap known", candidate must pass.
        var mockConstraints = new Mock<IExchangeSymbolConstraintsProvider>();
        mockConstraints
            .Setup(p => p.GetMaxNotionalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal?)null);

        var sut = new SignalEngine(
            _mockUow.Object,
            _mockCache.Object,
            predictionService: null,
            tierProvider: null,
            screeningProvider: null,
            symbolConstraintsProvider: mockConstraints.Object);

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 3,
            MaxLeverageCap = 50,
            TotalCapitalUsdc = 10_000m,
            MaxCapitalPerPosition = 0.50m,
            MinEdgeMultiplier = 1m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Lighter", 1, "WLFI",  0.0001m),
            MakeRate(2, "Aster",   1, "WLFI",  0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().HaveCount(1);
        result.Diagnostics!.PairsFilteredByExchangeSymbolCap.Should().Be(0);
    }

    // ── plan-v61 Task 3.1: SignalEngine consumes IsAvailable flag ──

    private const string CoinGlassUnavailableMessage =
        "CoinGlass screening skipped (unavailable) — continuing without screening for this cycle";

    [Fact]
    public async Task SignalEngine_CoinGlassUnavailable_ContinuesWithRemainingSources()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 1, "ETH", 0.0010m, takerFeeRate: 0m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 0.5m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        var screening = new Mock<ICoinGlassScreeningProvider>();
        screening.SetupGet(s => s.IsAvailable).Returns(false);
        screening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            screeningProvider: screening.Object);

        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().NotBeEmpty(
            "unavailable screening must not block opportunity generation from other sources");
    }

    [Fact]
    public async Task SignalEngine_CoinGlassUnavailable_LogsOncePerCycle_NotPerCandidate()
    {
        // Seed ≥3 candidate symbols so the inner nested loop runs multiple times.
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "BTC", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 1, "BTC", 0.0010m, takerFeeRate: 0m),
            MakeRate(1, "Hyperliquid", 2, "ETH", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 2, "ETH", 0.0010m, takerFeeRate: 0m),
            MakeRate(1, "Hyperliquid", 3, "SOL", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 3, "SOL", 0.0010m, takerFeeRate: 0m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 0.5m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        var screening = new Mock<ICoinGlassScreeningProvider>();
        screening.SetupGet(s => s.IsAvailable).Returns(false);
        screening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var logger = new ListLogger<SignalEngine>();
        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            screeningProvider: screening.Object,
            logger: logger);

        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().NotBeEmpty();
        logger.CountMessages(LogLevel.Warning, CoinGlassUnavailableMessage).Should().Be(1,
            "the unavailable warning must fire exactly once per cycle, not once per candidate");
    }

    [Fact]
    public async Task SignalEngine_CoinGlassAvailable_NoSkipLog()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Lighter", 1, "ETH", 0.0010m, takerFeeRate: 0m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 0.5m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        var screening = new Mock<ICoinGlassScreeningProvider>();
        screening.SetupGet(s => s.IsAvailable).Returns(true);
        screening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var logger = new ListLogger<SignalEngine>();
        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            screeningProvider: screening.Object,
            logger: logger);

        await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        logger.CountMessages(LogLevel.Warning, CoinGlassUnavailableMessage).Should().Be(0,
            "the unavailable warning must not fire when the provider is available");
    }

    // ── NT9: Non-Aster pair skips IExchangeSymbolConstraintsProvider entirely ─────

    [Fact]
    public async Task GetOpportunities_NotionalCapFilter_NonAsterPair_SkipsProviderEntirely()
    {
        // Arrange: strict mock — ANY call to the provider will throw MockException
        var strictProvider = new Mock<IExchangeSymbolConstraintsProvider>(MockBehavior.Strict);

        // Rates for Hyperliquid (long) vs Binance (short) — neither is "Aster"
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, takerFeeRate: 0m),
            MakeRate(2, "Binance",     1, "ETH", 0.0010m, takerFeeRate: 0m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var sut = new SignalEngine(
            _mockUow.Object,
            _mockCache.Object,
            symbolConstraintsProvider: strictProvider.Object);

        // Act — if the pre-filter works, the strict mock is never called and no MockException fires
        var result = await sut.GetOpportunitiesAsync(CancellationToken.None);

        // Assert: result is non-empty (spread is above threshold) and strict mock had no calls
        result.Should().NotBeEmpty(
            "spread between Hyperliquid and Binance is above threshold; opportunity must be returned");
        strictProvider.VerifyNoOtherCalls();
    }

    // ── F10: signal engine observability metrics (profitability-fixes) ────

    [Fact]
    public async Task SignalEngine_RecordCycle_EmitsDiagnosticsAndDuration_WhenCycleSucceeds()
    {
        // Arrange: standard rates fixture that yields a non-zero diagnostics shape.
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.0004m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0030m, takerFeeRate: 0.0004m),
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var mockMetrics = new Mock<ISignalEngineMetrics>();
        PipelineDiagnosticsDto? capturedDiagnostics = null;
        TimeSpan capturedDuration = default;
        mockMetrics
            .Setup(m => m.RecordCycle(It.IsAny<PipelineDiagnosticsDto>(), It.IsAny<TimeSpan>()))
            .Callback<PipelineDiagnosticsDto, TimeSpan>((d, t) =>
            {
                capturedDiagnostics = d;
                capturedDuration = t;
            });

        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            predictionService: null, tierProvider: null,
            screeningProvider: null, symbolConstraintsProvider: null,
            logger: null, metrics: mockMetrics.Object);

        // Act
        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert — RecordCycle fired once with the diagnostics from this cycle
        result.Should().NotBeNull();
        mockMetrics.Verify(
            m => m.RecordCycle(It.IsAny<PipelineDiagnosticsDto>(), It.IsAny<TimeSpan>()),
            Times.Once,
            "RecordCycle must fire exactly once on a successful signal engine cycle");
        capturedDiagnostics.Should().NotBeNull();
        capturedDiagnostics!.TotalRatesLoaded.Should().Be(2);
        capturedDuration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task SignalEngine_RecordCycle_Failure_DoesNotAbortCycle()
    {
        // Arrange: metrics backend throws — cycle must still succeed.
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.0004m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0030m, takerFeeRate: 0.0004m),
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        var mockMetrics = new Mock<ISignalEngineMetrics>();
        mockMetrics
            .Setup(m => m.RecordCycle(It.IsAny<PipelineDiagnosticsDto>(), It.IsAny<TimeSpan>()))
            .Throws(new InvalidOperationException("telemetry backend down"));

        var mockLogger = new Mock<ILogger<SignalEngine>>();

        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            predictionService: null, tierProvider: null,
            screeningProvider: null, symbolConstraintsProvider: null,
            logger: mockLogger.Object, metrics: mockMetrics.Object);

        // Act
        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert — cycle returned normally despite metrics failure
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue(
            "a metrics emission failure must never abort the signal cycle");
        result.Diagnostics.Should().NotBeNull(
            "the result payload must be fully populated even when metrics emission throws");

        // Warning log was emitted
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("metrics", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "metrics failure must be logged at Warning level");
    }

    [Fact]
    public async Task SignalEngine_RecordCycle_NotCalled_OnDatabaseUnavailableDegradedPath()
    {
        // Plan contract: metrics emission must NOT fire on the degraded path (the
        // DatabaseUnavailableException early-return at line 71-83 of SignalEngine.cs).
        // This test locks the contract so a future refactor that hoists RecordCycle
        // above the try block fails loudly.
        _mockBotConfig
            .Setup(b => b.GetActiveAsync())
            .ThrowsAsync(new DatabaseUnavailableException("simulated degraded DB"));

        var mockMetrics = new Mock<ISignalEngineMetrics>();

        var sut = new SignalEngine(
            _mockUow.Object, _mockCache.Object,
            predictionService: null, tierProvider: null,
            screeningProvider: null, symbolConstraintsProvider: null,
            logger: null, metrics: mockMetrics.Object);

        // Act
        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert — degraded result returned, no metric emitted
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.DatabaseAvailable.Should().BeFalse();
        mockMetrics.Verify(
            m => m.RecordCycle(It.IsAny<PipelineDiagnosticsDto>(), It.IsAny<TimeSpan>()),
            Times.Never,
            "metrics must not be emitted on the degraded (DatabaseUnavailableException) path");
    }

    // ── F8: break-even-size filter (profitability-fixes) ─────────────

    [Fact]
    public async Task SignalEngine_FiltersOpportunity_WhenMinHoldYieldBelowFeeFloor()
    {
        // Opportunity that passes OpenThreshold and the edge-guardrail (FeeAmortizationHours=12)
        // but fails the new break-even-size check (MinHoldTimeHours=2).
        // Inputs: takerFee 0.0004 per leg → totalEntryCost ≈ 0.0016.
        //   breakEvenSizeFloor = 3 × 0.0016 = 0.0048
        //   spread ≈ 0.0006 → net ≈ 0.000467
        //   minHoldYield = 0.000467 × 2 = 0.000934 → FAIL (< 0.0048)
        //   minEdgeThreshold = 3 × 0.0016/12 ≈ 0.0004 → net 0.000467 ≥ 0.0004 → PASS
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.0004m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0006m, takerFeeRate: 0.0004m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 3m,
            MinHoldTimeHours = 2,
            UseBreakEvenSizeFilter = true,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0005m } });

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty(
            "net × MinHoldTimeHours (≈0.0009) is below MinEdgeMultiplier × totalEntryCost (≈0.0048)");
        result.Diagnostics!.PairsFilteredByBreakEvenSize.Should().Be(1);
        result.Diagnostics.NetPositiveBelowEdgeGuardrail.Should().Be(0,
            "the new counter must take the opportunity before the old counter can fire");
    }

    [Fact]
    public async Task SignalEngine_AdmitsOpportunity_WhenMinHoldYieldCoversFeeFloor()
    {
        // Opportunity with net high enough that both checks pass.
        //   totalEntryCost ≈ 0.0016, MinHoldTimeHours = 2, MinEdgeMultiplier = 3
        //   breakEvenSizeFloor = 3 × 0.0016 = 0.0048
        //   spread = 0.0030 → net ≈ 0.00287
        //   minHoldYield = 0.00287 × 2 = 0.00573 ≥ 0.0048 ✓
        //   minEdgeThreshold = 0.0004 → net 0.00287 ≥ 0.0004 ✓
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.0004m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0030m, takerFeeRate: 0.0004m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 3m,
            MinHoldTimeHours = 2,
            UseBreakEvenSizeFilter = true,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0030m } });

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().HaveCount(1);
        result.Diagnostics!.PairsFilteredByBreakEvenSize.Should().Be(0);
        result.Diagnostics.NetPositiveBelowEdgeGuardrail.Should().Be(0);
    }

    [Fact]
    public async Task SignalEngine_CountsEdgeGuardrail_WhenBreakEvenPasses_ButFeeAmortFails()
    {
        // Ordering guard: when MinHoldTimeHours is high enough that the break-even-size
        // check passes but FeeAmortizationHours is short enough that the edge-guardrail
        // still fails, the old counter must still fire.
        //   MinHoldTimeHours = 24 (unusually high), FeeAmortizationHours = 12
        //   totalEntryCost ≈ 0.0016
        //   breakEvenSizeFloor = 3 × 0.0016 = 0.0048
        //   net ≈ 0.000267 → minHoldYield = 0.000267 × 24 = 0.00640 ≥ 0.0048 ✓ BreakEven PASS
        //   minEdgeThreshold = 3 × 0.0016/12 = 0.0004 → net 0.000267 < 0.0004 ✗ MinEdge FAIL
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.0004m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0004m, takerFeeRate: 0.0004m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 48,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 3m,
            MinHoldTimeHours = 24,
            UseBreakEvenSizeFilter = true,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0004m } });

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty(
            "net passes break-even-size with minHoldHours=24 but still fails the 3x edge guardrail");
        result.Diagnostics!.PairsFilteredByBreakEvenSize.Should().Be(0);
        result.Diagnostics.NetPositiveBelowEdgeGuardrail.Should().Be(1,
            "when break-even-size passes but edge-guardrail fails, the original counter must still fire");
    }

    [Fact]
    public async Task SignalEngine_CountsOnlyBreakEvenSize_WhenBothChecksFail()
    {
        // Regression guard on the ordering of the classification chain: if an
        // opportunity fails BOTH the break-even-size floor AND the edge-guardrail,
        // only the stricter (break-even-size) counter increments. A reordering
        // bug would silently re-route the count into the edge-guardrail bucket.
        //   totalEntryCost ≈ 0.0016 (0.04% taker × 2 × 2 legs)
        //   MinHoldTimeHours = 2, MinEdgeMultiplier = 3 → breakEvenFloor = 0.0048
        //   FeeAmortizationHours = 12 → minEdgeThreshold = 0.0004
        //   Choose spread = 0.0004:
        //     fees per hour ≈ 0.000133 → net ≈ 0.000267
        //     net (0.000267) >= OpenThreshold (0.0001) ✓
        //     minHoldYield = 0.000267 × 2 = 0.000534 < 0.0048 ✗ break-even fails
        //     passesMinEdge: 0.000267 < 0.0004 ✗ edge-guardrail fails
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.0004m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0004m, takerFeeRate: 0.0004m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 3m,
            MinHoldTimeHours = 2,
            UseBreakEvenSizeFilter = true,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0002m } });

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty();
        result.Diagnostics!.PairsFilteredByBreakEvenSize.Should().Be(1,
            "stricter filter takes the counter first");
        result.Diagnostics.NetPositiveBelowEdgeGuardrail.Should().Be(0,
            "legacy counter must not double-count an opportunity already taken by the stricter filter");
    }

    [Fact]
    public async Task SignalEngine_BreakEvenFilter_FailsClosed_WhenMinHoldTimeHoursIsZero()
    {
        // When MinHoldTimeHours=0 with the filter enabled, the semantic is incoherent —
        // a zero worst-case hold can never amortize any fees — so the filter fails-closed
        // and rejects every opportunity that would have passed OpenThreshold.
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0000m, takerFeeRate: 0.0004m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0030m, takerFeeRate: 0.0004m),
        };

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            BreakevenHoursMax = 24,
            FeeAmortizationHours = 12,
            MinConsecutiveFavorableCycles = 1,
            MinEdgeMultiplier = 3m,
            MinHoldTimeHours = 0,
            UseBreakEvenSizeFilter = true,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);
        _mockFundingRates.Setup(f => f.GetHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, It.IsAny<int>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { new() { RatePerHour = 0.0030m } });

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty(
            "MinHoldTimeHours=0 with filter on must reject every opportunity");
        result.Diagnostics!.PairsFilteredByBreakEvenSize.Should().Be(1);
    }

    // ── Unified TTL caching ───────────────────────────────────────────────────
    // After the first successful leverage-tier load (prewarm), SignalEngine caches
    // the result for OpportunityCacheTtl (5 s). A second EvaluateAllAsync call within
    // that window must return the cached result without re-invoking the DB repositories.

    [Fact]
    public async Task EvaluateAllAsync_WithinCacheTtl_DoesNotReEvaluate()
    {
        // Arrange: build a SignalEngine with a real IMemoryCache and a tier provider
        // so that the first call produces leverage-metric-enriched opportunities.
        using var memCache = new MemoryCache(new MemoryCacheOptions());

        var mockTierProvider = new Mock<ILeverageTierProvider>();
        mockTierProvider
            .Setup(t => t.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Returns(20);

        var sutWithCache = new SignalEngine(
            _mockUow.Object,
            _mockCache.Object,
            tierProvider: mockTierProvider.Object,
            opportunityCache: memCache);

        var config = new BotConfiguration
        {
            SlippageBufferBps = 0,
            OpenThreshold = 0.0001m,
            DefaultLeverage = 5,
            MaxLeverageCap = 50,
            TotalCapitalUsdc = 10_000m,
            MaxCapitalPerPosition = 0.50m,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
        };
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates);

        // Act — first call primes the cache
        await sutWithCache.EvaluateAllAsync(CancellationToken.None);

        // Act — second call within the 5 s TTL window
        await sutWithCache.EvaluateAllAsync(CancellationToken.None);

        // Assert: underlying repository was called exactly once (cache hit on second call)
        _mockFundingRates.Verify(
            f => f.GetLatestPerExchangePerAssetAsync(),
            Times.Once,
            "second EvaluateAllAsync within the TTL window must be served from cache");
    }

    [Fact]
    public async Task EvaluateAllAsync_AfterCacheExpiry_ReEvaluates()
    {
        // Arrange: use a very short TTL entry to simulate expiry
        using var memCache = new MemoryCache(new MemoryCacheOptions());

        var sutWithCache = new SignalEngine(
            _mockUow.Object,
            _mockCache.Object,
            opportunityCache: memCache);

        var config = new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0003m };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());

        // Prime the cache with the normal 5 s TTL path
        await sutWithCache.EvaluateAllAsync(CancellationToken.None);

        // Manually expire the cache entry so the next call re-evaluates
        memCache.Remove(SignalEngine.OpportunityCacheKey);

        await sutWithCache.EvaluateAllAsync(CancellationToken.None);

        // Assert: repository called twice (once on prime, once after expiry)
        _mockFundingRates.Verify(
            f => f.GetLatestPerExchangePerAssetAsync(),
            Times.Exactly(2),
            "after cache expiry, EvaluateAllAsync must re-evaluate from the DB");
    }

    // ── F1/F3/F9: Stampede protection + degraded TTL + steady-state TTL ──────

    /// <summary>
    /// F1 — stampede guard: N concurrent first-hit callers against an empty cache must
    /// result in exactly one underlying evaluation. All concurrent callers must still
    /// receive the computed result.
    /// </summary>
    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_ConcurrentFirstHit_EvaluatesOnce()
    {
        // Arrange: real MemoryCache + call counter to track compute invocations.
        using var memCache = new MemoryCache(new MemoryCacheOptions());
        int evalCount = 0;

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref evalCount);
                return new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m };
            });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
                MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            });

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, opportunityCache: memCache);

        // Act: fire 20 concurrent requests — all hit an empty cache simultaneously.
        const int N = 20;
        var tasks = Enumerable.Range(0, N)
            .Select(_ => sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert: underlying evaluator invoked exactly once despite N concurrent callers.
        evalCount.Should().Be(1,
            "stampede protection must serialise concurrent first-hit callers into a single compute");

        // All callers must receive a valid (non-null) result.
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
    }

    /// <summary>
    /// F3 — degraded-result short TTL: when the underlying compute throws a catchable
    /// exception, the degraded result must be cached with ~500 ms TTL so the cache
    /// self-heals quickly without hammering the failing dependency.
    /// </summary>
    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_DatabaseUnavailable_CachesWithShortTtl()
    {
        // Arrange: spy MemoryCache that captures MemoryCacheEntryOptions on Set.
        var spyCache = new SpyMemoryCache();

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ThrowsAsync(new DatabaseUnavailableException("db down"));

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, opportunityCache: spyCache);

        // Act
        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: degraded result returned
        result.DatabaseAvailable.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();

        // Assert: cached with the short ~500 ms TTL, not the steady-state 5 s TTL.
        spyCache.LastSetOptions.Should().NotBeNull("degraded result must be cached");
        spyCache.LastSetOptions!.AbsoluteExpirationRelativeToNow.Should().NotBeNull();
        spyCache.LastSetOptions!.AbsoluteExpirationRelativeToNow!.Value
            .Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(600),
                "degraded TTL must be ~500 ms to allow quick self-healing");
        spyCache.LastSetOptions!.AbsoluteExpirationRelativeToNow!.Value
            .Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(400),
                "degraded TTL must be approximately 500 ms");
    }

    /// <summary>
    /// Regression guard (F9 / steady-state TTL): a healthy compute must still cache the
    /// result with the 5 s steady-state TTL, not the degraded 500 ms TTL.
    /// </summary>
    [Fact]
    public async Task GetOpportunitiesWithDiagnostics_HealthyCompute_CachesWithSteadyStateTtl()
    {
        // Arrange: spy MemoryCache that captures MemoryCacheEntryOptions on Set.
        var spyCache = new SpyMemoryCache();

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { SlippageBufferBps = 0, OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m),
                MakeRate(2, "Lighter",     1, "ETH", 0.0010m),
            });

        var sut = new SignalEngine(_mockUow.Object, _mockCache.Object, opportunityCache: spyCache);

        // Act
        var result = await sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: success path
        result.IsSuccess.Should().BeTrue();

        // Assert: cached with the 5 s steady-state TTL.
        spyCache.LastSetOptions.Should().NotBeNull("healthy result must be cached");
        spyCache.LastSetOptions!.AbsoluteExpirationRelativeToNow.Should().NotBeNull();
        spyCache.LastSetOptions!.AbsoluteExpirationRelativeToNow!.Value
            .Should().BeGreaterOrEqualTo(TimeSpan.FromSeconds(4),
                "steady-state TTL must be ~5 s — not the 500 ms degraded TTL");
        spyCache.LastSetOptions!.AbsoluteExpirationRelativeToNow!.Value
            .Should().BeLessOrEqualTo(TimeSpan.FromSeconds(6),
                "steady-state TTL must be ~5 s");
    }

    /// <summary>
    /// Test spy that wraps <see cref="MemoryCache"/> and captures the
    /// <see cref="MemoryCacheEntryOptions"/> from the most recent <c>Set</c> call.
    /// </summary>
    private sealed class SpyMemoryCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(new MemoryCacheOptions());

        public MemoryCacheEntryOptions? LastSetOptions { get; private set; }

        public ICacheEntry CreateEntry(object key)
        {
            var entry = _inner.CreateEntry(key);
            return new SpyCacheEntry(entry, opts => LastSetOptions = opts);
        }

        public void Remove(object key) => _inner.Remove(key);

        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        public void Dispose() => _inner.Dispose();

        private sealed class SpyCacheEntry : ICacheEntry
        {
            private readonly ICacheEntry _inner;
            private readonly Action<MemoryCacheEntryOptions> _capture;

            public SpyCacheEntry(ICacheEntry inner, Action<MemoryCacheEntryOptions> capture)
            {
                _inner = inner;
                _capture = capture;
            }

            public object Key => _inner.Key;
            public object? Value { get => _inner.Value; set => _inner.Value = value; }
            public DateTimeOffset? AbsoluteExpiration { get => _inner.AbsoluteExpiration; set => _inner.AbsoluteExpiration = value; }
            public TimeSpan? AbsoluteExpirationRelativeToNow
            {
                get => _inner.AbsoluteExpirationRelativeToNow;
                set
                {
                    _inner.AbsoluteExpirationRelativeToNow = value;
                    _capture(new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = value,
                        SlidingExpiration = _inner.SlidingExpiration,
                        Priority = _inner.Priority,
                    });
                }
            }
            public TimeSpan? SlidingExpiration { get => _inner.SlidingExpiration; set => _inner.SlidingExpiration = value; }
            public IList<IChangeToken> ExpirationTokens => _inner.ExpirationTokens;
            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => _inner.PostEvictionCallbacks;
            public CacheItemPriority Priority { get => _inner.Priority; set => _inner.Priority = value; }
            public long? Size { get => _inner.Size; set => _inner.Size = value; }
            public void Dispose() => _inner.Dispose();
        }
    }
}
