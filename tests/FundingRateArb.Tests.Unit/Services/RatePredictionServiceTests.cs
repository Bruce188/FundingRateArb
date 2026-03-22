using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class RatePredictionServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRateRepo = new();
    private readonly Mock<IAssetRepository> _mockAssetRepo = new();
    private readonly Mock<IExchangeRepository> _mockExchangeRepo = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly RatePredictionService _sut;

    public RatePredictionServiceTests()
    {
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRateRepo.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssetRepo.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);

        _mockAssetRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync(new List<Asset>
        {
            new() { Id = 1, Symbol = "ETH" },
            new() { Id = 2, Symbol = "BTC" },
        });

        _mockExchangeRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync(new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid" },
            new() { Id = 2, Name = "Aster" },
        });

        _mockAssetRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Asset { Id = 1, Symbol = "ETH" });
        _mockAssetRepo.Setup(r => r.GetByIdAsync(2)).ReturnsAsync(new Asset { Id = 2, Symbol = "BTC" });
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Exchange { Id = 1, Name = "Hyperliquid" });
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(2)).ReturnsAsync(new Exchange { Id = 2, Name = "Aster" });

        _sut = new RatePredictionService(_mockUow.Object, _cache);
    }

    // ── EWMA Calculation ─────────────────────────────────────────

    [Fact]
    public void ComputeEwma_KnownSeries_ProducesExpectedValues()
    {
        // α = 0.3, series: [1, 2, 3, 4, 5]
        // ewma[0] = 1
        // ewma[1] = 0.3*2 + 0.7*1 = 1.3
        // ewma[2] = 0.3*3 + 0.7*1.3 = 1.81
        // ewma[3] = 0.3*4 + 0.7*1.81 = 2.467
        // ewma[4] = 0.3*5 + 0.7*2.467 = 3.2269
        var rates = new List<decimal> { 1m, 2m, 3m, 4m, 5m };

        var ewma = RatePredictionService.ComputeEwma(rates);

        ewma.Should().HaveCount(5);
        ewma[0].Should().Be(1m);
        ewma[1].Should().BeApproximately(1.3m, 0.001m);
        ewma[2].Should().BeApproximately(1.81m, 0.001m);
        ewma[3].Should().BeApproximately(2.467m, 0.001m);
        ewma[4].Should().BeApproximately(3.2269m, 0.001m);
    }

    [Fact]
    public async Task GetPredictionAsync_KnownSeries_ReturnsPredictedRate()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // 48 hours of linearly increasing rates
        for (int i = 0; i < 48; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-48 + i),
                AvgRatePerHour = 0.001m + i * 0.00001m,
                MinRate = 0.001m, MaxRate = 0.002m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetPredictionAsync(1, 1);

        result.Should().NotBeNull();
        result!.AssetSymbol.Should().Be("ETH");
        result.ExchangeName.Should().Be("Hyperliquid");
        result.PredictedRatePerHour.Should().BeGreaterThan(0);
    }

    // ── Trend Detection ──────────────────────────────────────────

    [Fact]
    public void ComputeTrend_RisingSeries_DetectedAsRising()
    {
        // EWMA values that increase by >15% over 12 points
        var ewma = new List<decimal>();
        for (int i = 0; i < 24; i++)
            ewma.Add(1m + i * 0.05m); // 1.0 to 2.15 — large increase

        var trend = RatePredictionService.ComputeTrend(ewma);

        trend.Should().Be("rising");
    }

    [Fact]
    public void ComputeTrend_FallingSeries_DetectedAsFalling()
    {
        var ewma = new List<decimal>();
        for (int i = 0; i < 24; i++)
            ewma.Add(2m - i * 0.05m); // 2.0 to 0.85 — large decrease

        var trend = RatePredictionService.ComputeTrend(ewma);

        trend.Should().Be("falling");
    }

    [Fact]
    public void ComputeTrend_StableSeries_DetectedAsStable()
    {
        var ewma = new List<decimal>();
        for (int i = 0; i < 24; i++)
            ewma.Add(1.0m); // constant

        var trend = RatePredictionService.ComputeTrend(ewma);

        trend.Should().Be("stable");
    }

    // ── Confidence ───────────────────────────────────────────────

    [Fact]
    public void ComputeConfidence_LowDataCount_LowConfidence()
    {
        // Only 12 hours (need 48 for full confidence)
        var rates = Enumerable.Range(0, 12).Select(i => 0.001m).ToList();

        var confidence = RatePredictionService.ComputeConfidence(rates);

        confidence.Should().BeLessThan(0.5m);
    }

    [Fact]
    public void ComputeConfidence_HighDataCount_StableRates_HighConfidence()
    {
        // 48 hours of very stable rates
        var rates = Enumerable.Range(0, 48).Select(i => 0.001m).ToList();

        var confidence = RatePredictionService.ComputeConfidence(rates);

        // stddev=0, absAvg=max(0.001, 0.0001)=0.001, volatilityPenalty=1-0=1
        // Count penalty = min(48/48, 1) = 1. Confidence = 1.0
        confidence.Should().Be(1.0m);
    }

    [Fact]
    public void ComputeConfidence_NearZeroAvgRate_DoesNotBlowUp()
    {
        // F8: Rates near zero should not cause confidence to blow up to zero
        // because of the absAvg floor at 0.0001
        var rates = Enumerable.Range(0, 48).Select(i => 0.00001m).ToList();

        var confidence = RatePredictionService.ComputeConfidence(rates);

        // absAvg = max(0.00001, 0.0001) = 0.0001, stddev = 0 (constant)
        // volatilityPenalty = 1 - 0/0.0001 = 1.0, confidence = 1.0
        confidence.Should().BeGreaterThan(0.5m);
    }

    // ── Insufficient Data ────────────────────────────────────────

    [Fact]
    public async Task GetPredictionAsync_InsufficientData_ReturnsNull()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // Only 10 hours (need 24 minimum)
        for (int i = 0; i < 10; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-10 + i),
                AvgRatePerHour = 0.001m,
                MinRate = 0.001m, MaxRate = 0.001m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetPredictionAsync(1, 1);

        result.Should().BeNull();
    }

    // ── Empty Data ───────────────────────────────────────────────

    [Fact]
    public async Task GetPredictionsAsync_EmptyData_ReturnsEmptyList()
    {
        SetupAggregates([]);

        var result = await _sut.GetPredictionsAsync();

        result.Should().BeEmpty();
    }

    // ── NB9: Boundary Tests for 24-Hour Prediction Minimum ──────

    [Fact]
    public void ComputePrediction_Exactly24Hours_ReturnsPrediction()
    {
        var now = DateTime.UtcNow;
        var aggregates = Enumerable.Range(0, 24).Select(i => new FundingRateHourlyAggregate
        {
            AssetId = 1, ExchangeId = 1,
            HourUtc = now.AddHours(-24 + i),
            AvgRatePerHour = 0.001m,
            MinRate = 0.001m, MaxRate = 0.001m,
            AvgVolume24hUsd = 1000000m, SampleCount = 10,
        }).ToList();

        var assetLookup = new Dictionary<int, string> { { 1, "ETH" } };
        var exchangeLookup = new Dictionary<int, string> { { 1, "Hyperliquid" } };

        var result = RatePredictionService.ComputePrediction(aggregates, 1, 1, assetLookup, exchangeLookup);

        result.Should().NotBeNull();
    }

    [Fact]
    public void ComputePrediction_23Hours_ReturnsNull()
    {
        var now = DateTime.UtcNow;
        var aggregates = Enumerable.Range(0, 23).Select(i => new FundingRateHourlyAggregate
        {
            AssetId = 1, ExchangeId = 1,
            HourUtc = now.AddHours(-23 + i),
            AvgRatePerHour = 0.001m,
            MinRate = 0.001m, MaxRate = 0.001m,
            AvgVolume24hUsd = 1000000m, SampleCount = 10,
        }).ToList();

        var assetLookup = new Dictionary<int, string> { { 1, "ETH" } };
        var exchangeLookup = new Dictionary<int, string> { { 1, "Hyperliquid" } };

        var result = RatePredictionService.ComputePrediction(aggregates, 1, 1, assetLookup, exchangeLookup);

        result.Should().BeNull();
    }

    // ── NB10: High-Volatility Confidence Test ─────────────────────

    [Fact]
    public void ComputeConfidence_HighVolatility_ReturnsZero()
    {
        // Alternating rates where stdDev > absAvg, driving confidence to 0
        var rates = Enumerable.Range(0, 48)
            .Select(i => i % 2 == 0 ? 0.01m : -0.01m)
            .ToList();

        var confidence = RatePredictionService.ComputeConfidence(rates);

        confidence.Should().Be(0m);
    }

    // ── Caching (F4) ─────────────────────────────────────────────

    [Fact]
    public async Task GetPredictionsAsync_SecondCall_ReturnsCachedResult()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();
        for (int i = 0; i < 48; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-48 + i),
                AvgRatePerHour = 0.001m,
                MinRate = 0.001m, MaxRate = 0.001m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result1 = await _sut.GetPredictionsAsync();
        var result2 = await _sut.GetPredictionsAsync();

        result1.Should().BeSameAs(result2);
        // Repository should only be called once due to caching
        _mockFundingRateRepo.Verify(
            r => r.GetHourlyAggregatesAsync(null, null, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── F12: ComputeTrend previousEwma==0 branch ────────────────

    [Fact]
    public void ComputeTrend_PreviousEwmaIsZero_PositiveCurrent_ReturnsRising()
    {
        // Build EWMA values where the compare point (-12h) is 0 and current is positive
        var ewma = new List<decimal>();
        for (int i = 0; i < 24; i++)
            ewma.Add(0m); // fill with zeros
        ewma[^1] = 0.001m; // current is positive

        var trend = RatePredictionService.ComputeTrend(ewma);

        trend.Should().Be("rising");
    }

    [Fact]
    public void ComputeTrend_PreviousEwmaIsZero_NegativeCurrent_ReturnsFalling()
    {
        var ewma = new List<decimal>();
        for (int i = 0; i < 24; i++)
            ewma.Add(0m);
        ewma[^1] = -0.001m; // current is negative

        var trend = RatePredictionService.ComputeTrend(ewma);

        trend.Should().Be("falling");
    }

    [Fact]
    public void ComputeTrend_PreviousEwmaIsZero_ZeroCurrent_ReturnsStable()
    {
        var ewma = new List<decimal>();
        for (int i = 0; i < 24; i++)
            ewma.Add(0m);

        var trend = RatePredictionService.ComputeTrend(ewma);

        trend.Should().Be("stable");
    }

    // ── F14: GetPredictionAsync cache hit ──────────────────────

    [Fact]
    public async Task GetPredictionAsync_CacheHit_ReturnsCachedPrediction()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();
        for (int i = 0; i < 48; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-48 + i),
                AvgRatePerHour = 0.001m,
                MinRate = 0.001m, MaxRate = 0.001m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        // Populate cache via bulk call
        await _sut.GetPredictionsAsync();

        // Single-pair call should hit cache, not DB
        var result = await _sut.GetPredictionAsync(1, 1);

        result.Should().NotBeNull();
        result!.AssetSymbol.Should().Be("ETH");
        // GetHourlyAggregatesAsync should only have been called once (the bulk call)
        _mockFundingRateRepo.Verify(
            r => r.GetHourlyAggregatesAsync(null, null, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void SetupAggregates(List<FundingRateHourlyAggregate> aggregates)
    {
        _mockFundingRateRepo.Setup(r => r.GetHourlyAggregatesAsync(
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int? assetId, int? exchangeId, DateTime from, DateTime to, CancellationToken _) =>
            {
                return aggregates
                    .Where(a => (!assetId.HasValue || a.AssetId == assetId.Value)
                             && (!exchangeId.HasValue || a.ExchangeId == exchangeId.Value)
                             && a.HourUtc >= from && a.HourUtc <= to)
                    .OrderBy(a => a.HourUtc)
                    .ToList();
            });
    }
}
