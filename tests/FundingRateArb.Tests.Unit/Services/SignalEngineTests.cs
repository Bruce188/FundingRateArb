using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FluentAssertions;
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0003m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0003m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0003m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0003m, FeeAmortizationHours = 24 });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0003m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread    = 0.0009m;
        var expectedFeePerHour = (0.0002m * 2 + 0.0001m * 2) / 24m;
        var expectedNet       = expectedSpread - expectedFeePerHour;

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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread    = 0.0009m;
        var expectedFeePerHour = (0.00090m + 0.00000m) / 24m;
        var expectedNet       = expectedSpread - expectedFeePerHour;

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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread    = 0.0009m;
        var expectedFeePerHour = (0.0003m * 2 + 0.00000m) / 24m;
        var expectedNet       = expectedSpread - expectedFeePerHour;

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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0000001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, FeeAmortizationHours = 48 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread     = 0.0009m;
        var expectedFeePerHour = (0.00090m + 0.00000m) / 48m;
        var expectedNet        = expectedSpread - expectedFeePerHour;

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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, FeeAmortizationHours = 24, MaxHoldTimeHours = 72 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var expectedSpread     = 0.0009m;
        var expectedFeePerHour = (0.00090m + 0.00000m) / 24m;
        var expectedNet        = expectedSpread - expectedFeePerHour;

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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, RateStalenessMinutes = 15 });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, RateStalenessMinutes = 15 });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, MinVolume24hUsdc = 50_000m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, MinVolume24hUsdc = 50_000m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0003m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics.TotalRatesLoaded.Should().Be(0);
        result.Diagnostics.PairsPassing.Should().Be(0);
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, RateStalenessMinutes = 15 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics.TotalRatesLoaded.Should().Be(2);
        result.Diagnostics.RatesAfterStalenessFilter.Should().Be(0);
        result.Diagnostics.PairsPassing.Should().Be(0);
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics.TotalPairsEvaluated.Should().Be(1);
        result.Diagnostics.PairsFilteredByVolume.Should().Be(1);
        result.Diagnostics.PairsPassing.Should().Be(0);
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Net yield is positive but below threshold — goes to NetPositiveBelowThreshold
        (result.Diagnostics.PairsFilteredByThreshold + result.Diagnostics.NetPositiveBelowThreshold).Should().BeGreaterThan(0);
        result.Diagnostics.BestRawSpread.Should().BeGreaterThan(0);
        result.Diagnostics.PairsPassing.Should().Be(0);
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Diagnostics.TotalRatesLoaded.Should().Be(2);
        result.Diagnostics.RatesAfterStalenessFilter.Should().Be(2);
        result.Diagnostics.TotalPairsEvaluated.Should().Be(1);
        result.Diagnostics.PairsFilteredByVolume.Should().Be(0);
        result.Diagnostics.PairsPassing.Should().Be(1);
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

        result.Diagnostics.StalenessMinutes.Should().Be(20);
        result.Diagnostics.MinVolumeThreshold.Should().Be(75_000m);
        result.Diagnostics.OpenThreshold.Should().Be(0.0005m);
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty();
        result.AllNetPositive.Should().HaveCount(1);
        result.AllNetPositive[0].NetYieldPerHour.Should().BeGreaterThan(0);
        result.Diagnostics.NetPositiveBelowThreshold.Should().Be(1);
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0003m, MinVolume24hUsdc = 50_000m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.001m, MinVolume24hUsdc = 50_000m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        result.Opportunities.Should().BeEmpty();
        result.AllNetPositive.Should().BeEmpty();
        result.Diagnostics.PairsFilteredByThreshold.Should().BeGreaterThan(0);
    }

    // ── Funding window boost tests ─────────────────────────────────────────

    [Fact]
    public async Task GetOpportunities_WithinFundingWindow_BoostsNetYield()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, FundingWindowMinutes = 10, FeeAmortizationHours = 24 });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Settlement in 5 minutes (within window of 10)
        var settlementTime = DateTime.UtcNow.AddMinutes(5);
        _mockCache.Setup(c => c.GetNextSettlement("Hyperliquid", "ETH")).Returns(settlementTime);
        _mockCache.Setup(c => c.GetNextSettlement("Lighter", "ETH")).Returns((DateTime?)null);

        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        var opp = result.Opportunities.Should().HaveCount(1).And.Subject.First();

        // Net yield should be boosted by 20%
        var spread = 0.0009m;
        var feePerHour = (0.00090m + 0.00000m) / 24m;
        var rawNet = spread - feePerHour;
        var boostedNet = rawNet * 1.2m;

        opp.NetYieldPerHour.Should().BeApproximately(boostedNet, 0.0000001m);
        // AnnualizedYield should use original net (not boosted)
        opp.AnnualizedYield.Should().BeApproximately(rawNet * 24m * 365m, 0.001m);
    }

    [Fact]
    public async Task GetOpportunities_OutsideFundingWindow_NoBoost()
    {
        var rates = new List<FundingRateSnapshot>
        {
            MakeRate(1, "Hyperliquid", 1, "ETH", 0.0001m, volume: 100_000m),
            MakeRate(2, "Lighter",     1, "ETH", 0.0010m, volume: 100_000m),
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, FundingWindowMinutes = 10, FeeAmortizationHours = 24 });
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

        // No boost — NetYieldPerHour equals raw net
        opp.NetYieldPerHour.Should().BeApproximately(rawNet, 0.0000001m);
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m, FundingWindowMinutes = 10, FeeAmortizationHours = 24 });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.01m, MinVolume24hUsdc = 50_000m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(rates);

        // Act — should not throw
        var result = await _sut.GetOpportunitiesWithDiagnosticsAsync(CancellationToken.None);

        // Assert: no cross-exchange pairs with only 1 exchange
        result.Opportunities.Should().BeEmpty(
            "cannot form cross-exchange pairs when only 1 exchange has data");

        // Diagnostics should show rates were loaded but 0 pairs evaluated
        // (each asset group has only 1 rate, so inner loop never runs)
        result.Diagnostics.TotalRatesLoaded.Should().Be(2);
        result.Diagnostics.TotalPairsEvaluated.Should().Be(0);
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
            .ReturnsAsync(new BotConfiguration { OpenThreshold = 0.0001m });
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
}
