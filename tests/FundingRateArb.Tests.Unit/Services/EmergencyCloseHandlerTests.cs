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

public class EmergencyCloseHandlerTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly EmergencyCloseHandler _sut;

    public EmergencyCloseHandlerTests()
    {
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _sut = new EmergencyCloseHandler(_mockUow.Object, NullLogger<EmergencyCloseHandler>.Instance);
    }

    // ── TryEmergencyCloseWithRetryAsync ───────────────────────────────────

    [Fact]
    public async Task TryEmergencyClose_ReturnsFalse_OnSuccessfulClose()
    {
        // Arrange — close succeeds on first attempt
        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true });

        // Act
        var result = await _sut.TryEmergencyCloseWithRetryAsync(
            mockConnector.Object, "ETH", Side.Long, "user1", CancellationToken.None);

        // Assert — returns false (position existed and was closed)
        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryEmergencyClose_ReturnsTrue_WhenNoPositionErrorDetected()
    {
        // Arrange — close returns "no open position" error
        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = "no open position found" });

        // Act
        var result = await _sut.TryEmergencyCloseWithRetryAsync(
            mockConnector.Object, "ETH", Side.Long, "user1", CancellationToken.None);

        // Assert — returns true (position never existed)
        result.Should().BeTrue();
    }

    [Fact]
    public async Task TryEmergencyClose_RetriesOnRetryableError()
    {
        // Arrange — first call returns retryable error, second succeeds
        var callCount = 0;
        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new OrderResultDto { Success = false, Error = "HTTP 429 rate limit" }
                    : new OrderResultDto { Success = true };
            });

        // Act
        var result = await _sut.TryEmergencyCloseWithRetryAsync(
            mockConnector.Object, "ETH", Side.Short, "user1", CancellationToken.None);

        // Assert — retried and eventually succeeded
        result.Should().BeFalse();
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task TryEmergencyClose_CreatesCriticalAlert_AfterMaxAttempts()
    {
        // Arrange — always returns non-retryable error
        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = "permanent failure" });

        // Act
        var result = await _sut.TryEmergencyCloseWithRetryAsync(
            mockConnector.Object, "ETH", Side.Long, "user1", CancellationToken.None);

        // Assert — alert created for permanent failure
        result.Should().BeFalse();
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.Severity == AlertSeverity.Critical
                && al.Type == AlertType.LegFailed
                && al.Message!.Contains("EMERGENCY CLOSE FAILED"))), Times.Once);
    }

    [Fact]
    public async Task TryEmergencyClose_RetriesOnException_CreatesAlertOnFinalFailure()
    {
        // Arrange — always throws
        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector
            .Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection reset"));

        // Act
        var result = await _sut.TryEmergencyCloseWithRetryAsync(
            mockConnector.Object, "ETH", Side.Long, "user1", CancellationToken.None);

        // Assert — retried all attempts (exception path retries unconditionally)
        result.Should().BeFalse();
        // Called 5 times (maxAttempts)
        mockConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Exactly(5));
        // Alert created on final failure
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(
            al => al.Severity == AlertSeverity.Critical
                && al.Message!.Contains("EMERGENCY CLOSE FAILED"))), Times.Once);
    }

    // ── SetEmergencyCloseFees ─────────────────────────────────────────────

    [Fact]
    public void SetEmergencyCloseFees_CalculatesFeesCorrectly()
    {
        // Arrange
        var position = new ArbitragePosition { UserId = "user1" };
        var legResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = 0.1m,
        };

        // Act
        EmergencyCloseHandler.SetEmergencyCloseFees(position, legResult, "Hyperliquid");

        // Assert — Hyperliquid taker fee is 0.00045 (0.045%)
        var notional = 3000m * 0.1m; // 300
        var feeRate = 0.00045m; // Hyperliquid
        var expectedFee = notional * feeRate;
        position.EntryFeesUsdc.Should().Be(expectedFee);
        position.ExitFeesUsdc.Should().Be(expectedFee);
    }

    [Fact]
    public void SetEmergencyCloseFees_SetsRealizedPnl_ToNegativeFeeTotal()
    {
        // Arrange
        var position = new ArbitragePosition { UserId = "user1" };
        var legResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = 0.1m,
        };

        // Act
        EmergencyCloseHandler.SetEmergencyCloseFees(position, legResult, "Hyperliquid");

        // Assert — RealizedPnl = -(entry + exit)
        position.RealizedPnl.Should().Be(-(position.EntryFeesUsdc + position.ExitFeesUsdc));
        position.RealizedPnl.Should().BeNegative();
    }
}
