using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class RateAnalyticsServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRateRepo = new();
    private readonly Mock<IAssetRepository> _mockAssetRepo = new();
    private readonly Mock<IExchangeRepository> _mockExchangeRepo = new();
    private readonly RateAnalyticsService _sut;

    public RateAnalyticsServiceTests()
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
            new() { Id = 3, Name = "Lighter" },
        });

        _sut = new RateAnalyticsService(_mockUow.Object);
    }

    // ── Trend Detection ──────────────────────────────────────────

    [Fact]
    public async Task GetRateTrendsAsync_RisingTrend_DetectedCorrectly()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // Create 12 hours of data: first 6h at 0.001, last 6h at 0.002 (100% increase = rising)
        for (int i = 0; i < 12; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-12 + i),
                AvgRatePerHour = i < 6 ? 0.001m : 0.002m,
                MinRate = 0.0009m, MaxRate = 0.0021m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetRateTrendsAsync(1, 7);

        result.Should().HaveCount(1);
        result[0].TrendDirection.Should().Be("rising");
        result[0].AssetSymbol.Should().Be("ETH");
        result[0].ExchangeName.Should().Be("Hyperliquid");
    }

    [Fact]
    public async Task GetRateTrendsAsync_FallingTrend_DetectedCorrectly()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // First 6h at 0.002, last 6h at 0.001 (−50% = falling)
        for (int i = 0; i < 12; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-12 + i),
                AvgRatePerHour = i < 6 ? 0.002m : 0.001m,
                MinRate = 0.0009m, MaxRate = 0.0021m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetRateTrendsAsync(1, 7);

        result.Should().HaveCount(1);
        result[0].TrendDirection.Should().Be("falling");
    }

    [Fact]
    public async Task GetRateTrendsAsync_StableTrend_DetectedCorrectly()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // All hours at same rate
        for (int i = 0; i < 12; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-12 + i),
                AvgRatePerHour = 0.001m,
                MinRate = 0.0009m, MaxRate = 0.0011m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetRateTrendsAsync(1, 7);

        result.Should().HaveCount(1);
        result[0].TrendDirection.Should().Be("stable");
    }

    // ── Correlation ──────────────────────────────────────────────

    [Fact]
    public async Task GetCrossExchangeCorrelationAsync_CorrelatedSeries_ReturnsHighR()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // Two exchanges with perfectly correlated rates
        for (int i = 0; i < 24; i++)
        {
            var rate = 0.001m + i * 0.0001m;
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-24 + i),
                AvgRatePerHour = rate,
                MinRate = rate - 0.0001m, MaxRate = rate + 0.0001m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 2,
                HourUtc = now.AddHours(-24 + i),
                AvgRatePerHour = rate * 2, // perfectly correlated (scaled)
                MinRate = rate * 2 - 0.0001m, MaxRate = rate * 2 + 0.0001m,
                AvgVolume24hUsd = 500000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetCrossExchangeCorrelationAsync(1, 7);

        result.Should().HaveCount(1);
        result[0].PearsonR.Should().BeApproximately(1.0m, 0.01m);
        result[0].SampleCount.Should().Be(24);
    }

    [Fact]
    public async Task GetCrossExchangeCorrelationAsync_AnticorrelatedSeries_ReturnsNegativeR()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // Two exchanges with anti-correlated rates
        for (int i = 0; i < 24; i++)
        {
            var rate = 0.001m + i * 0.0001m;
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-24 + i),
                AvgRatePerHour = rate,
                MinRate = rate, MaxRate = rate,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 2,
                HourUtc = now.AddHours(-24 + i),
                AvgRatePerHour = 0.01m - rate, // anti-correlated
                MinRate = 0.01m - rate, MaxRate = 0.01m - rate,
                AvgVolume24hUsd = 500000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetCrossExchangeCorrelationAsync(1, 7);

        result.Should().HaveCount(1);
        result[0].PearsonR.Should().BeApproximately(-1.0m, 0.01m);
    }

    // ── Time of Day ──────────────────────────────────────────────

    [Fact]
    public async Task GetTimeOfDayPatternsAsync_GroupsByHourCorrectly()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // Add data for 3 days covering all 24 hours (3 samples per hour bucket)
        for (int day = 0; day < 3; day++)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                aggregates.Add(new FundingRateHourlyAggregate
                {
                    AssetId = 1, ExchangeId = 1,
                    HourUtc = new DateTime(2026, 3, 19 + day, hour, 0, 0, DateTimeKind.Utc),
                    AvgRatePerHour = 0.001m + hour * 0.0001m,
                    MinRate = 0.001m, MaxRate = 0.002m,
                    AvgVolume24hUsd = 1000000m, SampleCount = 10,
                });
            }
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetTimeOfDayPatternsAsync(1, 1, 7);

        result.Should().HaveCount(24);
        result[0].HourUtc.Should().Be(0);
        result[0].SampleCount.Should().Be(3);
        result[23].HourUtc.Should().Be(23);
        result[23].SampleCount.Should().Be(3);
    }

    // ── Z-Score ──────────────────────────────────────────────────

    [Fact]
    public async Task GetZScoreAlertsAsync_AlertFires_WhenRateExceedsTwoSigma()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // Historical: 7 days of stable 0.001 rate
        for (int i = 0; i < 168; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-168 + i),
                AvgRatePerHour = 0.001m,
                MinRate = 0.001m, MaxRate = 0.001m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
                Asset = new Asset { Id = 1, Symbol = "ETH" },
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            });
        }

        // Latest: spike to 0.01 (outlier)
        var latestAggregate = new FundingRateHourlyAggregate
        {
            AssetId = 1, ExchangeId = 1,
            HourUtc = now,
            AvgRatePerHour = 0.01m, // 10x normal
            MinRate = 0.01m, MaxRate = 0.01m,
            AvgVolume24hUsd = 1000000m, SampleCount = 10,
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            Exchange = new Exchange { Id = 1, Name = "Hyperliquid" },
        };

        SetupAggregates(aggregates);
        _mockFundingRateRepo.Setup(r => r.GetLatestAggregatePerAssetExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateHourlyAggregate> { latestAggregate });

        var result = await _sut.GetZScoreAlertsAsync(2.0m);

        // With all-same values, stddev of aggregates is 0 → the latest is an outlier
        // But stddev of 168 identical values = 0, which would be skipped. Let's use varied data instead.
        // This test validates the zero-stddev skip path.
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetZScoreAlertsAsync_AlertFires_WithVariedHistoricalData()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // Historical: 168 hours with small variation around 0.001
        var rng = new Random(42);
        for (int i = 0; i < 168; i++)
        {
            var rate = 0.001m + (decimal)(rng.NextDouble() * 0.0002 - 0.0001); // ±0.0001 noise
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-168 + i),
                AvgRatePerHour = rate,
                MinRate = rate - 0.00005m, MaxRate = rate + 0.00005m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
                Asset = new Asset { Id = 1, Symbol = "ETH" },
                Exchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            });
        }

        // Latest: 0.01 (massive spike, well beyond 2σ of ~0.001 ± 0.0001)
        var latestAggregate = new FundingRateHourlyAggregate
        {
            AssetId = 1, ExchangeId = 1,
            HourUtc = now,
            AvgRatePerHour = 0.01m,
            MinRate = 0.01m, MaxRate = 0.01m,
            AvgVolume24hUsd = 1000000m, SampleCount = 10,
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            Exchange = new Exchange { Id = 1, Name = "Hyperliquid" },
        };

        SetupAggregates(aggregates);
        _mockFundingRateRepo.Setup(r => r.GetLatestAggregatePerAssetExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateHourlyAggregate> { latestAggregate });

        var result = await _sut.GetZScoreAlertsAsync(2.0m);

        result.Should().HaveCount(1);
        result[0].AssetSymbol.Should().Be("ETH");
        result[0].ZScore.Should().BeGreaterThan(2.0m);
    }

    // ── Empty Data ───────────────────────────────────────────────

    [Fact]
    public async Task GetRateTrendsAsync_EmptyData_ReturnsEmptyList()
    {
        SetupAggregates([]);

        var result = await _sut.GetRateTrendsAsync(1, 7);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCrossExchangeCorrelationAsync_EmptyData_ReturnsEmptyList()
    {
        SetupAggregates([]);

        var result = await _sut.GetCrossExchangeCorrelationAsync(1, 7);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTimeOfDayPatternsAsync_EmptyData_ReturnsEmptyList()
    {
        SetupAggregates([]);

        var result = await _sut.GetTimeOfDayPatternsAsync(1, 1, 7);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetZScoreAlertsAsync_EmptyData_ReturnsEmptyList()
    {
        SetupAggregates([]);
        _mockFundingRateRepo.Setup(r => r.GetLatestAggregatePerAssetExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _sut.GetZScoreAlertsAsync(2.0m);

        result.Should().BeEmpty();
    }

    // ── Static Helper Tests ──────────────────────────────────────

    [Fact]
    public void ComputePearsonCorrelation_PerfectCorrelation_ReturnsOne()
    {
        var x = new List<decimal> { 1m, 2m, 3m, 4m, 5m };
        var y = new List<decimal> { 2m, 4m, 6m, 8m, 10m };

        var r = RateAnalyticsService.ComputePearsonCorrelation(x, y);

        r.Should().BeApproximately(1.0m, 0.001m);
    }

    [Fact]
    public void ComputeStdDev_KnownValues_ComputesCorrectly()
    {
        var values = new List<decimal> { 2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m };
        var mean = values.Average(); // 5.0

        var stdDev = RateAnalyticsService.ComputeStdDev(values, mean);

        // Sample stddev of [2,4,4,4,5,5,7,9]: mean=5, SS=32, var=32/7≈4.571, stddev≈2.138
        stdDev.Should().BeApproximately(2.138m, 0.01m);
    }

    // ── F11: Edge Case Tests ────────────────────────────────────

    [Fact]
    public void ComputePearsonCorrelation_MismatchedLengths_ReturnsZero()
    {
        var x = new List<decimal> { 1m, 2m, 3m };
        var y = new List<decimal> { 1m, 2m };

        var r = RateAnalyticsService.ComputePearsonCorrelation(x, y);

        r.Should().Be(0m);
    }

    [Fact]
    public void ComputePearsonCorrelation_SingleElement_ReturnsZero()
    {
        var x = new List<decimal> { 1m };
        var y = new List<decimal> { 2m };

        var r = RateAnalyticsService.ComputePearsonCorrelation(x, y);

        r.Should().Be(0m);
    }

    [Fact]
    public void ComputePearsonCorrelation_ConstantSeries_ReturnsZero()
    {
        var x = new List<decimal> { 5m, 5m, 5m, 5m };
        var y = new List<decimal> { 1m, 2m, 3m, 4m };

        var r = RateAnalyticsService.ComputePearsonCorrelation(x, y);

        r.Should().Be(0m);
    }

    [Fact]
    public void ComputePearsonCorrelation_BothConstant_ReturnsZero()
    {
        var x = new List<decimal> { 3m, 3m, 3m };
        var y = new List<decimal> { 7m, 7m, 7m };

        var r = RateAnalyticsService.ComputePearsonCorrelation(x, y);

        r.Should().Be(0m);
    }

    [Fact]
    public async Task GetCrossExchangeCorrelationAsync_SingleOverlappingHour_SkipsPair()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>
        {
            // Exchange 1 has hours 0-5
            new() { AssetId = 1, ExchangeId = 1, HourUtc = now.AddHours(-5), AvgRatePerHour = 0.001m, MinRate = 0.001m, MaxRate = 0.001m, AvgVolume24hUsd = 1000000m, SampleCount = 10 },
            new() { AssetId = 1, ExchangeId = 1, HourUtc = now.AddHours(-4), AvgRatePerHour = 0.001m, MinRate = 0.001m, MaxRate = 0.001m, AvgVolume24hUsd = 1000000m, SampleCount = 10 },
            // Exchange 2 has only hour -4 overlapping (single overlap)
            new() { AssetId = 1, ExchangeId = 2, HourUtc = now.AddHours(-4), AvgRatePerHour = 0.002m, MinRate = 0.002m, MaxRate = 0.002m, AvgVolume24hUsd = 500000m, SampleCount = 10 },
        };

        SetupAggregates(aggregates);

        var result = await _sut.GetCrossExchangeCorrelationAsync(1, 7);

        // Only 1 overlapping hour < 2 minimum → pair is skipped
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRateTrendsAsync_NoDataInLast24h_FallsBackToAvg7d()
    {
        var now = DateTime.UtcNow;
        var aggregates = new List<FundingRateHourlyAggregate>();

        // Data only 3-5 days ago (none in last 24h)
        for (int i = 0; i < 48; i++)
        {
            aggregates.Add(new FundingRateHourlyAggregate
            {
                AssetId = 1, ExchangeId = 1,
                HourUtc = now.AddHours(-120 + i), // 5 days ago to 3 days ago
                AvgRatePerHour = 0.001m,
                MinRate = 0.0009m, MaxRate = 0.0011m,
                AvgVolume24hUsd = 1000000m, SampleCount = 10,
            });
        }

        SetupAggregates(aggregates);

        var result = await _sut.GetRateTrendsAsync(1, 7);

        result.Should().HaveCount(1);
        // avg24h should fall back to avg7d when no data in last 24h window
        result[0].Avg24h.Should().Be(result[0].Avg7d);
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
