using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class ExchangeAnalyticsServiceTests
{
    private readonly Mock<ICoinGlassAnalyticsRepository> _mockAnalyticsRepo = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IExchangeRepository> _mockExchangeRepo = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRateRepo = new();
    private readonly Mock<IAssetRepository> _mockAssetRepo = new();
    private readonly ExchangeAnalyticsService _sut;

    public ExchangeAnalyticsServiceTests()
    {
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRateRepo.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssetRepo.Object);

        _sut = new ExchangeAnalyticsService(
            _mockAnalyticsRepo.Object,
            _mockUow.Object,
            NullLogger<ExchangeAnalyticsService>.Instance);
    }

    [Fact]
    public async Task GetTopOpportunities_ComputesSpreadCorrectly()
    {
        var now = DateTime.UtcNow;
        _mockAnalyticsRepo.Setup(r => r.GetLatestSnapshotPerExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CoinGlassExchangeRate>
            {
                new() { SourceExchange = "Binance", Symbol = "BTC", RatePerHour = 0.0001m, SnapshotTime = now },
                new() { SourceExchange = "OKX", Symbol = "BTC", RatePerHour = 0.0003m, SnapshotTime = now }
            });

        var result = await _sut.GetTopOpportunitiesAsync(count: 10, minSpreadPerHour: 0m);

        result.Should().HaveCount(1);
        result[0].SpreadPerHour.Should().Be(0.0002m); // abs(0.0003 - 0.0001)
        result[0].LongExchange.Should().Be("Binance"); // lower rate = long
        result[0].ShortExchange.Should().Be("OKX"); // higher rate = short
    }

    [Fact]
    public async Task GetTopOpportunities_ComputesNetYieldAfterFees()
    {
        var now = DateTime.UtcNow;
        // Use Hyperliquid (fee = 0.00045) and Lighter (fee = 0)
        _mockAnalyticsRepo.Setup(r => r.GetLatestSnapshotPerExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CoinGlassExchangeRate>
            {
                new() { SourceExchange = "Hyperliquid", Symbol = "ETH", RatePerHour = 0.0001m, SnapshotTime = now },
                new() { SourceExchange = "Lighter", Symbol = "ETH", RatePerHour = 0.0005m, SnapshotTime = now }
            });

        var result = await _sut.GetTopOpportunitiesAsync(count: 10, minSpreadPerHour: 0m);

        result.Should().HaveCount(1);
        var opp = result[0];
        opp.SpreadPerHour.Should().Be(0.0004m);
        // Fees: Hyperliquid = 0.00045/24, Lighter = 0/24
        var expectedFees = 0.00045m / 24m + 0m / 24m;
        opp.EstFeesPerHour.Should().Be(expectedFees);
        opp.NetYieldPerHour.Should().Be(0.0004m - expectedFees);
    }

    [Fact]
    public async Task GetTopOpportunities_SortsByNetYieldDescending()
    {
        var now = DateTime.UtcNow;
        _mockAnalyticsRepo.Setup(r => r.GetLatestSnapshotPerExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CoinGlassExchangeRate>
            {
                // Small spread pair
                new() { SourceExchange = "Binance", Symbol = "BTC", RatePerHour = 0.0001m, SnapshotTime = now },
                new() { SourceExchange = "OKX", Symbol = "BTC", RatePerHour = 0.0002m, SnapshotTime = now },
                // Large spread pair
                new() { SourceExchange = "Binance", Symbol = "ETH", RatePerHour = 0.0001m, SnapshotTime = now },
                new() { SourceExchange = "OKX", Symbol = "ETH", RatePerHour = 0.001m, SnapshotTime = now }
            });

        var result = await _sut.GetTopOpportunitiesAsync(count: 10, minSpreadPerHour: 0m);

        result.Should().HaveCountGreaterThanOrEqualTo(2);
        // ETH spread (0.0009) > BTC spread (0.0001) so ETH should be first
        result[0].Symbol.Should().Be("ETH");
        result[0].NetYieldPerHour.Should().BeGreaterThan(result[1].NetYieldPerHour);
    }

    [Fact]
    public async Task GetExchangeOverview_CorrectStatusBadges()
    {
        var now = DateTime.UtcNow;
        _mockAnalyticsRepo.Setup(r => r.GetLatestSnapshotPerExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CoinGlassExchangeRate>
            {
                new() { SourceExchange = "Hyperliquid", Symbol = "BTC", RatePerHour = 0.0001m, SnapshotTime = now },
                new() { SourceExchange = "Binance", Symbol = "BTC", RatePerHour = 0.0002m, SnapshotTime = now },
                new() { SourceExchange = "dYdX", Symbol = "BTC", RatePerHour = 0.00015m, SnapshotTime = now }
            });

        _mockExchangeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid", IsDataOnly = false, IsPlanned = false },
            new() { Id = 2, Name = "Binance", IsDataOnly = true, IsPlanned = true },
            new() { Id = 3, Name = "dYdX", IsDataOnly = true, IsPlanned = false }
        });

        var result = await _sut.GetExchangeOverviewAsync();

        result.Should().HaveCount(3);

        var hyperliquid = result.First(e => e.ExchangeName == "Hyperliquid");
        hyperliquid.StatusBadge.Should().Be("Active Connector");
        hyperliquid.HasDirectConnector.Should().BeTrue();

        var binance = result.First(e => e.ExchangeName == "Binance");
        binance.StatusBadge.Should().Be("Planned");
        binance.IsPlanned.Should().BeTrue();

        var dydx = result.First(e => e.ExchangeName == "dYdX");
        dydx.StatusBadge.Should().Be("Available");
    }

    [Fact]
    public async Task GetRateComparisons_FlagsDivergenceAbove10Pct()
    {
        var now = DateTime.UtcNow;

        // CoinGlass rate for Hyperliquid: 0.001
        _mockAnalyticsRepo.Setup(r => r.GetLatestSnapshotPerExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CoinGlassExchangeRate>
            {
                new() { SourceExchange = "Hyperliquid", Symbol = "BTC", RatePerHour = 0.001m, SnapshotTime = now }
            });

        // Direct connector rate for Hyperliquid: 0.0005 (50% divergence)
        _mockFundingRateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { ExchangeId = 1, AssetId = 1, RatePerHour = 0.0005m, RecordedAt = now }
            });

        _mockExchangeRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync(new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid", IsDataOnly = false }
        });

        _mockAssetRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync(new List<Asset>
        {
            new() { Id = 1, Symbol = "BTC" }
        });

        var result = await _sut.GetRateComparisonsAsync();

        result.Should().HaveCount(1);
        result[0].IsWarning.Should().BeTrue();
        result[0].DivergencePercent.Should().BeGreaterThan(10m);
        result[0].DirectRate.Should().Be(0.0005m);
        result[0].CoinGlassRate.Should().Be(0.001m);
    }
}
