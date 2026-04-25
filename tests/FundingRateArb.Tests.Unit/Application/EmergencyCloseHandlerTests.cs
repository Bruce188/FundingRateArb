using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
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
    private static EmergencyCloseHandler NewSut(ILogger<EmergencyCloseHandler>? logger = null) =>
        new(Mock.Of<IUnitOfWork>(), logger ?? NullLogger<EmergencyCloseHandler>.Instance);

    [Fact]
    public void SetEmergencyCloseFees_WhenFilledQuantityIsZero_FeesAndPnlAreZero()
    {
        var sut = NewSut();
        var position = new ArbitragePosition { UserId = "user1" };
        var legResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = 0m,
        };

        sut.SetEmergencyCloseFees(position, legResult, "Hyperliquid");

        position.EntryFeesUsdc.Should().Be(0m);
        position.ExitFeesUsdc.Should().Be(0m);
        position.RealizedPnl.Should().Be(0m,
            "RealizedPnl must be 0 (not null) when the zero-fill guard fires — " +
            "null is ambiguous and allows downstream callers to silently skip PnL accounting");
    }

    [Fact]
    public void SetEmergencyCloseFees_WhenFilledQuantityIsZero_ClearsPreExistingFees()
    {
        var sut = NewSut();
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

        sut.SetEmergencyCloseFees(position, legResult, "Hyperliquid");

        position.EntryFeesUsdc.Should().Be(0m);
        position.ExitFeesUsdc.Should().Be(0m);
        position.RealizedPnl.Should().Be(0m);
    }

    [Fact]
    public void SetEmergencyCloseFees_WhenFilledQuantityIsNegative_ZeroesFeesAndPnl_AndLogsError()
    {
        var mockLogger = new Mock<ILogger<EmergencyCloseHandler>>();
        var sut = NewSut(mockLogger.Object);
        var position = new ArbitragePosition { Id = 42, UserId = "user1" };
        var legResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = -0.05m,
        };

        sut.SetEmergencyCloseFees(position, legResult, "Hyperliquid");

        position.EntryFeesUsdc.Should().Be(0m);
        position.ExitFeesUsdc.Should().Be(0m);
        position.RealizedPnl.Should().Be(0m);

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

    [Fact]
    public void SetEmergencyCloseFees_WithUnknownExchangeName_PinsFeeRateFallbackContract()
    {
        var sut = NewSut();
        var position = new ArbitragePosition { UserId = "user1" };
        var legResult = new OrderResultDto
        {
            Success = true,
            FilledPrice = 3000m,
            FilledQuantity = 0.1m,
        };

        sut.SetEmergencyCloseFees(position, legResult, "UnknownExchange");

        var fallbackRate = Application.Common.ExchangeFeeConstants.GetTakerFeeRate("UnknownExchange");
        var expectedFee = 0.1m * 3000m * fallbackRate;

        position.EntryFeesUsdc.Should().Be(expectedFee);
    }
}
