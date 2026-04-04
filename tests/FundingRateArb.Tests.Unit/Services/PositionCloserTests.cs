using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class PositionCloserTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IExchangeRepository> _mockExchanges = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly Mock<IConnectorLifecycleManager> _mockConnectorLifecycle = new();
    private readonly Mock<IPnlReconciliationService> _mockReconciliation = new();
    private readonly Mock<IExchangeConnector> _mockLongConnector = new();
    private readonly Mock<IExchangeConnector> _mockShortConnector = new();
    private readonly PositionCloser _sut;

    private static ArbitragePosition CreateTestPosition(
        PositionStatus status = PositionStatus.Open,
        bool isDryRun = false,
        bool longLegClosed = false,
        bool shortLegClosed = false) => new()
    {
        Id = 1,
        UserId = "user1",
        AssetId = 1,
        LongExchangeId = 1,
        ShortExchangeId = 2,
        SizeUsdc = 100m,
        Leverage = 5,
        LongEntryPrice = 3000m,
        ShortEntryPrice = 3001m,
        EntryFeesUsdc = 0.21m,
        AccumulatedFunding = 0.5m,
        Status = status,
        IsDryRun = isDryRun,
        LongLegClosed = longLegClosed,
        ShortLegClosed = shortLegClosed,
        LongFilledQuantity = 0.1m,
        ShortFilledQuantity = 0.1m,
        LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
        ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        Asset = new Asset { Id = 1, Symbol = "ETH" },
    };

    public PositionCloserTests()
    {
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _mockConnectorLifecycle
            .Setup(c => c.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter"))
            .ReturnsAsync((_mockLongConnector.Object, _mockShortConnector.Object, (string?)null));

        _sut = new PositionCloser(
            _mockUow.Object, _mockConnectorLifecycle.Object,
            _mockReconciliation.Object, NullLogger<PositionCloser>.Instance);
    }

    private static OrderResultDto SuccessCloseOrder(decimal price = 3005m, decimal qty = 0.1m) =>
        new() { Success = true, FilledPrice = price, FilledQuantity = qty };

    private static OrderResultDto FailCloseOrder(string error = "Exchange down") =>
        new() { Success = false, Error = error };

    // ── Both legs succeed ─────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_BothLegsSucceed_ComputesPnlAndSetsClosedStatus()
    {
        // Arrange
        // expectedQty = SizeUsdc * Leverage / avgEntryPrice = 100 * 5 / 3000.5 ≈ 0.16664
        // Use filledQuantity that passes the 95% threshold
        var position = CreateTestPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCloseOrder(3005m, 0.166m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCloseOrder(2995m, 0.166m));

        // Act
        await _sut.ClosePositionAsync("user1", position, CloseReason.Manual);

        // Assert
        position.Status.Should().Be(PositionStatus.Closed);
        position.CloseReason.Should().Be(CloseReason.Manual);
        position.RealizedPnl.Should().NotBeNull();
        position.ClosedAt.Should().NotBeNull();
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.PositionClosed)), Times.Once);
    }

    // ── One leg fails ─────────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_OneLegFails_StaysInClosingWithLegFlags()
    {
        // Arrange — long succeeds, short fails
        var position = CreateTestPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCloseOrder(3005m, 0.1m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailCloseOrder());

        // Act
        await _sut.ClosePositionAsync("user1", position, CloseReason.Manual);

        // Assert — stays in Closing, long leg marked as closed
        position.Status.Should().Be(PositionStatus.Closing);
        position.LongLegClosed.Should().BeTrue();
        position.ShortLegClosed.Should().BeFalse();
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.Type == AlertType.LegFailed
                && al.Severity == AlertSeverity.Critical)), Times.Once);
    }

    // ── Both legs fail ────────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_BothLegsFail_SetsEmergencyClosedStatus()
    {
        // Arrange
        var position = CreateTestPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailCloseOrder("Long exchange down"));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailCloseOrder("Short exchange down"));

        // Act
        await _sut.ClosePositionAsync("user1", position, CloseReason.Manual);

        // Assert
        position.Status.Should().Be(PositionStatus.EmergencyClosed);
        position.ClosedAt.Should().NotBeNull();
    }

    // ── Partial fill ──────────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_PartialFillBelow95Percent_StaysInClosing()
    {
        // Arrange — very small fills relative to expected
        var position = CreateTestPosition();
        // Expected qty = SizeUsdc * Leverage / avgEntryPrice = 100 * 5 / 3000.5 ≈ 0.16664
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 3005m, FilledQuantity = 0.01m });
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 2995m, FilledQuantity = 0.01m });

        // Act
        await _sut.ClosePositionAsync("user1", position, CloseReason.Manual);

        // Assert — partial fill detected, stays in Closing for retry
        position.Status.Should().Be(PositionStatus.Closing);
        position.Notes.Should().Contain("Partial close");
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.SpreadWarning)), Times.Once);
    }

    // ── Credential error ──────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_CredentialError_CreatesAlertAndReturns()
    {
        // Arrange — connector creation fails
        _mockConnectorLifecycle
            .Setup(c => c.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter"))
            .ReturnsAsync((null!, null!, "No credentials found for Hyperliquid"));

        var position = CreateTestPosition();

        // Act
        await _sut.ClosePositionAsync("user1", position, CloseReason.Manual);

        // Assert
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.Type == AlertType.LegFailed
                && al.Severity == AlertSeverity.Critical
                && al.Message!.Contains("Cannot close position"))), Times.Once);
    }

    // ── Both legs already closed ──────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_BothLegsAlreadyClosed_CallsFinalizeClosedPosition()
    {
        // Arrange — both legs already closed from prior retry
        var position = CreateTestPosition(
            status: PositionStatus.Closing,
            longLegClosed: true,
            shortLegClosed: true);

        // Act
        await _sut.ClosePositionAsync("user1", position, CloseReason.Manual);

        // Assert — finalized with approximate PnL
        position.Status.Should().Be(PositionStatus.Closed);
        position.RealizedPnl.Should().NotBeNull();
        position.ClosedAt.Should().NotBeNull();
        // No close orders dispatched
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Dry-run position ──────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_DryRunPosition_WrapsConnectors()
    {
        // Arrange
        var position = CreateTestPosition(isDryRun: true);
        var wrappedLong = new Mock<IExchangeConnector>();
        var wrappedShort = new Mock<IExchangeConnector>();

        _mockConnectorLifecycle
            .Setup(c => c.WrapForDryRun(_mockLongConnector.Object, _mockShortConnector.Object))
            .Returns((wrappedLong.Object, wrappedShort.Object));

        wrappedLong
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 3005m, FilledQuantity = 0m });
        wrappedShort
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 2995m, FilledQuantity = 0m });

        // Act
        await _sut.ClosePositionAsync("user1", position, CloseReason.Manual);

        // Assert — WrapForDryRun was called
        _mockConnectorLifecycle.Verify(
            c => c.WrapForDryRun(_mockLongConnector.Object, _mockShortConnector.Object),
            Times.Once);
    }

    // ── Reconciliation failure ────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_ReconciliationFailure_DoesNotBlockClose()
    {
        // Arrange — reconciliation throws
        var position = CreateTestPosition();
        _mockLongConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCloseOrder(3005m, 0.166m));
        _mockShortConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessCloseOrder(2995m, 0.166m));
        _mockReconciliation
            .Setup(r => r.ReconcileAsync(It.IsAny<ArbitragePosition>(), It.IsAny<string>(),
                It.IsAny<IExchangeConnector>(), It.IsAny<IExchangeConnector>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Reconciliation API failure"));

        // Act
        await _sut.ClosePositionAsync("user1", position, CloseReason.Manual);

        // Assert — position still closed despite reconciliation failure
        position.Status.Should().Be(PositionStatus.Closed);
        position.RealizedPnl.Should().NotBeNull();
    }
}
