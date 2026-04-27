using System.Text.Json;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FundingRateArb.Tests.Unit.Services;

public class ExchangeReconciliationServiceTests
{
    private const string HyperliquidName = "Hyperliquid";

    // ---------------------------------------------------------------------------
    // Shared fixture builder
    // ---------------------------------------------------------------------------

    private static (
        ExchangeReconciliationService Sut,
        Mock<IUnitOfWork> Uow,
        Mock<IPositionRepository> Positions,
        Mock<IFundingRateRepository> FundingRates,
        Mock<IUserConfigurationRepository> UserConfigs,
        Mock<IExchangeConnector> HlConnector,
        Mock<IExchangeConnectorFactory> Factory,
        Mock<IBalanceAggregator> Balance)
    BuildSut()
    {
        var mockUow = new Mock<IUnitOfWork>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockFundingRates = new Mock<IFundingRateRepository>();
        var mockAlerts = new Mock<IAlertRepository>();
        var mockUserConfigs = new Mock<IUserConfigurationRepository>();
        var mockReconcReports = new Mock<IReconciliationReportRepository>();

        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.FundingRates).Returns(mockFundingRates.Object);
        mockUow.Setup(u => u.Alerts).Returns(mockAlerts.Object);
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigs.Object);
        mockUow.Setup(u => u.ReconciliationReports).Returns(mockReconcReports.Object);

        // Default: empty open/closed positions, no phantom fee rows, no user IDs.
        mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Open)).ReturnsAsync([]);
        mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>())).ReturnsAsync([]);
        mockPositions.Setup(p => p.CountPhantomFeeRowsSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync([]);
        mockUserConfigs.Setup(u => u.GetAllEnabledUserIdsAsync()).ReturnsAsync([]);

        var mockHl = new Mock<IExchangeConnector>();
        mockHl.Setup(c => c.ExchangeName).Returns(HyperliquidName);
        mockHl.Setup(c => c.HasCredentials).Returns(true);
        mockHl.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockHl.Setup(c => c.GetAllOpenPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<(string, Side, decimal)>?)null);
        mockHl.Setup(c => c.GetCommissionIncomeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync((decimal?)null);

        var mockFactory = new Mock<IExchangeConnectorFactory>();
        mockFactory.Setup(f => f.GetAllConnectors()).Returns([mockHl.Object]);

        var mockBalance = new Mock<IBalanceAggregator>();
        mockBalance.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { Balances = [] });

        var sut = new ExchangeReconciliationService(
            mockFactory.Object,
            mockUow.Object,
            mockBalance.Object,
            NullLogger<ExchangeReconciliationService>.Instance);

        return (sut, mockUow, mockPositions, mockFundingRates, mockUserConfigs, mockHl, mockFactory, mockBalance);
    }

    // ---------------------------------------------------------------------------
    // (a) Happy path — all-green
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunReconciliation_HappyPath_ReturnsHealthyStatusAndNoAnomalies()
    {
        var (sut, _, _, _, _, _, _, _) = BuildSut();

        var result = await sut.RunReconciliationAsync();

        result.Report.OverallStatus.Should().Be("Healthy");
        result.Report.FreshRateMismatchCount.Should().Be(0);
        result.Report.OrphanPositionCount.Should().Be(0);
        result.Report.PhantomFeeRowCount24h.Should().Be(0);
        result.Report.FeeDeltaOutsideToleranceCount.Should().Be(0);
        result.AnomalyDescriptions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------
    // (b) Funding rate mismatch — DB rate 0.0008 vs live 0.0011 → ratio ~0.727
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunReconciliation_StaleFundingRate_FlagsFreshRateMismatch()
    {
        var (sut, _, mockPositions, mockFundingRates, _, mockHl, _, _) = BuildSut();

        var asset = new Asset { Id = 1, Symbol = "BTC" };
        var exchange = new Exchange { Id = 1, Name = HyperliquidName };

        // One open position on Hyperliquid with BTC so the asset is considered "active".
        var openPosition = new ArbitragePosition
        {
            Id = 1,
            Asset = asset,
            AssetId = asset.Id,
            LongExchange = exchange,
            LongExchangeId = exchange.Id,
        };
        mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Open)).ReturnsAsync([openPosition]);

        // DB snapshot: BTC/Hyperliquid with RatePerHour = 0.0008, fresh within 5 min.
        var dbSnapshot = new FundingRateSnapshot
        {
            Exchange = exchange,
            ExchangeId = exchange.Id,
            Asset = asset,
            AssetId = asset.Id,
            RatePerHour = 0.0008m,
            RecordedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync()).ReturnsAsync([dbSnapshot]);

        // Live rate from exchange: BTC at 0.0011.
        mockHl.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FundingRateDto { Symbol = "BTC", RatePerHour = 0.0011m }]);

        var result = await sut.RunReconciliationAsync();

        result.Report.FreshRateMismatchCount.Should().Be(1);
        result.AnomalyDescriptions.Should().ContainMatch("*ratio*");
    }

    // ---------------------------------------------------------------------------
    // (c) Orphan position — exchange says BTC/Long open, DB has none
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunReconciliation_OrphanPositionOnExchange_FlagsOrphanPosition()
    {
        var (sut, _, mockPositions, _, _, mockHl, _, _) = BuildSut();

        // DB has no open positions for this exchange.
        mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Open)).ReturnsAsync([]);

        // Exchange reports BTC/Long open.
        IReadOnlyList<(string Asset, Side Side, decimal Size)> exchangePositions =
        [
            ("BTC", Side.Long, 1.0m)
        ];
        mockHl.Setup(c => c.GetAllOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(exchangePositions);

        var result = await sut.RunReconciliationAsync();

        result.Report.OrphanPositionCount.Should().Be(1);
        result.AnomalyDescriptions.Should().ContainMatch("*Orphan*BTC*Hyperliquid*");
    }

    // ---------------------------------------------------------------------------
    // (d) Phantom fee rows — CountPhantomFeeRowsSinceAsync returns 1
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunReconciliation_PhantomFeeRowsExist_FlagsPhantomFeeCount()
    {
        var (sut, _, mockPositions, _, _, _, _, _) = BuildSut();

        mockPositions.Setup(p => p.CountPhantomFeeRowsSinceAsync(
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await sut.RunReconciliationAsync();

        result.Report.PhantomFeeRowCount24h.Should().Be(1);
        result.AnomalyDescriptions.Should().ContainMatch("*Phantom*");
    }

    // ---------------------------------------------------------------------------
    // (e) Connector throws on GetCommissionIncomeAsync — degrades gracefully
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunReconciliation_ConnectorThrows_DegradesGracefullyWithoutThrowing()
    {
        var (sut, _, _, _, _, mockHl, _, _) = BuildSut();

        mockHl.Setup(c => c.GetCommissionIncomeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("forced for test"));

        var act = async () => await sut.RunReconciliationAsync();
        await act.Should().NotThrowAsync();

        var result = await sut.RunReconciliationAsync();

        result.Report.OverallStatus.Should().Be("Degraded");
        var degraded = JsonSerializer.Deserialize<List<string>>(result.Report.DegradedExchangesJson)!;
        degraded.Should().Contain(HyperliquidName);
    }
}
