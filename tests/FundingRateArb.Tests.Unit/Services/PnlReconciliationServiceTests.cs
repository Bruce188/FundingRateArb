using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class PnlReconciliationServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly Mock<IExchangeConnector> _mockLongConnector = new();
    private readonly Mock<IExchangeConnector> _mockShortConnector = new();
    private readonly PnlReconciliationService _sut;
    private readonly List<Alert> _capturedAlerts = [];

    private static readonly Asset TestAsset = new() { Id = 1, Symbol = "ETH" };

    public PnlReconciliationServiceTests()
    {
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockAssets.Setup(a => a.GetByIdAsync(TestAsset.Id)).ReturnsAsync(TestAsset);

        _mockAlerts
            .Setup(a => a.Add(It.IsAny<Alert>()))
            .Callback<Alert>(alert => _capturedAlerts.Add(alert));

        _sut = new PnlReconciliationService(
            _mockUow.Object,
            NullLogger<PnlReconciliationService>.Instance);
    }

    private static ArbitragePosition CreatePosition(
        decimal? realizedPnl = 10m,
        decimal accumulatedFunding = 5m,
        decimal entryFees = 1m,
        decimal exitFees = 0.5m)
    {
        return new ArbitragePosition
        {
            Id = 42,
            UserId = "test-user",
            AssetId = TestAsset.Id,
            Asset = TestAsset,
            RealizedPnl = realizedPnl,
            AccumulatedFunding = accumulatedFunding,
            EntryFeesUsdc = entryFees,
            ExitFeesUsdc = exitFees,
            OpenedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private void SetupConnectorPnl(Mock<IExchangeConnector> connector, decimal? pnl)
    {
        connector
            .Setup(c => c.GetRealizedPnlAsync(
                It.IsAny<string>(), It.IsAny<Side>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(pnl);
    }

    private void SetupConnectorFunding(Mock<IExchangeConnector> connector, decimal? funding)
    {
        connector
            .Setup(c => c.GetFundingPaymentsAsync(
                It.IsAny<string>(), It.IsAny<Side>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(funding);
    }

    [Fact]
    public async Task Reconcile_WhenExchangePnlWithinTolerance_NoAlertCreated()
    {
        // Local PnL = 10, exchange PnL = 9.8 → divergence ~2% (within 5%)
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 4.8m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(9.8m);
        position.PnlDivergence.Should().NotBeNull();
        Math.Abs(position.PnlDivergence!.Value).Should().BeLessThan(5m);
        position.ReconciledAt.Should().NotBeNull();
        _capturedAlerts.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenDivergenceAbove5Pct_CreatesWarningAlert()
    {
        // Local PnL = 10, exchange PnL = 9.3 → divergence ~7.5% (>5%, <10%)
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 4.3m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(9.3m);
        position.PnlDivergence.Should().NotBeNull();
        Math.Abs(position.PnlDivergence!.Value).Should().BeGreaterThan(5m);
        Math.Abs(position.PnlDivergence!.Value).Should().BeLessThanOrEqualTo(10m);

        _capturedAlerts.Should().HaveCount(1);
        _capturedAlerts[0].Type.Should().Be(AlertType.PnlDivergence);
        _capturedAlerts[0].Severity.Should().Be(AlertSeverity.Warning);
    }

    [Fact]
    public async Task Reconcile_WhenDivergenceAbove10Pct_CreatesCriticalAlert()
    {
        // Local PnL = 10, exchange PnL = 8.5 → divergence ~17.6% (>10%)
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 3.5m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(8.5m);
        position.PnlDivergence.Should().NotBeNull();
        Math.Abs(position.PnlDivergence!.Value).Should().BeGreaterThan(10m);

        _capturedAlerts.Should().HaveCount(1);
        _capturedAlerts[0].Type.Should().Be(AlertType.PnlDivergence);
        _capturedAlerts[0].Severity.Should().Be(AlertSeverity.Critical);
    }

    [Fact]
    public async Task Reconcile_WhenConnectorReturnsNull_SkipsReconciliation()
    {
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, null);
        SetupConnectorPnl(_mockShortConnector, null);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().BeNull();
        position.PnlDivergence.Should().BeNull();
        position.ReconciledAt.Should().NotBeNull();
        _capturedAlerts.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenFundingDiverges_SetsExchangeReportedFunding()
    {
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 5m);
        SetupConnectorFunding(_mockLongConnector, 3m);
        SetupConnectorFunding(_mockShortConnector, 1.5m);

        await _sut.ReconcileAsync(position, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedFunding.Should().Be(4.5m);
        position.ReconciledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconcile_WhenExchangePnlIsZero_HandlesGracefully()
    {
        // Exchange PnL = 0 — division by zero guard
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 0m);
        SetupConnectorPnl(_mockShortConnector, 0m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(0m);
        position.PnlDivergence.Should().BeNull(); // Cannot compute divergence vs zero
        position.ReconciledAt.Should().NotBeNull();
        _capturedAlerts.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenOnlyOneLegReportsData_UsesAvailableData()
    {
        // Long connector returns PnL, short returns null → uses partial data
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 7m);
        SetupConnectorPnl(_mockShortConnector, null);
        SetupConnectorFunding(_mockLongConnector, 3m);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, _mockLongConnector.Object, _mockShortConnector.Object);

        // Partial data: only long leg's PnL available (null treated as 0)
        position.ExchangeReportedPnl.Should().Be(7m);
        position.ExchangeReportedFunding.Should().Be(3m);
        position.ReconciledAt.Should().NotBeNull();
    }
}
