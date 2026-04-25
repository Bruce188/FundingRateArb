using FluentAssertions;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="EmergencyCloseHandler.SetEmergencyCloseFees"/> focusing on
/// the zero-fill guard: when <c>successfulLeg.FilledQuantity == 0</c> the method must
/// return early and leave <em>all</em> fee and PnL fields at zero — never write phantom
/// values produced from a zero-notional calculation.
/// </summary>
public class EmergencyCloseHandlerTestsZeroFill
{
    // ── SetEmergencyCloseFees: zero-fill early-return guard ──────────────────

    /// <summary>
    /// When FilledQuantity is zero the method must return early.
    /// EntryFeesUsdc and ExitFeesUsdc must remain 0 (their default),
    /// and RealizedPnl must be explicitly set to 0m (not left as null),
    /// so that callers can distinguish "fees computed" from "early-return / no trade".
    /// </summary>
    [Fact]
    public void SetEmergencyCloseFees_WhenFilledQuantityIsZero_FeesAndPnlAreZero()
    {
        // Arrange
        var position = new ArbitragePosition { UserId = "user1" };
        var legResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = 0m,   // zero fill — no trade took place
        };

        // Act
        EmergencyCloseHandler.SetEmergencyCloseFees(position, legResult, "Hyperliquid");

        // Assert — zero-fill must produce zero fees and a zero (not null) PnL
        position.EntryFeesUsdc.Should().Be(0m,
            "no entry fee can be incurred when nothing was filled");
        position.ExitFeesUsdc.Should().Be(0m,
            "no exit fee can be incurred when nothing was filled");
        position.RealizedPnl.Should().Be(0m,
            "RealizedPnl must be 0 (not null) when the zero-fill guard fires — " +
            "null is ambiguous and allows downstream callers to silently skip PnL accounting");
    }

    /// <summary>
    /// Confirms that a position which already carries stale fee values from a previous
    /// operation has those fees cleared to zero when SetEmergencyCloseFees is called
    /// with a zero-fill result.  The guard must not merely skip writing — it must
    /// actively zero out any pre-existing phantom values.
    /// </summary>
    [Fact]
    public void SetEmergencyCloseFees_WhenFilledQuantityIsZero_ClearsPreExistingFees()
    {
        // Arrange — position with stale/phantom fees already set
        var position = new ArbitragePosition
        {
            UserId = "user1",
            EntryFeesUsdc = 5m,
            ExitFeesUsdc = 3m,
            RealizedPnl = -8m,
        };
        var legResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = 0m,
        };

        // Act
        EmergencyCloseHandler.SetEmergencyCloseFees(position, legResult, "Hyperliquid");

        // Assert — stale values must be cleared, not preserved
        position.EntryFeesUsdc.Should().Be(0m,
            "a zero-fill means the previous EntryFeesUsdc was a phantom and must be cleared");
        position.ExitFeesUsdc.Should().Be(0m,
            "a zero-fill means the previous ExitFeesUsdc was a phantom and must be cleared");
        position.RealizedPnl.Should().Be(0m,
            "phantom fees must not inflate negative PnL — position effectively never traded");
    }

    // ── NB13: negative-quantity test ─────────────────────────────────────────

    /// <summary>
    /// When FilledQuantity is negative (pathological connector response), the method must
    /// zero out all fee and PnL fields and log a single Error-level message.
    /// </summary>
    [Fact]
    public void SetEmergencyCloseFees_WhenFilledQuantityIsNegative_ZeroesFeesAndPnl_AndLogsError()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<EmergencyCloseHandler>>();
        var position = new ArbitragePosition { Id = 42, UserId = "user1" };
        var legResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = -0.05m,
        };

        // Act
        EmergencyCloseHandler.SetEmergencyCloseFees(position, legResult, "Hyperliquid", mockLogger.Object);

        // Assert — negative fill treated the same as zero (coerced to zero)
        position.EntryFeesUsdc.Should().Be(0m, "negative fill quantity is coerced to zero");
        position.ExitFeesUsdc.Should().Be(0m, "negative fill quantity is coerced to zero");
        position.RealizedPnl.Should().Be(0m, "negative fill quantity is coerced to zero");

        // Verify Error log was emitted once
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "a negative FilledQuantity must be logged at Error level as a connector parsing bug");
    }

    // ── NB14: unknown-exchange fee-rate fallback contract ────────────────────

    /// <summary>
    /// Pins the fallback fee rate contract when an unknown exchange name is passed.
    /// If ExchangeFeeConstants.GetTakerFeeRate returns a non-zero fallback,
    /// the resulting EntryFeesUsdc must match qty * price * fallbackRate.
    /// </summary>
    [Fact]
    public void SetEmergencyCloseFees_WithUnknownExchangeName_PinsFeeRateFallbackContract()
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
        EmergencyCloseHandler.SetEmergencyCloseFees(position, legResult, "UnknownExchange");

        // Assert — compute expected using the same lookup the implementation uses
        var fallbackRate = Application.Common.ExchangeFeeConstants.GetTakerFeeRate("UnknownExchange");
        var expectedFee = 0.1m * 3000m * fallbackRate;

        position.EntryFeesUsdc.Should().Be(expectedFee,
            "EntryFeesUsdc must match qty * price * fallback rate for an unknown exchange");
    }
}
