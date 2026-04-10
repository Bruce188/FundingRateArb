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

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

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

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

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

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

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

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

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

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

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

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(0m);
        position.PnlDivergence.Should().BeNull(); // Cannot compute divergence vs zero
        position.ReconciledAt.Should().NotBeNull();
        _capturedAlerts.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenOnlyOneLegReportsPnl_StoresPartialButSkipsDivergence()
    {
        // Long connector returns PnL, short returns null (e.g. Lighter)
        // Partial PnL stored for debugging, but divergence NOT computed
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 7m);
        SetupConnectorPnl(_mockShortConnector, null);
        SetupConnectorFunding(_mockLongConnector, 3m);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(7m, "partial PnL stored for debugging");
        position.PnlDivergence.Should().BeNull("divergence requires both legs");
        position.ExchangeReportedFunding.Should().BeNull("funding requires both legs");
        position.ReconciledAt.Should().NotBeNull();
        _capturedAlerts.Should().BeEmpty("no divergence alert for partial data");
    }

    [Fact]
    public async Task Reconcile_WhenBothLegsReportPnl_ComputesDivergenceNormally()
    {
        // Both legs report → divergence computed as before
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 4m);
        SetupConnectorFunding(_mockLongConnector, 2m);
        SetupConnectorFunding(_mockShortConnector, 1.5m);

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(9m);
        position.PnlDivergence.Should().NotBeNull("both legs reported → divergence computed");
        position.ExchangeReportedFunding.Should().Be(3.5m);
    }

    [Fact]
    public async Task Reconcile_WhenOnlyOneLegReportsFunding_StoresNullFunding()
    {
        // Both legs report PnL but only one reports funding
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 5m);
        SetupConnectorFunding(_mockLongConnector, 3m);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(10m);
        position.ExchangeReportedFunding.Should().BeNull("funding requires both legs to report");
    }

    // ── B5: Null/empty symbol handling (N2 changed signature to accept assetSymbol) ──

    [Fact]
    public async Task Reconcile_WhenAssetSymbolIsNullOrEmpty_ReturnsEarlyWithoutSettingReconciledAt()
    {
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 5m);

        await _sut.ReconcileAsync(position, "", _mockLongConnector.Object, _mockShortConnector.Object);

        position.ReconciledAt.Should().BeNull();
        position.ExchangeReportedPnl.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_WhenAssetSymbolIsNull_ReturnsEarlyWithoutSettingReconciledAt()
    {
        var position = CreatePosition(realizedPnl: 10m);

        await _sut.ReconcileAsync(position, null!, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ReconciledAt.Should().BeNull();
        position.ExchangeReportedPnl.Should().BeNull();
    }

    // ── NB5: RealizedPnl null with exchange PnL available ──

    [Fact]
    public async Task Reconcile_WhenLocalPnlIsNull_StoresExchangePnlButSkipsDivergence()
    {
        var position = CreatePosition(realizedPnl: null);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 5m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(10m);
        position.PnlDivergence.Should().BeNull();
        position.ReconciledAt.Should().NotBeNull();
        _capturedAlerts.Should().BeEmpty();
    }

    // ── NB6: Boundary values at 5%/10% thresholds ──

    [Fact]
    public async Task Reconcile_WhenDivergenceExactly5Pct_NoAlertCreated()
    {
        // local=10.5, exchange=10.0 → divergence = (10.5-10)/10*100 = 5.0% exactly
        var position = CreatePosition(realizedPnl: 10.5m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 5m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.PnlDivergence.Should().Be(5m);
        _capturedAlerts.Should().BeEmpty("exactly 5% should not trigger alert (threshold is >5%)");
    }

    [Fact]
    public async Task Reconcile_WhenDivergenceExactly10Pct_CreatesWarningNotCritical()
    {
        // local=11.0, exchange=10.0 → divergence = (11-10)/10*100 = 10.0% exactly
        var position = CreatePosition(realizedPnl: 11m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 5m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.PnlDivergence.Should().Be(10m);
        _capturedAlerts.Should().HaveCount(1);
        _capturedAlerts[0].Severity.Should().Be(AlertSeverity.Warning,
            "exactly 10% should create Warning (threshold for Critical is >10%)");
    }

    // ── NB7: Negative divergence ──

    [Fact]
    public async Task Reconcile_WhenLocalPnlBelowExchange_CreatesAlertWithNegativeDivergence()
    {
        // local=5, exchange=10 → divergence = (5-10)/10*100 = -50% → Critical (|divergence| > 10%)
        var position = CreatePosition(realizedPnl: 5m);
        SetupConnectorPnl(_mockLongConnector, 5m);
        SetupConnectorPnl(_mockShortConnector, 5m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.PnlDivergence.Should().BeNegative();
        _capturedAlerts.Should().HaveCount(1);
        _capturedAlerts[0].Severity.Should().Be(AlertSeverity.Critical);
    }

    // ── B1: Near-zero PnL min threshold ──

    [Fact]
    public async Task Reconcile_WhenExchangePnlNearZero_SkipsDivergenceCalculation()
    {
        // Exchange PnL = 0.005 (below MinPnlForDivergence of 0.01), local PnL = 10
        // Without the threshold this would produce 199900% divergence
        var position = CreatePosition(realizedPnl: 10m);
        SetupConnectorPnl(_mockLongConnector, 0.003m);
        SetupConnectorPnl(_mockShortConnector, 0.002m);
        SetupConnectorFunding(_mockLongConnector, null);
        SetupConnectorFunding(_mockShortConnector, null);

        await _sut.ReconcileAsync(position, TestAsset.Symbol, _mockLongConnector.Object, _mockShortConnector.Object);

        position.ExchangeReportedPnl.Should().Be(0.005m);
        position.PnlDivergence.Should().BeNull("near-zero exchange PnL should skip divergence calculation");
        position.ReconciledAt.Should().NotBeNull();
        _capturedAlerts.Should().BeEmpty();
    }
}
